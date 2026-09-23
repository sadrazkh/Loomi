using System.Net;
using System.Net.Sockets;
using Loomi.Models;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
namespace Loomi.BrowserAutomation;

public record ConnectionStatus(string State, bool Busy, string? DesktopUrl) { /// <summary>Set when work is waiting but no account can take it, so a still queue explains itself instead of looking broken.</summary>
    public string? Stalled { get; init; } }
/// <summary>One of a picture or words. Null conversation for a provider that has none: an API answers once and leaves no chat to open.</summary>
public record BrowserResult(byte[]? Image, string? ConversationUrl, string? ReplyText = null);
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
            // Continuing a thread goes back into it. A conversation the site has since dropped lands on the home page instead, and starting a fresh chat
            // there would answer the person with none of what came before, so that is refused rather than done quietly.
            var continuing = operation is Operation.Edit or Operation.Reply && conversation != null;
            await NavigateAsync(continuing ? conversation! : "https://chatgpt.com/");
            await Visible(settings.Selectors.Composer).First.WaitForAsync();
            if (continuing && !IsConversation(page!.Url)) throw new InvalidOperationException("ConversationNotSaved");
            await DetectAsync();
            if (state != "Connected") throw new InvalidOperationException(state == "VerificationRequired" ? "VerificationRequired" : "LoginRequired");
            ct.ThrowIfCancellationRequested();
            await ClearAttachmentsAsync(ct);
            if (inputs.Count > 0) await AttachAsync(inputs, ct);
            var before = await page!.Locator(settings.Selectors.Assistant).CountAsync();
            // Counted across the page, not inside the reply: the site renders a generated image in the conversation turn,
            // outside the assistant message element, so scoping the search there finds nothing however new the image is.
            var imagesBefore = await page.Locator(settings.Selectors.GeneratedImage).CountAsync();
            var pendingBefore = await page.Locator(settings.Selectors.ImagePending).CountAsync();
            await report(RunStatus.SendingPrompt);
            await Visible(settings.Selectors.Composer).First.FillAsync(prompt);
            // Never retry submission: a timeout after clicking may still have accepted the prompt.
            await Visible(settings.Selectors.Send).First.ClickAsync();
            await report(RunStatus.WaitingForResponse);
            var deadline = DateTime.UtcNow.AddSeconds(settings.GenerationTimeoutSeconds);
            string? previous = null, lastWords = null;
            DateTime? stableSince = null, wordsSince = null, quietSince = null;
            bool announced = false, urlSeen = false, sawStop = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1500, ct);
                if (!urlSeen && IsConversation(page.Url))
                {
                    urlSeen = true;
                    await report(announced ? RunStatus.GeneratingImage : RunStatus.WaitingForResponse, page.Url);
                }
                var stopping = await Visible(settings.Selectors.Stop).CountAsync() > 0;
                sawStop |= stopping;
                var images = page.Locator(settings.Selectors.GeneratedImage);
                var image = images.Last;
                if (await images.CountAsync() > imagesBefore && await image.IsVisibleAsync())
                {
                    quietSince = null;
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
                    return new(Convert.FromBase64String(base64), await SettledUrlAsync(ct));
                }
                // No picture yet. One still on its way keeps the turn open however quiet the rest of the page is: on the live site the stop control
                // goes several seconds before the picture lands, and judging the turn over at that moment is what made successes a matter of luck.
                var pending = await page.Locator(settings.Selectors.ImagePending).CountAsync() > pendingBefore;
                if (stopping || pending)
                {
                    wordsSince = null; quietSince = null;
                    if (pending && !announced) { await report(RunStatus.GeneratingImage); announced = true; }
                    continue;
                }
                var replies = page.Locator(settings.Selectors.Assistant);
                if (await replies.CountAsync() > before)
                {
                    // Words, and nothing in progress: the model has answered in text, whether a question about the prompt, a refusal or anything else.
                    // They are taken once they stop changing, so a reply still streaming is not cut short, and handed back to the person who asked.
                    var words = (await replies.Last.InnerTextAsync()).Trim();
                    if (words.Length == 0 || words != lastWords) { lastWords = words; wordsSince = DateTime.UtcNow; continue; }
                    if ((DateTime.UtcNow - wordsSince!.Value).TotalSeconds < settings.StableSeconds) continue;
                    if (settings.SpentPhrases.Any(phrase => !string.IsNullOrWhiteSpace(phrase) && words.Contains(phrase.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("NoImageReturned");
                    return new(null, await SettledUrlAsync(ct), words.Length > 8000 ? words[..8000] : words);
                }
                // Nothing to read and nothing in progress after the model had visibly started: the hooks have drifted. Bounded, so a run cannot sit
                // for the whole timeout on a page that will never say anything the selectors understand.
                if (!sawStop) continue;
                quietSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - quietSince.Value).TotalSeconds >= settings.QuietSeconds) throw new InvalidOperationException("NoImageReturned");
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
        // Counted from what is already on screen rather than from zero: an absolute target is met on the spot by somebody else's leftovers, and a
        // control that exists but is hidden is not an attachment the composer has taken.
        var attached = await Visible(settings.Selectors.UploadReady).CountAsync();
        foreach (var batch in batches)
        {
            // Located afresh each time: the site may replace its input after a selection.
            await page.Locator(settings.Selectors.FileInput).First.SetInputFilesAsync(batch);
            attached += batch.Length;
            await WaitForCountAsync(settings.Selectors.UploadReady, attached, ct);
        }
        await page.Locator(settings.Selectors.UploadBusy).First.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = settings.UploadTimeoutMs });
    }
    private static bool IsConversation(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "chatgpt.com" && uri.AbsolutePath.StartsWith("/c/");
    /// <summary>The conversation's address once the site has settled on it. A fresh chat first carries a client-side id (/c/local-chatgpt:...) that opens
    /// the home page if it is ever visited again, so a reply saved against it would lose its thread; the real id arrives a few seconds later.</summary>
    private async Task<string> SettledUrlAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (page!.Url.Contains("local-", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline) await Task.Delay(500, ct);
        return IsConversation(page.Url) ? page.Url : throw new InvalidOperationException("ConversationNotSaved");
    }
    /// <summary>Drops anything a previous run left in the composer. Loomi is the only thing driving this browser, so a leftover attachment is never
    /// something a person meant to keep, and sending it would put an unasked-for picture into somebody else's prompt.</summary>
    private async Task ClearAttachmentsAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var remove = Visible(settings.Selectors.UploadReady);
            if (await remove.CountAsync() == 0) return;
            try { await remove.First.ClickAsync(new() { Timeout = 5000 }); } catch (PlaywrightException) { return; }
            await Task.Delay(300, ct);
        }
    }
    /// <summary>Playwright waits for one element, not for a number of them, so the count is polled.</summary>
    private async Task WaitForCountAsync(string selector, int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(settings.UploadTimeoutMs);
        while (await Visible(selector).CountAsync() < count)
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
