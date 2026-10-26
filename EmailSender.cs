using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flashy;

public sealed class EmailSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _fromAddress;
    private readonly string _fromName;

    public EmailSender(string accountSid, string authToken, string fromAddress, string? fromName = null)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://comms.twilio.com/v1/") };

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        _fromAddress = fromAddress;
        _fromName = fromName ?? fromAddress;
    }

    /// <param name="html">Required by Twilio. The HTML body.</param>
    /// <param name="text">Optional. If omitted, Twilio generates plain text from the HTML.</param>
    public async Task<string> SendAsync(string to, string subject, string html, string? text = null)
    {
        var payload = new
        {
            from = new { address = _fromAddress, name = _fromName },
            to = new[] { new { address = to } },
            content = new { subject, html, text }
        };

        using var response = await _http.PostAsJsonAsync("Emails", payload, Json);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Email failed ({(int)response.StatusCode}): {body}");

        // 202 Accepted: sending is async, operationId lets you track status
        return JsonSerializer.Deserialize<SendResult>(body, Json)!.OperationId;
    }

    private sealed record SendResult(string OperationId);
}