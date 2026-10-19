using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>On a server short of memory the kernel kills a ChatGPT tab while it waits for the picture. The site goes on and finishes it, so the run
/// reopens the conversation and takes the picture from there; before, it sat on the dead tab for its whole ten-minute timeout.</summary>
public class CrashRecoveryTests
{
    /// <summary>Sending moves to the conversation and draws nothing, as if the picture were still being made. Opened at that conversation's
    /// address, the page shows the turn finished, picture included, the way the site does once it has made it.</summary>
    private const string Body = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        function picture() {
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = '#7aa5cf'; ctx.fillRect(0, 0, 256, 256);
          const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
          reply.append(image); document.body.append(reply);
        }
        function send() { history.replaceState({}, '', '/c/fixture-conversation'); window.sent = true; }
        if (location.pathname.startsWith('/c/')) picture();
        </script></body></html>
        """;

    [Fact]
    public async Task A_tab_that_dies_while_the_picture_is_made_is_reopened_on_the_conversation_and_the_picture_taken_from_there()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-crash-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 40, ConversationLoadSeconds = 2 }), config, launcher);
        var run = session.RunAsync(Operation.Generate, "draw", [], null, (_, _) => Task.CompletedTask, CancellationToken.None);
        var tab = await WaitAsync(() => launcher.Contexts.Count > 0 && launcher.Context.Pages.Count > 0 ? launcher.Context.Pages[0] : null);
        await WaitAsync(async () => await tab.EvaluateAsync<bool>("() => window.sent === true"));
        var cdp = await launcher.Context.NewCDPSessionAsync(tab);
        _ = cdp.SendAsync("Page.crash");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result.Image);
        Assert.Equal("https://chatgpt.com/c/fixture-conversation", result.ConversationUrl);
    }

    [Fact]
    public async Task A_tab_that_dies_before_the_conversation_exists_fails_at_once_rather_than_waiting_out_the_timeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-crash-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        // Sending here stays on the home page, so there is no conversation to go back to.
        await using var launcher = new FixtureLauncher { Body = Body.Replace("history.replaceState({}, '', '/c/fixture-conversation'); ", "") };
        await using var session = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 60, ConversationLoadSeconds = 2 }), config, launcher);
        var run = session.RunAsync(Operation.Generate, "draw", [], null, (_, _) => Task.CompletedTask, CancellationToken.None);
        var tab = await WaitAsync(() => launcher.Contexts.Count > 0 && launcher.Context.Pages.Count > 0 ? launcher.Context.Pages[0] : null);
        await WaitAsync(async () => await tab.EvaluateAsync<bool>("() => window.sent === true"));
        _ = (await launcher.Context.NewCDPSessionAsync(tab)).SendAsync("Page.crash");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("BrowserCrashed", failure.Message);
    }

    [Fact]
    public async Task A_tab_that_stops_answering_is_taken_for_hung_and_reopened_on_the_conversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-crash-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        // After sending, the page's own thread spins for good, so every look at it waits with no answer: what a tab the kernel killed did on the server.
        await using var launcher = new FixtureLauncher { Body = Body.Replace("window.sent = true; }", "window.sent = true; setTimeout(() => { for (;;) {} }, 300); }") };
        await using var session = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 60, ConversationLoadSeconds = 2, HungSeconds = 3 }), config, launcher);
        var result = await session.RunAsync(Operation.Generate, "draw", [], null, (_, _) => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40));
        Assert.NotNull(result.Image);
        Assert.Equal("https://chatgpt.com/c/fixture-conversation", result.ConversationUrl);
    }

    private static async Task<T> WaitAsync<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 200; i++) { if (probe() is { } found) return found; await Task.Delay(100); }
        throw new TimeoutException("never appeared");
    }
    private static async Task WaitAsync(Func<Task<bool>> probe)
    {
        for (var i = 0; i < 200; i++) { try { if (await probe()) return; } catch (Microsoft.Playwright.PlaywrightException) { } await Task.Delay(100); }
        throw new TimeoutException("never happened");
    }
}
