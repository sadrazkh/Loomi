using Loomi.Models;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
namespace Loomi.BrowserAutomation;

public record ConnectionStatus(string State, bool Busy, string DesktopUrl = "/desktop/vnc.html?autoconnect=true&resize=scale&path=desktop/websockify");
public record BrowserResult(byte[] Image, string ConversationUrl);
public sealed class BrowserAutomationService(IOptions<BrowserOptions> options, IConfiguration config, IChromiumLauncher launcher) : IAsyncDisposable
{
    private readonly BrowserOptions settings = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IBrowserContext? context;
    private IPage? page;
    private string state = "Disconnected";
    private string Profile => Path.GetFullPath(Path.Combine(config["Storage:Root"] ?? "Storage", "profiles", "default"));
    public async Task<ConnectionStatus> StatusAsync()
    {
        if (!await gate.WaitAsync(0)) return new(state, true);
        try { await DetectAsync(); return new(state, false); }
        finally { gate.Release(); }
    }
    public async Task<ConnectionStatus> ConnectAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("BrowserBusy");
        try { await EnsureAsync(); await DetectAsync(); return new(state, false); }
        finally { gate.Release(); }
    }
    public async Task NewProjectAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("BrowserBusy");
        try { await EnsureAsync(); await NavigateAsync("https://chatgpt.com/"); }
        finally { gate.Release(); }
    }
    public async Task ResetAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("BrowserBusy");
        try
        {
            if (context != null) await context.CloseAsync();
            context = null; page = null; state = "Disconnected";
            if (Directory.Exists(Profile)) Directory.Delete(Profile, true);
        }
        finally { gate.Release(); }
    }
    private async Task EnsureAsync()
    {
        if (context != null && page is { IsClosed: false }) return;
        if (context != null) { try { await context.CloseAsync(); } catch (PlaywrightException) { } }
        Directory.CreateDirectory(Profile);
        context = await launcher.LaunchAsync(Profile, settings);
        context.SetDefaultTimeout(20000);
        context.SetDefaultNavigationTimeout(settings.NavigationTimeoutMs);
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await NavigateAsync("https://chatgpt.com/");
    }
    private async Task NavigateAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "chatgpt.com" || !(uri.AbsolutePath == "/" || uri.AbsolutePath.StartsWith("/c/")))
            throw new InvalidOperationException("InvalidConversation");
        for (var attempt = 0; ; attempt++)
        {
            try { await page!.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded }); return; }
            catch (PlaywrightException) when (attempt < 2) { await Task.Delay(1000 * (attempt + 1)); }
        }
    }
    private async Task DetectAsync()
    {
        try
        {
            if (page == null || page.IsClosed) { state = "Disconnected"; return; }
            if (await page.Locator(settings.Selectors.LoggedOut).First.IsVisibleAsync()) state = "LoginRequired";
            else if (await page.Locator(settings.Selectors.LoggedIn).First.IsVisibleAsync() && await page.Locator(settings.Selectors.Composer).First.IsVisibleAsync()) state = "Connected";
            else state = "LoginRequired";
        }
        catch (PlaywrightException) { state = "Disconnected"; }
    }
    public async Task<BrowserResult> RunAsync(Generation generation, string? parentImage, string? conversation, Func<RunStatus, Task> report, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await report(RunStatus.OpeningBrowser);
            await EnsureAsync();
            await report(RunStatus.OpeningChatGPT);
            await NavigateAsync(generation.Operation == Operation.Edit && conversation != null ? conversation : "https://chatgpt.com/");
            await page!.Locator(settings.Selectors.Composer).First.WaitForAsync();
            await DetectAsync();
            if (state != "Connected") throw new InvalidOperationException("LoginRequired");
            ct.ThrowIfCancellationRequested();
            if (parentImage != null)
            {
                var input = page.Locator(settings.Selectors.FileInput);
                if (await input.CountAsync() == 0) await page.Locator(settings.Selectors.AttachmentMenu).First.ClickAsync();
                await input.First.SetInputFilesAsync(parentImage);
                await page.Locator(settings.Selectors.UploadReady).First.WaitForAsync();
                await page.Locator(settings.Selectors.UploadBusy).First.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 60000 });
            }
            var before = await page.Locator(settings.Selectors.Assistant).CountAsync();
            await report(RunStatus.SendingPrompt);
            await page.Locator(settings.Selectors.Composer).First.FillAsync(generation.Prompt);
            // Never retry submission: a timeout after clicking may still have accepted the prompt.
            await page.Locator(settings.Selectors.Send).First.ClickAsync();
            await report(RunStatus.WaitingForResponse);
            var deadline = DateTime.UtcNow.AddSeconds(settings.GenerationTimeoutSeconds);
            string? previous = null;
            DateTime? stableSince = null;
            bool announced = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1500, ct);
                if (generation.ConversationUrl == null && Uri.TryCreate(page.Url, UriKind.Absolute, out var observed) && observed.Host == "chatgpt.com" && observed.AbsolutePath.StartsWith("/c/"))
                {
                    generation.ConversationUrl = page.Url;
                    await report(announced ? RunStatus.GeneratingImage : RunStatus.WaitingForResponse);
                }
                var replies = page.Locator(settings.Selectors.Assistant);
                if (await replies.CountAsync() <= before) continue;
                var latest = replies.Last;
                var image = latest.Locator(settings.Selectors.GeneratedImage).Last;
                if (await image.CountAsync() == 0 || !await image.IsVisibleAsync()) continue;
                if (!announced) { await report(RunStatus.GeneratingImage); announced = true; }
                var source = await image.EvaluateAsync<string>("el => el.complete && el.naturalWidth >= 256 && el.naturalHeight >= 256 ? el.currentSrc : ''");
                if (string.IsNullOrEmpty(source) || await page.Locator(settings.Selectors.Stop).First.IsVisibleAsync()) { stableSince = null; continue; }
                if (source != previous || stableSince == null) { previous = source; stableSince = DateTime.UtcNow; continue; }
                if ((DateTime.UtcNow - stableSince.Value).TotalSeconds < settings.StableSeconds) continue;
                await report(RunStatus.DownloadingImage);
                // Read only the rendered image resource in its browser origin; no private API calls.
                var base64 = await image.EvaluateAsync<string>("""
                    async el => {
                      const response = await fetch(el.currentSrc);
                      if (!response.ok) throw new Error('ImageDownloadFailed');
                      const blob = await response.blob();
                      if (blob.size > 40 * 1024 * 1024 || !blob.type.startsWith('image/')) throw new Error('InvalidImage');
                      return await new Promise((resolve, reject) => { const r = new FileReader(); r.onload = () => resolve(r.result.split(',')[1]); r.onerror = reject; r.readAsDataURL(blob); });
                    }
                    """);
                var url = page.Url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "chatgpt.com" || !uri.AbsolutePath.StartsWith("/c/")) throw new InvalidOperationException("ConversationNotSaved");
                return new(Convert.FromBase64String(base64), url);
            }
            throw new InvalidOperationException("GenerationTimeout");
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (context != null) await context.CloseAsync();
        gate.Dispose();
    }
}
