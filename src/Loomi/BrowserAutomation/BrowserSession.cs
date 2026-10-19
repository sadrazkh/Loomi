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
public sealed class BrowserSession(string directory, IOptions<BrowserOptions> options, IConfiguration config, IChromiumLauncher launcher, ILogger? logger = null) : IAsyncDisposable
{
    private readonly BrowserOptions settings = options.Value;
    private readonly string profile = ProfilePath(config["Storage:Root"] ?? "Storage", directory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IBrowserContext? context;
    private IPage? page;
    private string state = "Disconnected";
    /// <summary>Set when the tab's renderer dies, which on a server short of memory is the kernel killing it. A crashed tab is not closed, so without
    /// this every check on it simply finds nothing and the run waited out its whole timeout while the picture sat finished in the conversation.</summary>
    private volatile bool crashed;
    private const int MaxRecoveries = 2;
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
    /// <summary>A crashed tab is closed at once. On the live server a call made to a tab the kernel had killed never returned at all, and the run, and
    /// the account's every request after it, hung there; closing the tab fails those calls, so the run can reopen the conversation instead.</summary>
    private void Watch(IPage tab) => tab.Crash += (_, _) => Abandon(tab, "The ChatGPT tab crashed; the server is most likely short of memory");
    private void Abandon(IPage tab, string why)
    {
        if (crashed) return;
        crashed = true;
        logger?.LogWarning("{Why}", why);
        _ = Task.Run(async () => { try { await tab.CloseAsync(); } catch (PlaywrightException) { /* gone already */ } });
    }
    /// <summary>A fresh tab in the same browser, signed in as before, in place of one that crashed.</summary>
    private async Task ReplaceTabAsync()
    {
        try { await page!.CloseAsync(); } catch (PlaywrightException) { /* already gone with its renderer */ }
        page = await context!.NewPageAsync();
        Watch(page);
        crashed = false;
    }
    private async Task EnsureAsync()
    {
        if (context != null && page is { IsClosed: false } && crashed)
        {
            await ReplaceTabAsync();
            await NavigateAsync("https://chatgpt.com/");
            await SettleAsync();
            return;
        }
        if (context != null && page is { IsClosed: false }) return;
        if (context != null) { try { await context.CloseAsync(); } catch (PlaywrightException) { } }
        Directory.CreateDirectory(profile);
        context = await launcher.LaunchAsync(profile, settings);
        context.SetDefaultTimeout(settings.ActionTimeoutMs);
        context.SetDefaultNavigationTimeout(settings.NavigationTimeoutMs);
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        Watch(page);
        crashed = false;
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
    private async Task NavigateAsync(string url, float? timeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "chatgpt.com" || !(uri.AbsolutePath == "/" || uri.AbsolutePath.StartsWith("/c/")))
            throw new InvalidOperationException("InvalidConversation");
        for (var attempt = 0; ; attempt++)
        {
            try { await page!.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout }); return; }
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
            // While the model waits for an answer the site takes the composer away and shows its question instead; that page is just as signed in.
            else if (await Visible(settings.Selectors.LoggedIn).CountAsync() > 0 && await Visible($"{settings.Selectors.Composer}, {settings.Selectors.QuestionForm}").CountAsync() > 0) state = "Connected";
            else state = "LoginRequired";
        }
        catch (PlaywrightException) { state = "Disconnected"; }
    }
    public async Task<BrowserResult> RunAsync(Operation operation, string prompt, IReadOnlyList<string> inputs, string? conversation, ReportStatus report, CancellationToken ct, IReadOnlySet<string>? knownImages = null)
    {
        await gate.WaitAsync(ct);
        var step = "opening the browser";
        var clock = new RunClock();
        // One line per finished run saying where its time went, so "it is slow" can be answered from the log rather than guessed at.
        BrowserResult Done(BrowserResult result)
        {
            clock.Mark("handed over");
            logger?.LogInformation("Run timings ({Operation}, {Outcome}): {Timings}", operation, result.Image != null ? "image" : "words", clock);
            return result;
        }
        try
        {
            await report(RunStatus.OpeningBrowser);
            await EnsureAsync();
            clock.Mark("browser");
            await report(RunStatus.OpeningChatGPT);
            // Continuing a thread goes back into it. A conversation the site has since dropped lands on the home page instead, and starting a fresh chat
            // there would answer the person with none of what came before, so that is refused rather than done quietly.
            var continuing = operation is Operation.Edit or Operation.Reply && conversation != null;
            step = continuing ? "opening the conversation" : "opening ChatGPT";
            // The tab is used where it stands when it already shows what this run needs: the same conversation for a follow-up, or a fresh chat one
            // click away for a new one. Loading the page again took 10 to 16 seconds for the home page and 25 to 55 for a conversation, which the site
            // fetches whole, earlier pictures and all; a follow-up to the picture just made paid that every time for a page that was already there.
            var kept = continuing ? await ShowsConversationAsync(conversation!) : await FreshChatInPlaceAsync(ct);
            if (!kept)
            {
                // One allowance for the whole opening, the address and the box together, so a slow load is not granted it twice.
                var allowance = continuing ? settings.ConversationOpenSeconds * 1000 : settings.NavigationTimeoutMs;
                var opened = DateTime.UtcNow.AddMilliseconds(allowance);
                await NavigateAsync(continuing ? conversation! : "https://chatgpt.com/", allowance);
                step = "waiting for the message box";
                await Visible($"{settings.Selectors.Composer}, {settings.Selectors.QuestionForm}").First.WaitForAsync(new() { Timeout = (float)Math.Max(1000, (opened - DateTime.UtcNow).TotalMilliseconds) });
            }
            clock.Mark(kept ? "page kept" : "page loaded");
            if (continuing && !IsConversation(page!.Url)) throw new InvalidOperationException("ConversationNotSaved");
            // A conversation that was already on screen has long finished drawing its earlier turns.
            if (continuing && !kept) { step = "waiting for the conversation to load"; await SettleConversationAsync(ct); }
            await DetectAsync();
            if (state != "Connected") throw new InvalidOperationException(state == "VerificationRequired" ? "VerificationRequired" : "LoginRequired");
            clock.Mark("settle");
            ct.ThrowIfCancellationRequested();
            step = "clearing old attachments";
            await ClearAttachmentsAsync(ct);
            if (inputs.Count > 0)
            {
                step = "waiting for the page to load";
                await LoadedAsync();
                step = "attaching pictures";
                await AttachAsync(inputs, ct);
                clock.Mark("attach");
            }
            step = "reading the page";
            // Only what shows is counted, before sending and while waiting alike: a chat started in place keeps the previous one in the page, hidden,
            // and its last picture would otherwise stand where the new one is looked for.
            var before = await Visible(settings.Selectors.Assistant).CountAsync();
            // Counted across the page, not inside the reply: the site renders a generated image in the conversation turn,
            // outside the assistant message element, so scoping the search there finds nothing however new the image is.
            var imagesBefore = await Visible(settings.Selectors.GeneratedImage).CountAsync();
            var pendingBefore = await page.Locator(settings.Selectors.ImagePending).CountAsync();
            // A question already waiting on this page is the one being answered; only a different one afterwards is a new question.
            var askedBefore = await QuestionAsync();
            var shown = await ImageKeysAsync();
            await report(RunStatus.SendingPrompt);
            // Never retry submission: a timeout after sending may still have accepted the prompt.
            if (askedBefore != null && await Visible(settings.Selectors.Composer).CountAsync() == 0)
            {
                // The model asked and is waiting: the answer goes into the question's own field, as a person would type it there.
                step = "answering the question";
                var field = Visible(settings.Selectors.QuestionForm).First.Locator(settings.Selectors.QuestionInput).First;
                await field.FillAsync(prompt);
                await field.PressAsync("Enter");
            }
            else
            {
                step = "typing the message";
                await Visible(settings.Selectors.Composer).First.FillAsync(prompt);
                step = "pressing send";
                await Visible(settings.Selectors.Send).First.ClickAsync();
            }
            step = "waiting for the answer";
            clock.Mark("send");
            await report(RunStatus.WaitingForResponse);
            var deadline = DateTime.UtcNow.AddSeconds(settings.GenerationTimeoutSeconds);
            string? previous = null, lastWords = null;
            DateTime? stableSince = null, wordsSince = null, quietSince = null, recoveredAt = null;
            bool announced = false, urlSeen = false, sawStop = false;
            // Where the answer lives, so a tab that dies on the way can be reopened on it: the site keeps generating whatever the tab does.
            var thread = continuing ? conversation : null;
            var recoveries = 0;
            // Each pass over the page sets the beat; one that stops for longer than any look at a page takes is a tab that will never answer.
            var beat = DateTime.UtcNow;
            using var watching = new CancellationTokenSource();
            var watchdog = Task.Run(async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, settings.HungSeconds / 3))), watching.Token); } catch (OperationCanceledException) { return; }
                    if (DateTime.UtcNow - beat > TimeSpan.FromSeconds(settings.HungSeconds) && page is { } tab) Abandon(tab, "A look at the ChatGPT tab hung; reopening it");
                }
            });
            try
            {
            while (DateTime.UtcNow < deadline)
            {
                beat = DateTime.UtcNow;
                await Task.Delay(settings.PollMs, ct);
                if (crashed)
                {
                    // The dead tab still knows its last address: a crash right after sending happens before this loop has read it.
                    if (thread == null && IsConversation(page.Url) && !page.Url.Contains("local-", StringComparison.OrdinalIgnoreCase)) thread = page.Url;
                    if (thread == null || thread.Contains("local-", StringComparison.OrdinalIgnoreCase) || recoveries++ >= MaxRecoveries) throw new InvalidOperationException("BrowserCrashed");
                    step = "reopening the conversation after a crash";
                    await ReplaceTabAsync();
                    await NavigateAsync(thread, settings.ConversationOpenSeconds * 1000);
                    await Visible($"{settings.Selectors.Composer}, {settings.Selectors.QuestionForm}").First.WaitForAsync(new() { Timeout = settings.ConversationOpenSeconds * 1000 });
                    await SettleConversationAsync(ct);
                    step = "waiting for the answer";
                    // Everything is drawn afresh: the newest picture is judged by what it is, against what the page showed before the prompt went.
                    imagesBefore = 0; previous = null; stableSince = null; wordsSince = null; quietSince = null; lastWords = null;
                    recoveredAt = DateTime.UtcNow;
                    clock.Mark("recovered");
                    continue;
                }
                if (!urlSeen && IsConversation(page.Url))
                {
                    urlSeen = true;
                    if (!page.Url.Contains("local-", StringComparison.OrdinalIgnoreCase)) thread = page.Url;
                    await report(announced ? RunStatus.GeneratingImage : RunStatus.WaitingForResponse, page.Url);
                }
                else if (urlSeen && thread?.Contains("local-", StringComparison.OrdinalIgnoreCase) != false && IsConversation(page.Url) && !page.Url.Contains("local-", StringComparison.OrdinalIgnoreCase)) thread = page.Url;
                // A call that was in flight when the tab died fails with it; the next pass sees the crash and reopens the conversation.
                try
                {
                    var stopping = await Visible(settings.Selectors.Stop).CountAsync() > 0;
                    sawStop |= stopping;
                    var images = Visible(settings.Selectors.GeneratedImage);
                    var image = images.Last;
                    if (await images.CountAsync() > imagesBefore && await image.IsVisibleAsync())
                    {
                        quietSince = null;
                        var source = await image.EvaluateAsync<string>("el => el.complete && el.naturalWidth >= 256 && el.naturalHeight >= 256 ? el.currentSrc : ''");
                        // A picture the page already showed before the prompt went, only drawn late: part of the conversation, not the answer to it.
                        if (source.Length > 0 && shown.Contains(ImageKey(source))) { imagesBefore = await images.CountAsync(); previous = null; stableSince = null; continue; }
                        if (!announced) { await report(RunStatus.GeneratingImage); announced = true; }
                        clock.Mark("first sign");
                        if (string.IsNullOrEmpty(source) || stopping) { stableSince = null; continue; }
                        // Drawn and no longer streaming: what follows is only the wait that proves the picture has stopped changing.
                        clock.Mark("drawn");
                        if (source != previous || stableSince == null) { previous = source; stableSince = DateTime.UtcNow; continue; }
                        if ((DateTime.UtcNow - stableSince.Value).TotalSeconds < settings.StableSeconds) continue;
                        clock.Mark("steady");
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
                        var picture = Convert.FromBase64String(base64);
                        // The last guard: bytes the conversation already produced are its history, however late the page drew them. Keep waiting for a new one.
                        if (knownImages is { Count: > 0 } && knownImages.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture))))
                        { imagesBefore = await images.CountAsync(); shown.Add(ImageKey(source)); previous = null; stableSince = null; continue; }
                        return Done(new(picture, await SettledUrlAsync(ct)));
                    }
                    // No picture yet. One still on its way keeps the turn open however quiet the rest of the page is: on the live site the stop control
                    // goes several seconds before the picture lands, and judging the turn over at that moment is what made successes a matter of luck.
                    var pending = await page.Locator(settings.Selectors.ImagePending).CountAsync() > pendingBefore;
                    if (stopping || pending)
                    {
                        clock.Mark("first sign");
                        wordsSince = null; quietSince = null;
                        if (pending && !announced) { await report(RunStatus.GeneratingImage); announced = true; }
                        continue;
                    }
                    // Just reopened, the conversation's words are on the page before its pictures are: an answer in words is not judged until they have had time to arrive.
                    if (recoveredAt is { } since && DateTime.UtcNow - since < TimeSpan.FromSeconds(20)) continue;
                    var replies = Visible(settings.Selectors.Assistant);
                    if (await QuestionAsync() is { } asked && asked != askedBefore)
                    {
                        // The model is waiting on the person: any preface it wrote reads like progress on its own, so the question is what is handed back.
                        var preface = await replies.CountAsync() > before ? (await replies.Last.InnerTextAsync()).Trim() : "";
                        var words = preface.Length > 0 && !preface.Contains(asked) ? preface + "\n\n" + asked : asked;
                        return Done(new(null, await SettledUrlAsync(ct), words.Length > 8000 ? words[..8000] : words));
                    }
                    if (await replies.CountAsync() > before)
                    {
                        // Words, and nothing in progress: the model has answered in text, whether a question about the prompt, a refusal or anything else.
                        // They are taken once they stop changing, so a reply still streaming is not cut short, and handed back to the person who asked.
                        var words = (await replies.Last.InnerTextAsync()).Trim();
                        if (words.Length == 0 || words != lastWords) { lastWords = words; wordsSince = DateTime.UtcNow; continue; }
                        if ((DateTime.UtcNow - wordsSince!.Value).TotalSeconds < settings.StableSeconds) continue;
                        if (settings.SpentPhrases.Any(phrase => !string.IsNullOrWhiteSpace(phrase) && words.Contains(phrase.Trim(), StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("NoImageReturned");
                        return Done(new(null, await SettledUrlAsync(ct), words.Length > 8000 ? words[..8000] : words));
                    }
                    // Nothing to read and nothing in progress after the model had visibly started: the hooks have drifted. Bounded, so a run cannot sit
                    // for the whole timeout on a page that will never say anything the selectors understand.
                    if (!sawStop) continue;
                    quietSince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - quietSince.Value).TotalSeconds >= settings.QuietSeconds) throw new InvalidOperationException("NoImageReturned");
                }
                catch (Exception e) when (crashed && e is PlaywrightException or TimeoutException) { /* reopened on the next pass */ }
            }
            }
            finally { watching.Cancel(); await watchdog; }
            throw new InvalidOperationException("GenerationTimeout");
        }
        // Every way a run can stop short is recorded, the run's own verdicts (an upload that never finished, a reply without a picture) as much as the
        // browser's. Playwright for .NET reports a wait that ran out as System.TimeoutException, not a PlaywrightException: caught by that alone, the
        // most common failure of all went unrecorded.
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            await RecordFailureAsync(step, ex);
            throw;
        }
        finally { gate.Release(); }
    }
    private static bool SamePage(string a, string b) => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && x.Host == y.Host && x.AbsolutePath.TrimEnd('/') == y.AbsolutePath.TrimEnd('/');
    private async Task<bool> ComposerShownAsync() => !crashed && page is { IsClosed: false } && await Visible($"{settings.Selectors.Composer}, {settings.Selectors.QuestionForm}").CountAsync() > 0;
    /// <summary>The tab is on this very conversation, alive, with its box showing: usually because the previous run in it was this chat's last turn.</summary>
    private async Task<bool> ShowsConversationAsync(string conversation) => !crashed && page is { IsClosed: false } && SamePage(page.Url, conversation) && await ComposerShownAsync();
    /// <summary>Nothing of a conversation showing. Counted among what is visible: after New chat the site keeps the previous conversation in the page,
    /// hidden, so counting everything never found a fresh chat at all.</summary>
    private async Task<bool> EmptyThreadAsync() =>
        await Visible($"{settings.Selectors.Assistant}, {settings.Selectors.GeneratedImage}, [data-message-author-role='user']").CountAsync() == 0;
    /// <summary>A fresh chat without loading the site again: the tab is already on one, or the site makes one in place, first through its own keyboard
    /// shortcut and then through its New chat button. The shortcut leads because on the live site the visible button did not take a click within five
    /// seconds while the shortcut worked at once. Anything short of an empty chat on the home address soon after counts as not done, and the run loads
    /// the page as before.</summary>
    private async Task<bool> FreshChatInPlaceAsync(CancellationToken ct)
    {
        if (page is not { IsClosed: false } || crashed || !Uri.TryCreate(page.Url, UriKind.Absolute, out var at) || at.Host != "chatgpt.com" || !await ComposerShownAsync()) return false;
        if (at.AbsolutePath == "/" && await EmptyThreadAsync()) return true;
        foreach (var how in new[] { "shortcut", "button" })
        {
            try
            {
                if (how == "shortcut") await page.Keyboard.PressAsync("Control+Shift+O");
                else if (await Visible(settings.Selectors.NewChat).CountAsync() > 0) await Visible(settings.Selectors.NewChat).First.ClickAsync(new() { Timeout = 3000, Force = true });
                else continue;
            }
            catch (Exception e) when (e is PlaywrightException or TimeoutException) { logger?.LogInformation("New chat in place: the {How} could not be used ({Type})", how, e.GetType().Name); continue; }
            if (await FreshAsync(TimeSpan.FromSeconds(4), ct)) return true;
            // Said in counts only, so a change on the site shows which part stopped matching.
            logger?.LogInformation("New chat in place did not settle after the {How}: home {Home}, turns showing {Turns}, box showing {Box}", how,
                Uri.TryCreate(page.Url, UriKind.Absolute, out var end) && end.AbsolutePath == "/",
                await Visible($"{settings.Selectors.Assistant}, {settings.Selectors.GeneratedImage}, [data-message-author-role='user']").CountAsync(),
                await Visible(settings.Selectors.Composer).CountAsync());
        }
        return false;
    }
    private async Task<bool> FreshAsync(TimeSpan within, CancellationToken ct)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(250, ct);
            if (Uri.TryCreate(page!.Url, UriKind.Absolute, out var now) && now.AbsolutePath == "/" && await EmptyThreadAsync() && await Visible(settings.Selectors.Composer).CountAsync() > 0) return true;
        }
        return false;
    }
    /// <summary>A conversation opens with its message box first and its earlier turns a moment later, pictures last. Counted before they arrive, the
    /// conversation's own last picture turned up after the prompt was sent and was taken for the answer: a follow-up came back with the previous
    /// image, or, on a slower server, sat waiting on it until it timed out. Waits until something of the conversation is on the page and the page has
    /// stopped changing; a conversation that shows nothing for the whole allowance is left as it is.</summary>
    private async Task SettleConversationAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(settings.ConversationLoadSeconds);
        (int Images, int Replies)? last = null;
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            (int Images, int Replies) now = (await page!.Locator(settings.Selectors.GeneratedImage).CountAsync(), await page.Locator(settings.Selectors.Assistant).CountAsync());
            if (now != last) { (last, since) = (now, DateTime.UtcNow); }
            else if (now.Images + now.Replies > 0 && DateTime.UtcNow - since >= TimeSpan.FromSeconds(2)) return;
            await Task.Delay(400, ct);
        }
    }
    /// <summary>The same key on both sides of the page: length and both ends of the address, enough to tell pictures apart without carrying megabytes of data URL.</summary>
    public static string ImageKey(string source) => $"{source.Length}:{source[..Math.Min(64, source.Length)]}:{source[^Math.Min(64, source.Length)..]}";
    private async Task<HashSet<string>> ImageKeysAsync() =>
        [.. (await page!.Locator(settings.Selectors.GeneratedImage).EvaluateAllAsync<string[]>(
            "els => els.map(e => { const s = e.currentSrc || e.src || ''; return s ? s.length + ':' + s.slice(0, 64) + ':' + s.slice(-Math.min(64, s.length)) : ''; })")).Where(k => k.Length > 0)];
    /// <summary>Which step a run stopped at, and what the page showed then. The exception's text is not logged, as it can carry page content; the step
    /// says where to look, and the screenshot, kept on the server beside the data it shows, says what the site was doing. The site's pages change
    /// every few weeks and every change reads the same from outside, so this is how the next one gets found in minutes rather than guessed at.</summary>
    private async Task RecordFailureAsync(string step, Exception ex)
    {
        var where = page?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath == "/" ? "home" : uri.AbsolutePath.StartsWith("/c/") ? "conversation" : uri.Host : "no";
        var facts = page is { IsClosed: false } ? await FactsAsync() : "";
        logger?.LogWarning("Browser run stopped while {Step}, on the {Page} page ({Type}); {Facts}", step, where, ex.GetType().Name, facts);
        if (page is null or { IsClosed: true }) return;
        try
        {
            var folder = Path.Combine(Path.GetFullPath(config["Storage:Root"] ?? "Storage"), "diagnostics");
            Directory.CreateDirectory(folder);
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{step.Replace(' ', '-')}.png"), Timeout = 5000 });
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.png").OrderByDescending(f => f.CreationTimeUtc).Skip(20)) old.Delete();
        }
        catch (Exception shot) when (shot is PlaywrightException or IOException) { logger?.LogDebug("No screenshot of the failed run ({Type})", shot.GetType().Name); }
    }
    /// <summary>How many of each control the page has, how many of them show, and what the first shown one is: tag, id, label, whether it is
    /// disabled, and how many characters it holds. Names and counts only, never what anybody wrote.</summary>
    private async Task<string> FactsAsync()
    {
        var hooks = new Dictionary<string, string>
        {
            ["composer"] = settings.Selectors.Composer, ["send"] = settings.Selectors.Send, ["stop"] = settings.Selectors.Stop,
            ["question"] = settings.Selectors.QuestionForm, ["remove"] = settings.Selectors.UploadReady, ["signedIn"] = settings.Selectors.LoggedIn,
        };
        try
        {
            return await page!.EvaluateAsync<string>("""
                hooks => Object.entries(hooks).map(([name, selector]) => {
                  let all = [];
                  try { all = [...document.querySelectorAll(selector)]; } catch { return name + ' ?'; }
                  const shown = all.filter(e => e.checkVisibility ? e.checkVisibility() : e.offsetParent !== null);
                  const e = shown[0];
                  const what = !e ? '' : ' ' + e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.getAttribute('aria-label') ? '[' + e.getAttribute('aria-label') + ']' : '')
                    + (e.disabled || e.getAttribute('aria-disabled') === 'true' ? ' disabled' : '') + ' ' + (e.value ?? e.innerText ?? '').length + 'ch';
                  return name + ' ' + shown.length + '/' + all.length + what;
                }).join(', ') + ', focus ' + (document.activeElement ? document.activeElement.tagName.toLowerCase() + (document.activeElement.id ? '#' + document.activeElement.id : '') : 'none')
                """, hooks);
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException) { return "page unreadable"; }
    }
    /// <summary>The site draws its message box well before it has finished loading, and a file handed to it in between is dropped without a word: on a
    /// slow line the box came 24 seconds before the load, and every picture attached in that gap ended as UploadFailed. A page that never reports its
    /// load is attached to anyway once the wait is up.</summary>
    private async Task LoadedAsync()
    {
        try { await page!.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = settings.NavigationTimeoutMs }); }
        catch (TimeoutException) { }
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
    /// <summary>The question the site is holding the conversation on, or null. Its own controls (the field, Skip, attachments) are not part of it.</summary>
    private async Task<string?> QuestionAsync()
    {
        var forms = Visible(settings.Selectors.QuestionForm);
        if (await forms.CountAsync() == 0) return null;
        var text = await forms.Last.EvaluateAsync<string>("f => { const c = f.cloneNode(true); c.querySelectorAll('button, input, textarea, select, svg, [role=status]').forEach(e => e.remove()); return (c.innerText || c.textContent || '').trim(); }");
        return string.IsNullOrWhiteSpace(text) ? null : text;
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
            try { await remove.First.ClickAsync(new() { Timeout = 5000 }); } catch (Exception e) when (e is PlaywrightException or TimeoutException) { return; }
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
