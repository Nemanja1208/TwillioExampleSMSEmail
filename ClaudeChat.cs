using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Flashy;

public sealed class ClaudeChat
{
    private const string ResearchRules = """

        When you search the web:
        - Prefer primary, authoritative sources (official docs, vendor and government sites, reputable news) over forums, aggregators and SEO pages.
        - Prefer the most recent information; check page dates.
        - If sources conflict, say so and explain which one you trust and why.
        - Only state facts supported by the results. If you can't verify something, say so instead of guessing.
        - If results are vague, search again with a more specific query.
        - Lead with the direct answer, then a short explanation.
        """;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly int _maxTokens;
    private readonly JsonArray? _tools;
    private readonly List<JsonObject> _history = [];
    private readonly List<Source> _sources = [];

    /// <summary>Status updates while Claude uses tools, e.g. "searching: .NET 10 release date".</summary>
    public event Action<string>? Status;

    /// <summary>Sources Claude cited in the last answer.</summary>
    public IReadOnlyList<Source> LastSources => _sources;

    public ClaudeChat(
        string apiKey,
        string model = "claude-sonnet-5-5",
        string? systemPrompt = null,
        int maxTokens = 4096,
        bool webSearch = false,
        int maxSearchesPerTurn = 5,
        string[]? allowedDomains = null,
        string? country = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        _model = model;
        _maxTokens = maxTokens;
        _systemPrompt = (systemPrompt ?? "You are a helpful assistant.") + (webSearch ? ResearchRules : "");

        if (!webSearch) return;

        // web_search_20260318 = dynamic filtering: Claude filters results with code
        // before they reach its context, so it isn't drowning in irrelevant pages
        var tool = new JsonObject
        {
            ["type"] = "web_search_20260318",
            ["name"] = "web_search",
            ["max_uses"] = maxSearchesPerTurn
        };
        if (allowedDomains is { Length: > 0 })
            tool["allowed_domains"] = new JsonArray(allowedDomains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
        if (country is not null)
            tool["user_location"] = new JsonObject { ["type"] = "approximate", ["country"] = country };

        _tools = new JsonArray(tool);
    }

    /// <summary>Streams Claude's reply and keeps full history, including search results.</summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _sources.Clear();
        var turn = new List<JsonObject> { new() { ["role"] = "user", ["content"] = userMessage } };

        // Long search turns can return "pause_turn": send the partial answer back and let Claude continue
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,
                ["system"] = _systemPrompt,
                ["messages"] = new JsonArray(_history.Concat(turn).Select(m => (JsonNode?)m.DeepClone()).ToArray()),
                ["stream"] = true
            };
            if (_tools is not null) body["tools"] = _tools.DeepClone();

            using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Claude API failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var blocks = new SortedDictionary<int, JsonObject>();
            var buffers = new Dictionary<int, StringBuilder>();
            string? stopReason = null;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                var evt = JsonNode.Parse(line["data: ".Length..])!;

                switch ((string?)evt["type"])
                {
                    case "content_block_start":
                        blocks[(int)evt["index"]!] = evt["content_block"]!.DeepClone().AsObject();
                        break;

                    case "content_block_delta":
                        {
                            var i = (int)evt["index"]!;
                            var delta = evt["delta"]!;
                            if (!buffers.TryGetValue(i, out var buffer)) buffers[i] = buffer = new StringBuilder();

                            switch ((string?)delta["type"])
                            {
                                case "text_delta":
                                    var text = (string)delta["text"]!;
                                    buffer.Append(text);
                                    yield return text;
                                    break;

                                case "input_json_delta":
                                    buffer.Append((string?)delta["partial_json"]);
                                    break;

                                case "citations_delta":
                                    if (blocks[i]["citations"] is not JsonArray citations)
                                        blocks[i]["citations"] = citations = new JsonArray();
                                    citations.Add(delta["citation"]!.DeepClone());
                                    break;
                            }
                            break;
                        }

                    case "content_block_stop":
                        FinalizeBlock(blocks[(int)evt["index"]!], buffers.GetValueOrDefault((int)evt["index"]!));
                        break;

                    case "message_delta":
                        stopReason = (string?)evt["delta"]?["stop_reason"] ?? stopReason;
                        break;

                    case "error":
                        throw new HttpRequestException($"Claude stream error: {(string?)evt["error"]?["message"]}");
                }
            }

            // Keep the exact content blocks (incl. encrypted search results + citations).
            // Saving only the text is what made follow-up questions lose context and hallucinate.
            var assistant = turn.FirstOrDefault(m => (string?)m["role"] == "assistant");
            if (assistant is null)
                turn.Add(assistant = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray() });

            var content = assistant["content"]!.AsArray();
            foreach (var block in blocks.Values.Where(b => (string?)b["type"] != "text" || !string.IsNullOrEmpty((string?)b["text"])))
                content.Add(block);

            if (stopReason != "pause_turn") break;
        }

        _history.AddRange(turn);
    }

    /// <summary>Cited sources from the last answer as ready-to-print text (empty if none).</summary>
    public string FormatSources() =>
        _sources.Count == 0
            ? ""
            : "Sources:\n" + string.Join("\n", _sources.Select(s => $"  - {s.Title}: {s.Url}"));

    public void Reset() => _history.Clear();

    private void FinalizeBlock(JsonObject block, StringBuilder? buffer)
    {
        var type = (string?)block["type"];

        if (type == "text")
        {
            if (buffer is not null) block["text"] = buffer.ToString();

            if (block["citations"] is JsonArray citations)
                foreach (var c in citations)
                {
                    var url = (string?)c?["url"];
                    if (url is not null && _sources.All(s => s.Url != url))
                        _sources.Add(new Source((string?)c?["title"] ?? url, url));
                }
            return;
        }

        if (block.ContainsKey("input") && buffer is { Length: > 0 })
            block["input"] = JsonNode.Parse(buffer.ToString());

        if (type == "server_tool_use")
        {
            var name = (string?)block["name"];
            Status?.Invoke(name == "web_search"
                ? $"searching: {(string?)block["input"]?["query"]}"
                : $"running {name}");
        }
    }
}

public sealed record Source(string Title, string Url);