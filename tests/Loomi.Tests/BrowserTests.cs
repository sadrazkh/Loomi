using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Xunit;
namespace Loomi.Tests;
public class FixtureLauncher : IChromiumLauncher, IAsyncDisposable
{
    private readonly SemaphoreSlim launching = new(1, 1);
    private IPlaywright? playwright;
    public IBrowserContext Context => Contexts[^1];
    /// <summary>One per launch: a pool gives every account a browser of its own.</summary>
    public List<IBrowserContext> Contexts { get; } = [];
    public List<string> Visits { get; } = [];
    /// <summary>Replaces the ChatGPT stand-in, so tests can serve an interstitial instead.</summary>
    public string? Body { get; init; }
    /// <summary>How long any address under /slow/ takes to answer, so a page can be made to finish loading well after it has drawn.</summary>
    public int SlowMs { get; init; }
    /// <summary>The stand-in composer. Attachments accumulate across change events and each one renders its own remove button, which is how the real site behaves whether or not its input takes several files at once.</summary>
    public static string BodyWith(bool multiple) => $$"""
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea><input type="file" {{(multiple ? "multiple" : "")}} onchange="attach(this)">
        <div id="attachments"></div>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        window.attached = 0;
        function attach(input) {
          window.attached += input.files.length;
          document.querySelector('#attachments').innerHTML = Array.from({length: window.attached}, () => '<button aria-label="Remove file">Remove</button>').join('');
        }
        function send() {
          const prompt = document.querySelector('textarea').value;
          history.replaceState({},'', '/c/fixture-conversation');
          window.submission = {prompt, files:window.attached};
          if (prompt === 'timeout') return;
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          // A colour of its own for each answer, as the site never hands back the very same picture twice; a page kept from one run to the next
          // still shows the earlier ones.
          window.answers = (window.answers || 0) + 1;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = ['#7aa5cf', '#cf7a7a', '#7acf8f', '#cfc27a'][window.answers % 4]; ctx.fillRect(0,0,256,256);
          const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
          reply.append(image); document.body.append(reply);
        }
        </script></body></html>
        """;
    public async Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options)
    {
        await launching.WaitAsync();
        playwright ??= await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync();
        Contexts.Add(context);
        launching.Release();
        await context.RouteAsync("**/*", async route =>
        {
            Visits.Add(route.Request.Url);
            if (route.Request.Url.Contains("/slow/")) await Task.Delay(SlowMs);
            await route.FulfillAsync(new() { ContentType = "text/html", Body = Body ?? BodyWith(true) });
        });
        return context;
    }
    public async ValueTask DisposeAsync() { foreach (var open in Contexts) if (open.Browser is { } b) await b.CloseAsync(); playwright?.Dispose(); launching.Dispose(); }
}
public class BrowserTests
{
    [Fact]
    public async Task Generate_edit_branch_use_correct_conversations_and_parent_uploads()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-browser-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new FixtureLauncher();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Root"] = root }).Build();
            await using var service = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 20 }), config, launcher);
            Assert.Equal("Connected", (await service.ConnectAsync(default)).State);
            var statuses = new List<RunStatus>();
            var generated = await service.RunAsync(Operation.Generate, "initial", [], null, (s, _) => { statuses.Add(s); return Task.CompletedTask; }, default);
            Assert.StartsWith("https://chatgpt.com/c/", generated.ConversationUrl);
            Assert.True(generated.Image!.Length > 100);
            Assert.Contains(RunStatus.DownloadingImage, statuses);
            var path = Path.Combine(root, "parent.png"); await File.WriteAllBytesAsync(path, generated.Image);
            foreach (var operation in new[] { Operation.Edit, Operation.Branch })
            {
                launcher.Visits.Clear();
                var result = await service.RunAsync(operation, "change", [path], generated.ConversationUrl, (_, _) => Task.CompletedTask, default);
                // An edit goes on in the conversation, which the tab is already showing; a branch starts a fresh chat, here by loading the site.
                if (operation == Operation.Edit) { Assert.Equal(generated.ConversationUrl, result.ConversationUrl); Assert.DoesNotContain("https://chatgpt.com/", launcher.Visits); }
                else Assert.Equal("https://chatgpt.com/", launcher.Visits[0]);
                Assert.Equal(1, await launcher.Context.Pages[0].EvaluateAsync<int>("window.submission.files"));
                Assert.Equal("change", await launcher.Context.Pages[0].EvaluateAsync<string>("window.submission.prompt"));
            }
            await service.ResetAsync(default);
            Assert.Equal("Disconnected", (await service.StatusAsync()).State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Timeout_preserves_conversation_without_resubmitting()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-timeout-" + Guid.NewGuid());
        try
        {
            await using var launcher = new FixtureLauncher();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Root"] = root }).Build();
            await using var service = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, GenerationTimeoutSeconds = 2 }), config, launcher);
            string? conversation = null;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Operation.Generate, "timeout", [], null, (_, url) => { if (url != null) conversation = url; return Task.CompletedTask; }, default));
            Assert.Equal("GenerationTimeout", ex.Message);
            Assert.Equal("https://chatgpt.com/c/fixture-conversation", conversation);
            Assert.Equal("timeout", await launcher.Context.Pages[0].EvaluateAsync<string>("window.submission.prompt"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
