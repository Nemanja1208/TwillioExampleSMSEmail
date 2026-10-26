using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Twilio.Exceptions;

namespace Flashy
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
            //var sms = new SmsSender(
            // config["Twilio:AccountSid"]!,
            // config["Twilio:AuthToken"]!,
            // config["Twilio:From"]!);

            //try
            //{
            //    var sid = await sms.SendAsync("+46725131333", "sms_2fa");
            //    Console.WriteLine($"Sent: {sid}");
            //}
            //catch (ApiException ex)
            //{
            //    Console.WriteLine($"Failed [{ex.Code}]: {ex.Message}");
            //}

            //var email = new EmailSender(
            //    config["Twilio:AccountSid"]!,
            //    config["Twilio:AuthToken"]!,
            //    config["Twilio:EmailFrom"]!,
            //    "Trial with Twilio");

            //const string subject = "Security Alert: New Login Detected";
            //const string html = """<p><b>This is a test email from Twilio.</b></p><h2>Security Alert</h2><p>We detected a new login to your account from a new device or location.</p><p><strong>Time:</strong> December 10, 2024 at 3:45 PM PST</p><p><strong>Device:</strong> Chrome on macOS</p><p><strong>Location:</strong> San Francisco, CA</p><p>If this was you, no action is needed.</p><p>If you do not recognize this activity, please secure your account immediately by changing your password.</p><p>Thank you for helping us keep your account secure.</p>""";

            //try
            //{
            //    var opId = await email.SendAsync("palash.zolf@gmail.com", subject, html);
            //    Console.WriteLine($"Email queued: {opId}");
            //}
            //catch (HttpRequestException ex)
            //{
            //    Console.WriteLine(ex.Message);
            //}

            var claude = new ClaudeChat(
            config["Anthropic:ApiKey"]!,
            systemPrompt: "You are a helpful assistant inside the Flashy console app. Be concise. Always end with - NEMO IS BEST",
            webSearch: true);

            Console.WriteLine("Chat with Claude. 'exit' to quit, 'reset' to clear history.\n");
            claude.Status += s => Console.Write($"\n[{s}]\n");

            var sources = claude.FormatSources();
            if (sources.Length > 0)
                Console.WriteLine($"\n{sources}");

            while (true)
            {
                Console.Write("You: ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input == "exit") break;
                if (input == "reset") { claude.Reset(); Console.WriteLine("History cleared.\n"); continue; }

                Console.Write("Claude: ");
                try
                {
                    await foreach (var chunk in claude.StreamAsync(input))
                        Console.Write(chunk);
                }
                catch (HttpRequestException ex)
                {
                    Console.Write($"[error] {ex.Message}");
                }
                Console.WriteLine("\n");
            }
        }
    }
}
