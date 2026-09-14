using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
public class GenerationOutcomeTests
{
    /// <summary>Signed in, answers, and never produces an image — what an account that has hit its image limit looks like.</summary>
    private const string TextOnlyReply = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        function send() {
          history.replaceState({},'', '/c/text-only-conversation');
          const reply = document.createElement('div');
          reply.dataset.messageAuthorRole = 'assistant';
          reply.textContent = 'You have reached your image generation limit.';
          document.body.append(reply);
        }
        </script></body></html>
        """;

    [Fact]
    public async Task A_finished_reply_without_an_image_fails_fast_instead_of_waiting_out_the_timeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-outcome-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        await using var launcher = new FixtureLauncher { Body = TextOnlyReply };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        var options = new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 120 };
        await using var session = new BrowserSession("default", Options.Create(options), config, launcher);
        var started = DateTime.UtcNow;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(new Generation { Prompt = "a red circle" }, null, null, _ => Task.CompletedTask, default));
        Assert.Equal("NoImageReturned", failure.Message);
        // The point of the change: it must not sit until GenerationTimeoutSeconds.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), $"gave up after {(DateTime.UtcNow - started).TotalSeconds:0}s");
        Directory.Delete(root, true);
    }
}
