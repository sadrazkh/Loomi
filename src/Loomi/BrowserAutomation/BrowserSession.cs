using System.Net;
using System.Net.Sockets;
using Loomi.Models;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
namespace Loomi.BrowserAutomation;

public record ConnectionStatus(string State, bool Busy, string? DesktopUrl) { /// <summary>Set when work is waiting but no account can take it, so a still queue explains itself instead of looking broken.</summary>
    public string? Stalled { get; init; } }
/// <summary>Null conversation for a provider that has none: an API answers once and leaves no chat to open.</summary>
public record BrowserResult(byte[] Image, string? ConversationUrl);
/// <summary>Reports progress and, when first seen, the conversation URL, so a run that later fails still leaves the chat on the row.</summary>
public delegate Task ReportStatus(RunStatus status, string? conversation = null);
/// <summary>One ChatGPT account's browser: its own profile, page and gate, so two accounts never wait on each other.</summary>
public sealed class BrowserSession(string directory, IOptions<BrowserOptions> options, IConfiguration config, IChromiumLauncher launcher) : IAsyncDisposable
{
    private readonly BrowserOptions settings = options.Value;
    private readonly string profile = ProfilePath(config["Storage:Root"] ?? "Storage", directory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IBrowserContext? context;
    private IPage? page;
    private string state = "Disconnected";
    public bool Busy => gate.CurrentCount == 0;
    /// <summary>The last state observed, without touching the page, so listing accounts costs no browser traffic.</summary>
    public string State => page is null or { IsClosed: true } ? "Disconnected" : state;
    /// <summary>Resolved the way stored images are: a crafted directory must not reach outside the profile root.</summary>
    public static string ProfilePath(string storageRoot, string directory)
    {
        var root = Path.GetFullPath(Path.Combine(storageRoot, "profiles"));
        var full = Path.GetFullPath(Path.Combine(root, directory));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("InvalidPath");
        return full;
    }
    public async Task<ConnectionStatus> StatusAsync()
    {
        if (!await gate.WaitAsync(0)) return new(state, true, await DesktopUrlAsync());
        try { await DetectAsync(); return new(state, false, await DesktopUrlAsync()); }
        finally { gate.Release(); }
    }
    public async Task<ConnectionStatus> ConnectAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("BrowserBusy");
        try { await EnsureAsync(); await DetectAsync(); return new(state, false, await DesktopUrlAsync()); }
        finally { gate.Release(); }
    }
    /// <summary>Null when no noVNC service answers, so the UI can point at the desktop window instead of a dead proxy route.</summary>
    public static async Task<string?> DesktopUrlAsync(BrowserOptions settings)
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await probe.ConnectAsync(IPAddress.Loopback, settings.DesktopPort, timeout.Token);
            return "/desktop/vnc.html?autoconnect=true&resize=scale&path=desktop/websockify";
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return null; }
    }
    private Task<string?> DesktopUrlAsync() => DesktopUrlAsync(settings);
    /// <summary>Only the matches that are actually on screen. The site renders some controls twice — a sidebar copy beside a header copy — and just one
    /// of them is visible; taking the first in DOM order picks the hidden one, which reads as "not signed in" however well the owner had logged in.
    /// File inputs are deliberately not filtered this way: theirs is hidden by design and still accepts files.</summary>
    private ILocator Visible(string selector) => page!.Locator(selector).Filter(new() { Visible = true });
    public async Task ResetAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("BrowserBusy");
        try
        {
            if (context != null) await context.CloseAsync();
            context = null; page = null; state = "Disconnected";
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
        }
        finally { gate.Release(); }
    }
    private async Task EnsureAsync()
    {
        if (context != null && page is { IsClosed: false }) return;
        if (context != null) { try { await context.CloseAsync(); } catch (PlaywrightException) { } }
        Directory.CreateDirectory(profile);
        context = await launcher.LaunchAsync(profile, settings);
        context.SetDefaultTimeout(20000);
        context.SetDefaultNavigationTimeout(settings.NavigationTimeoutMs);
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await NavigateAsync("https://chatgpt.com/");
        await SettleAsync();
    }
    /// <summary>The site renders after DOMContentLoaded, so deciding immediately reports a login prompt that is not there. The composer is one of the
    /// signals waited for, not the profile button: the button appears first, and settling on it let the very first answer be "login required" on a
    /// session that was signed in perfectly well and only needed another second.</summary>
    private async Task SettleAsync()
    {
        var signals = $"{settings.Selectors.Challenge}, {settings.Selectors.LoggedOut}, {settings.Selectors.Composer}";
        try { await Visible(signals).First.WaitForAsync(new() { Timeout = settings.SettleTimeoutMs }); }
        catch (TimeoutException) { }
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
            if (await Visible(settings.Selectors.Challenge).CountAsync() > 0) state = "VerificationRequired";
            else if (await Visible(settings.Selectors.LoggedOut).CountAsync() > 0) state = "LoginRequired";
            else if (await Visible(settings.Selectors.LoggedIn).CountAsync() > 0 && await Visible(settings.Selectors.Composer).CountAsync() > 0) state = "Connected";
            else state = "LoginRequired";
        }
        catch (PlaywrightException) { state = "Disconnected"; }
    }
    public async Task<BrowserResult> RunAsync(Operation operation, string prompt, IReadOnlyList<string> inputs, string? conversation, ReportStatus report, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await report(RunStatus.OpeningBrowser);
            await EnsureAsync();
            await report(RunStatus.OpeningChatGPT);
            await NavigateAsync(operation == Operation.Edit && conversation != null ? conversation : "https://chatgpt.com/");
            await Visible(settings.Selectors.Composer).First.WaitForAsync();
            await DetectAsync();
            if (state != "Connected") throw new InvalidOperationException(state == "VerificationRequired" ? "VerificationRequired" : "LoginRequired");
            ct.ThrowIfCancellationRequested();
            if (inputs.Count > 0) await AttachAsync(inputs, ct);
            var before = await page.Locator(settings.Selectors.Assistant).CountAsync();
            // Counted across the page, not inside the reply: the site renders a generated image in the conversation turn,
            // outside the assistant message element, so scoping the search there finds nothing however new the image is.
            var imagesBefore = await page.Locator(settings.Selectors.GeneratedImage).CountAsync();
            await report(RunStatus.SendingPrompt);
            await Visible(settings.Selectors.Composer).First.FillAsync(prompt);
            // Never retry submission: a timeout after clicking may still have accepted the prompt.
            await Visible(settings.Selectors.Send).First.ClickAsync();
            await report(RunStatus.WaitingForResponse);
            var deadline = DateTime.UtcNow.AddSeconds(settings.GenerationTimeoutSeconds);
            string? previous = null;
            DateTime? stableSince = null, emptySince = null;
            bool announced = false, urlSeen = false, sawStop = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1500, ct);
                if (!urlSeen && Uri.TryCreate(page.Url, UriKind.Absolute, out var observed) && observed.Host == "chatgpt.com" && observed.AbsolutePath.StartsWith("/c/"))
                {
                    urlSeen = true;
                    await report(announced ? RunStatus.GeneratingImage : RunStatus.WaitingForResponse, page.Url);
                }
                var stopping = await Visible(settings.Selectors.Stop).CountAsync() > 0;
                sawStop |= stopping;
                // The site no longer marks a message with its author, so a turn is known to be over the way a person sees it: the stop control appeared
                // while the model worked and has gone again. The old marker is still honoured wherever it exists.
                var replied = await page.Locator(settings.Selectors.Assistant).CountAsync() > before || (sawStop && !stopping);
                var images = page.Locator(settings.Selectors.GeneratedImage);
                var image = images.Last;
                if (await images.CountAsync() <= imagesBefore || !await image.IsVisibleAsync())
                {
                    // A reply that has finished and brought no new image is an answer, not a delay: an account limit,
                    // a refusal, or plain text. Waiting out the timeout tells the caller nothing it can act on.
                    if (!replied || stopping) { emptySince = null; continue; }
                    emptySince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - emptySince.Value).TotalSeconds < settings.StableSeconds) continue;
                    throw new InvalidOperationException("NoImageReturned");
                }
                emptySince = null;
                if (!announced) { await report(RunStatus.GeneratingImage); announced = true; }
                var source = await image.EvaluateAsync<string>("el => el.complete && el.naturalWidth >= 256 && el.naturalHeight >= 256 ? el.currentSrc : ''");
                if (string.IsNullOrEmpty(source) || stopping) { stableSince = null; continue; }
                if (source != previous || stableSince == null) { previous = source; stableSince = DateTime.UtcNow; continue; }
                if ((DateTime.UtcNow - stableSince.Value).TotalSeconds < settings.StableSeconds) continue;
                await report(RunStatus.DownloadingImage);
                // Read only the rendered image resource in its browser origin; no private API calls.
                var base64 = await image.EvaluateAsync<string>("""
                    async el => {
                      const src = el.currentSrc || el.src;
                      // The site hands the finished picture over inline, and its own content policy refuses a fetch of a data URL from this page.
                      // The bytes are already here, so they are read straight off the attribute instead of asked for again.
                      if (src.startsWith('data:')) {
                        const comma = src.indexOf(',');
                        const meta = src.slice(5, comma);
                        if (!meta.startsWith('image/') || !meta.includes('base64')) throw new Error('InvalidImage');
                        const data = src.slice(comma + 1);
                        if (data.length * 3 / 4 > 40 * 1024 * 1024) throw new Error('InvalidImage');
                        return data;
                      }
                      const response = await fetch(src);
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
    /// <summary>Sends the files through the composer's own input. One that takes several at once gets them in a single call; one that does not gets them one by one,
    /// the site adding each to its attachments. Either way the run waits until every file shows its remove control and nothing is still uploading.</summary>
    private async Task AttachAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var input = page!.Locator(settings.Selectors.FileInput);
        if (await input.CountAsync() == 0) await page.Locator(settings.Selectors.AttachmentMenu).First.ClickAsync();
        var multiple = await input.First.EvaluateAsync<bool>("el => el.multiple");
        List<string[]> batches = multiple ? [inputs.ToArray()] : [.. inputs.Select(path => new[] { path })];
        var attached = 0;
        foreach (var batch in batches)
        {
            // Located afresh each time: the site may replace its input after a selection.
            await page.Locator(settings.Selectors.FileInput).First.SetInputFilesAsync(batch);
            attached += batch.Length;
            await WaitForCountAsync(settings.Selectors.UploadReady, attached, ct);
        }
        await page.Locator(settings.Selectors.UploadBusy).First.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = settings.UploadTimeoutMs });
    }
    /// <summary>Playwright waits for one element, not for a number of them, so the count is polled.</summary>
    private async Task WaitForCountAsync(string selector, int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(settings.UploadTimeoutMs);
        while (await page!.Locator(selector).CountAsync() < count)
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("UploadFailed");
            await Task.Delay(250, ct);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (context != null) await context.CloseAsync();
        gate.Dispose();
    }
}
