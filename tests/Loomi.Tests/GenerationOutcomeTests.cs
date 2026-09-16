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

    /// <summary>What the live site does: the reply element is empty and the generated image sits beside it in the conversation turn.</summary>
    private const string ImageOutsideTheReply = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        function send() {
          history.replaceState({},'', '/c/turn-conversation');
          const turn = document.createElement('div'); turn.dataset.testid = 'conversation-turn-2';
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          reply.textContent = 'Here it is.';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = '#c05050'; ctx.fillRect(0,0,256,256);
          const image = document.createElement('img');
          image.alt = 'Generated image: a red square'; image.src = canvas.toDataURL('image/png');
          turn.append(reply, image); document.body.append(turn);
        }
        </script></body></html>
        """;

    [Fact]
    public async Task An_image_rendered_outside_the_reply_is_still_found()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-turn-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        await using var launcher = new FixtureLauncher { Body = ImageOutsideTheReply };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        var options = new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 60 };
        await using var session = new BrowserSession("default", Options.Create(options), config, launcher);
        var result = await session.RunAsync(Operation.Generate, "a red square", [], null, (_, _) => Task.CompletedTask, default);
        Assert.True(result.Image.Length > 100);
        Assert.EndsWith("/c/turn-conversation", result.ConversationUrl);
        Directory.Delete(root, true);
    }

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
            () => session.RunAsync(Operation.Generate, "a red circle", [], null, (_, _) => Task.CompletedTask, default));
        Assert.Equal("NoImageReturned", failure.Message);
        // The point of the change: it must not sit until GenerationTimeoutSeconds.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), $"gave up after {(DateTime.UtcNow - started).TotalSeconds:0}s");
        Directory.Delete(root, true);
    }
}
