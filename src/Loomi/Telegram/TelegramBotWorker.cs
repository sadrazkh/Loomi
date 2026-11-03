using System.Collections.Concurrent;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using File = System.IO.File;
namespace Loomi.Telegram;
/// <summary>The bot, in one place. It talks to the same <see cref="GenerationService"/> and <see cref="TemplateService"/> the web and the API do, so
/// quota, credits and pricing are the same rules however the work arrived; nothing here calls Loomi's own HTTP surface.</summary>
public class TelegramBotWorker(IServiceScopeFactory scopes, ITelegramClient client, GenerationEvents events, IOptions<TelegramOptions> telegram,
                               IOptions<BrowserOptions> browser, BotSettings bot, QueueEstimates queue, ILogger<TelegramBotWorker> logger) : BackgroundService
{
    /// <summary>Where every plain description from a chat went before each chat became a project of its own. Only delivery still reads it, so work
    /// queued back then, before requests were recorded, is still answered.</summary>
    public const string BotProject = "Telegram";
    /// <summary>How long a chosen template or a started edit waits for the photos or words that finish it.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(30);
    public static readonly IReadOnlyList<BotCommandText> Commands =
    [
        new("new", "Start a new chat", "شروع گفتگوی تازه"),
        new("templates", "Ready-made looks: pick one, send your photo", "قالب‌های آماده: یکی را انتخاب کنید و عکستان را بفرستید"),
        new("balance", "Your credits", "اعتبار شما"),
        new("status", "What is running", "کارهای در جریان"),
        new("cancel", "Stop what you are doing", "لغو کار جاری"),
        new("web", "A one-time link to your account on the website", "لینک یک‌بارمصرف ورود به حسابتان در وب"),
        new("help", "How it works", "راهنما"),
    ];
    private readonly TelegramOptions settings = telegram.Value;
    /// <summary>The language last seen from a chat. A finished generation arrives with no message behind it, so the answer has to remember how to speak.</summary>
    private readonly ConcurrentDictionary<long, bool> persianChats = new();
    private readonly ConcurrentDictionary<string, Album> albums = new();
    /// <summary>A template chosen or an edit started, waiting for the message that completes it. Held in memory: after a restart the person taps again.</summary>
    private readonly ConcurrentDictionary<long, Pending> pending = new();
    /// <summary>What each status line last said, so it is edited only when it would read differently; Telegram refuses an edit that changes nothing.</summary>
    private readonly ConcurrentDictionary<Guid, string> statusLines = new();
    /// <summary>Photos of one album arrive as separate messages with no marker for the last, so the only way to know it is complete is that none has arrived for a moment.</summary>
    private sealed class Album { public long Chat; public bool Persian; public string? Caption; public List<string> Files = []; public DateTime Last; }
    private abstract record Pending(DateTime Since);
    private sealed record TemplatePending(Guid Template, string Title, int Min, int Max, DateTime Since) : Pending(Since);
    private sealed record EditPending(Guid Generation, DateTime Since) : Pending(Since);
    private static IReadOnlyList<BotButton> Row(params BotButton[] buttons) => buttons;
    private static IReadOnlyList<IReadOnlyList<BotButton>> Keys(params IReadOnlyList<BotButton>[] rows) => rows;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        events.Subscribe(DeliverAsync);
        var albumLoop = Task.Run(() => FlushLoopAsync(stoppingToken), stoppingToken);
        var statusLoop = Task.Run(() => StatusLoopAsync(stoppingToken), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            // No token means no bot yet. Say nothing, and wake when the owner sets one: an operator who has not configured one needs no warning every start.
            var changed = bot.Changed;
            if (!bot.Configured) { try { await changed.WaitAsync(stoppingToken); } catch (OperationCanceledException) { break; } continue; }
            // A new token is a new bot: its update numbering starts over, and a long poll held open with the old one is let go at once.
            using var reset = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _ = changed.ContinueWith(_ => { try { reset.Cancel(); } catch (ObjectDisposedException) { /* this bot's loop already ended */ } }, TaskScheduler.Default);
            await RegisterCommandsAsync(reset.Token);
            var offset = 0;
            while (!reset.IsCancellationRequested)
            {
                try
                {
                    foreach (var update in await client.GetUpdatesAsync(offset, reset.Token))
                    {
                        offset = update.Id + 1;
                        if (update.Message is { } message) await HandleAsync(message, stoppingToken);
                        else if (update.CallbackQuery is { } press) await HandleCallbackAsync(press, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (reset.IsCancellationRequested) { break; }
                // Never the exception text: it can carry the bot token from a request URL.
                catch (Exception ex) { logger.LogWarning("Telegram polling failed ({Type})", ex.GetType().Name); try { await Task.Delay(5000, reset.Token); } catch (OperationCanceledException) { break; } }
            }
        }
        events.Unsubscribe(DeliverAsync);
        await albumLoop;
        await statusLoop;
    }
    /// <summary>The menu Telegram shows when a person types "/". Set for each bot as it starts; a failure costs only the menu, not the bot.</summary>
    public async Task RegisterCommandsAsync(CancellationToken ct)
    {
        try { await client.SetCommandsAsync(Commands, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Telegram command menu could not be set ({Type})", ex.GetType().Name); }
    }
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct); await FlushAlbumsAsync(DateTime.UtcNow, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Telegram album flush failed ({Type})", ex.GetType().Name); }
        }
    }
    /// <summary>Submits every album nothing has been added to for the debounce window. Public so a test can drive the clock instead of waiting on it.</summary>
    public async Task FlushAlbumsAsync(DateTime now, CancellationToken ct)
    {
        foreach (var (key, album) in albums.ToArray())
        {
            if (now - album.Last < TimeSpan.FromMilliseconds(settings.MediaGroupMs) || !albums.TryRemove(key, out _)) continue;
            await RouteAsync(album.Chat, album.Persian, album.Caption ?? "", album.Files, ct);
        }
    }
    /// <summary>Persian once a chat has shown it in any way: Telegram set to Persian, or simply writing in Persian with Telegram in English.</summary>
    private bool Remember(long chat, string? languageCode, string? text)
    {
        var persian = BotMessages.IsPersian(languageCode) || BotMessages.LooksPersian(text) || persianChats.GetValueOrDefault(chat);
        persianChats[chat] = persian;
        return persian;
    }
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var text = (message.Text ?? message.Caption ?? "").Trim();
        var persian = Remember(chat, message.From?.LanguageCode, text);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var code = text[6..].Trim();
            if (code.Length == 0)
            {
                var joining = Joining(scope, db);
                // With sign-up open, someone who has an account and taps Start again should not be told the chat is not linked. With it closed, nothing changes.
                if (await UserAsync(db, chat, ct) is not null && await joining.OpenAsync(ct)) await HelpAsync(chat, persian, ct);
                else await joining.StrangerAsync(message, persian, ct);
                return;
            }
            var outcome = await Linking.RedeemAsync(db, code, chat, DateTime.UtcNow, ct);
            // Linked is the moment to show what the bot can do, one tap away.
            await client.SendTextAsync(chat, BotMessages.Text(outcome.Key, persian, outcome.User?.Username ?? ""), ct,
                outcome.User is null ? null : Keys(Row(new BotButton(BotMessages.Text("btnTemplates", persian), "templates"), new BotButton(BotMessages.Text("btnHelp", persian), "help"))));
            return;
        }
        // Everything past this point acts as a Loomi user, so an unlinked chat stops here and never reaches the queue.
        var user = await UserAsync(db, chat, ct);
        if (user == null) { await Joining(scope, db).StrangerAsync(message, persian, ct); return; }
        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/help": await HelpAsync(chat, persian, ct); return;
            case "/new": await NewChatAsync(db, chat, persian, ct); return;
            case "/templates": await ShowCategoriesAsync(db, chat, persian, ct); return;
            case "/balance": await BalanceAsync(db, chat, user, persian, ct); return;
            case "/status": await StatusAsync(db, chat, user, persian, ct); return;
            case "/web": await Joining(scope, db).WebAsync(message, user, persian, ct); return;
            // A half-finished choice is what the person most likely means to stop; running work is cancelled only when there is nothing else.
            case "/cancel": if (pending.TryRemove(chat, out _)) await SayAsync(chat, "stateCleared", persian, ct); else await CancelAsync(db, chat, user, persian, ct); return;
            // Everybody's use is the owner's to read. To anyone else it is an unknown command, and it stays out of the menu members see.
            case "/users" when user.Role == UserRole.Owner: await Usage(scope, db).SummaryAsync(chat, UsagePeriod.Today, persian, ct); return;
        }
        if (message.Photo is { Length: > 0 } sizes)
        {
            // The last size is the largest Telegram kept; anything smaller is a thumbnail of the same picture.
            var file = sizes[^1].FileId;
            if (message.MediaGroupId is { } group)
            {
                var album = albums.GetOrAdd(group, _ => new Album { Chat = chat, Persian = persian });
                lock (album) { album.Files.Add(file); album.Caption ??= string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption.Trim(); album.Last = DateTime.UtcNow; }
                return;
            }
            await RouteAsync(chat, persian, text, [file], ct);
            return;
        }
        if (text.StartsWith('/')) { await HelpAsync(chat, persian, ct); return; }
        await RouteAsync(chat, persian, text, [], ct);
    }
    /// <summary>Where a message goes: into the template or edit it completes, when one is waiting for it, or else into the chat's conversation.</summary>
    private async Task RouteAsync(long chat, bool persian, string text, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (pending.TryGetValue(chat, out var waiting) && DateTime.UtcNow - waiting.Since < PendingFor)
        {
            switch (waiting)
            {
                case TemplatePending t when files.Count == 0 && t.Min > 0:
                    await client.SendTextAsync(chat, BotMessages.Text("templateNeedsPhotos", persian, PhotoCount(t.Min, t.Max, persian)), ct); return;
                case TemplatePending t:
                    await UseTemplateAsync(chat, persian, t.Template, text, files, ct); return;
                case EditPending when text.Length == 0:
                    await SayAsync(chat, "editPrompt", persian, ct); return;
                case EditPending e:
                    await EditAsync(chat, persian, e.Generation, text, files, ct); return;
            }
        }
        if (files.Count > 0 && text.Length == 0) { await SayAsync(chat, "needCaption", persian, ct); return; }
        if (text.Length == 0) { await HelpAsync(chat, persian, ct); return; }
        await SubmitAsync(chat, persian, text, files, ct);
    }
    /// <summary>A pressed button. Answered at once so Telegram stops its spinner, then treated like the command it stands for.</summary>
    public async Task HandleCallbackAsync(CallbackQuery press, CancellationToken ct)
    {
        try { await client.AnswerAsync(press.Id, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Callback answer failed ({Type})", ex.GetType().Name); }
        if (press.Message?.Chat.Id is not { } chat || press.Data is not { } data) return;
        var persian = Remember(chat, press.From?.LanguageCode, null);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await UserAsync(db, chat, ct);
        // These two are for a chat with no account yet, so they come before what stops one.
        if (data is SignupBot.Join or SignupBot.HaveAccount) { await Joining(scope, db).PressAsync(press, chat, data, user, persian, ct); return; }
        if (user == null) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        var colon = data.IndexOf(':');
        var verb = colon < 0 ? data : data[..colon];
        var payload = colon < 0 ? "" : data[(colon + 1)..];
        Guid.TryParseExact(payload, "N", out var id);
        switch (verb)
        {
            case "templates": await ShowCategoriesAsync(db, chat, persian, ct); break;
            case "help": await HelpAsync(chat, persian, ct); break;
            case "cat": await ShowCategoryAsync(db, chat, persian, id, ct); break;
            case "tpl": await ShowTemplateAsync(db, scope, chat, persian, user, id, ct); break;
            case "use": await ChooseTemplateAsync(db, chat, persian, id, ct); break;
            case "go": await UseTemplateAsync(chat, persian, id, "", [], ct); break;
            case "edit": await StartEditAsync(db, chat, persian, user, id, ct); break;
            case "again": await RepeatAsync(chat, persian, id, ct); break;
            case "file": await SendOriginalAsync(db, scope, chat, persian, user, id, ct); break;
            case "new": await NewChatAsync(db, chat, persian, ct); break;
            // Pressed by a member, from a forwarded message or a hand-made query, these do nothing at all.
            case "usage" or "usr" or "uimg" when user.Role == UserRole.Owner: await Usage(scope, db).PressAsync(chat, verb, payload, persian, ct); break;
        }
    }
    /// <summary>The owner's report, speaking through this worker's own client.</summary>
    private UsageBot Usage(IServiceScope scope, AppDbContext db) =>
        new(client, scope.ServiceProvider.GetRequiredService<UsageReport>(), db, scope.ServiceProvider.GetRequiredService<IImageStorage>(), scope.ServiceProvider.GetRequiredService<Watermark>());
    /// <summary>Signing up and the way into the web, speaking through this worker's own client.</summary>
    private SignupBot Joining(IServiceScope scope, AppDbContext db) =>
        new(client, db, scope.ServiceProvider.GetRequiredService<GrowthSettings>(), scope.ServiceProvider.GetRequiredService<IConfiguration>(), bot, logger);
    private static async Task<AppUser?> UserAsync(AppDbContext db, long chat, CancellationToken ct) =>
        await db.TelegramLinks.AsNoTracking().Where(l => l.ChatId == chat)
            .Join(db.Users.Where(u => !u.IsDisabled), l => l.UserId, u => u.Id, (_, u) => u).FirstOrDefaultAsync(ct);
    private Task SayAsync(long chat, string key, bool persian, CancellationToken ct) => client.SendTextAsync(chat, BotMessages.Text(key, persian), ct);
    /// <summary>The help, with the line about /web under it, which is kept with the rest of signing up.</summary>
    public static string HelpText(bool persian) => BotMessages.Text("help", persian) + "\n\n" + BotMessages.Text("helpWeb", persian);
    private Task HelpAsync(long chat, bool persian, CancellationToken ct) =>
        client.SendTextAsync(chat, HelpText(persian), ct, Keys(Row(new BotButton(BotMessages.Text("btnTemplates", persian), "templates"))));
    private async Task BalanceAsync(AppDbContext db, long chat, AppUser user, bool persian, CancellationToken ct)
    {
        var used = await db.UsedByAsync(user.Id, ct);
        // The owner is not charged, so a balance for them would be a number with nothing behind it — the same reason the web hides it.
        if (user.Role == UserRole.Owner) { await client.SendTextAsync(chat, BotMessages.Text("balanceOwner", persian, used), ct); return; }
        var now = DateTime.UtcNow;
        var running = await db.CreditPackages.AsNoTracking().Where(p => p.UserId == user.Id).ActiveAt(now).ToListAsync(ct);
        var packages = await db.DescribeAsync(running.OrderBy(p => p.EndsAt).ThenBy(p => p.CreatedAt).ToList(), now, withNote: false, ct);
        var balance = BotMessages.Text("balance", persian, await db.BalanceAsync(user.Id, ct), used);
        await client.SendTextAsync(chat, string.Join("\n", packages.Select(p => PackageLine(p, now, persian)).Prepend(balance)), ct);
    }
    /// <summary>A running package as one line: how long it has left and what is left of it today, or of its credits.</summary>
    private static string PackageLine(PackageDto p, DateTime now, bool persian)
    {
        string N(long n) => BotMessages.Number(n, persian);
        var left = p.Kind switch
        {
            PackageKind.Credits => BotMessages.Text("balancePackageCredits", persian, N(p.Remaining ?? 0)),
            PackageKind.Unlimited when p.Amount == 0 => BotMessages.Text("balancePackageUnlimited", persian, N(p.UsedToday)),
            _ => BotMessages.Text("balancePackageToday", persian, N(p.UsedToday), N(p.Amount)),
        };
        return BotMessages.Text("balancePackage", persian, p.Name, PackageReminderWorker.Days(PackageReminderWorker.DaysLeft(p.EndsAt, now), persian), PackageReminderWorker.Date(p.EndsAt, persian), left);
    }
    private async Task StatusAsync(AppDbContext db, long chat, AppUser user, bool persian, CancellationToken ct)
    {
        var running = await db.Generations.AsNoTracking().Where(g => g.UserId == user.Id).Unfinished().OrderBy(g => g.CreatedAt).Select(g => g.Status).ToListAsync(ct);
        await client.SendTextAsync(chat, running.Count == 0 ? BotMessages.Text("statusIdle", persian)
            : BotMessages.Text("statusBusy", persian, running.Count, string.Join(", ", running.Select(s => s.ToString()))), ct);
    }
    /// <summary>Only this user's own work, whoever they are: an owner cancelling everything is a decision for the web, not a side effect of a chat command.</summary>
    private async Task CancelAsync(AppDbContext db, long chat, AppUser user, bool persian, CancellationToken ct)
    {
        var mine = await db.Generations.Include(g => g.Project).Where(g => g.UserId == user.Id).Unfinished().ToListAsync(ct);
        foreach (var g in mine) await Cancellation.CancelAsync(db, g, ct);
        await client.SendTextAsync(chat, mine.Count == 0 ? BotMessages.Text("nothingToCancel", persian) : BotMessages.Text("cancelled", persian, mine.Count), ct);
    }

    // ---- Templates: browse by category, see the sample, choose, send the photos ----

    private static IQueryable<PromptTemplate> Offered(AppDbContext db) => db.Templates.AsNoTracking().Where(t => t.IsActive && t.SamplePath != null);
    private async Task ShowCategoriesAsync(AppDbContext db, long chat, bool persian, CancellationToken ct)
    {
        var counts = await Offered(db).GroupBy(t => t.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var categories = (await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct)).Where(c => counts.ContainsKey(c.Id)).ToList();
        if (categories.Count == 0) { await SayAsync(chat, "noTemplates", persian, ct); return; }
        // One category is not a choice worth a tap.
        if (categories.Count == 1) { await ShowCategoryAsync(db, chat, persian, categories[0].Id, ct); return; }
        var rows = categories.Chunk(2).Select(pair => (IReadOnlyList<BotButton>)pair.Select(c => new BotButton($"{c.Name} ({BotMessages.Number(counts[c.Id], persian)})", $"cat:{c.Id:N}")).ToList()).ToList();
        await client.SendTextAsync(chat, BotMessages.Text("pickCategory", persian), ct, rows);
    }
    private async Task ShowCategoryAsync(AppDbContext db, long chat, bool persian, Guid category, CancellationToken ct)
    {
        var name = await db.TemplateCategories.AsNoTracking().Where(c => c.Id == category).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var templates = await Offered(db).Where(t => t.CategoryId == category).OrderBy(t => t.Order).ThenBy(t => t.Title).Select(t => new { t.Id, t.Title }).ToListAsync(ct);
        if (name is null || templates.Count == 0) { await SayAsync(chat, "noTemplates", persian, ct); return; }
        var rows = templates.Select(t => (IReadOnlyList<BotButton>)new List<BotButton> { new(t.Title, $"tpl:{t.Id:N}") }).ToList();
        rows.Add([new BotButton(BotMessages.Text("btnCategories", persian), "templates")]);
        await client.SendTextAsync(chat, BotMessages.Text("pickTemplate", persian, name), ct, rows);
    }
    /// <summary>The sample itself, with what it takes and what it costs, and the button that chooses it.</summary>
    private async Task ShowTemplateAsync(AppDbContext db, IServiceScope scope, long chat, bool persian, AppUser user, Guid id, CancellationToken ct)
    {
        var template = await Offered(db).FirstOrDefaultAsync(t => t.Id == id, ct);
        var path = template is null ? null : scope.ServiceProvider.GetRequiredService<IImageStorage>().Resolve(template.SamplePath!);
        if (template is null || !File.Exists(path)) { await SayAsync(chat, "NotFound", persian, ct); return; }
        var lines = new List<string> { template.Title };
        if (!string.IsNullOrWhiteSpace(template.Instructions)) lines.Add(template.Instructions);
        lines.Add(BotMessages.Text("templatePhotos", persian, PhotoCount(template.MinInputs, template.MaxInputs, persian)));
        if (user.Role == UserRole.Member && await db.PriceAsync(template.Provider, Operation.Generate, ct) is > 0 and var price)
            lines.Add(BotMessages.Text("templatePrice", persian, BotMessages.Number(price, persian)));
        await client.SendPhotoAsync(chat, await File.ReadAllBytesAsync(path, ct), "sample" + Path.GetExtension(path), string.Join("\n\n", lines), ct,
            Keys(Row(new BotButton(BotMessages.Text("btnUse", persian), $"use:{id:N}")), Row(new BotButton(BotMessages.Text("btnOtherTemplates", persian), $"cat:{template.CategoryId:N}"))));
    }
    private async Task ChooseTemplateAsync(AppDbContext db, long chat, bool persian, Guid id, CancellationToken ct)
    {
        var template = await Offered(db).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) { await SayAsync(chat, "NotFound", persian, ct); return; }
        pending[chat] = new TemplatePending(id, template.Title, template.MinInputs, template.MaxInputs, DateTime.UtcNow);
        await client.SendTextAsync(chat, BotMessages.Text("sendTemplatePhotos", persian, template.Title, PhotoCount(template.MinInputs, template.MaxInputs, persian)), ct,
            template.MinInputs == 0 ? Keys(Row(new BotButton(BotMessages.Text("btnGoNow", persian), $"go:{id:N}"))) : null);
    }
    private static string PhotoCount(int min, int max, bool persian) =>
        min == max ? (min == 1 ? BotMessages.Text("photosOne", persian) : BotMessages.Text("photosExact", persian, BotMessages.Number(min, persian)))
        : min == 0 ? BotMessages.Text("photosUpTo", persian, BotMessages.Number(max, persian))
        : BotMessages.Text("photosRange", persian, BotMessages.Number(min, persian), BotMessages.Number(max, persian));
    private async Task UseTemplateAsync(long chat, bool persian, Guid template, string note, IReadOnlyList<string> files, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await UserAsync(db, chat, ct) is not { } user) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        try
        {
            if (await InputsAsync(db, scope, chat, persian, user, files, ct) is not { } inputs) return;
            var generation = await scope.ServiceProvider.GetRequiredService<TemplateService>().UseAsync(Viewer.For(user), template, note, inputs, ct);
            pending.TryRemove(chat, out _);
            await TrackAsync(db, chat, persian, generation, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "TemplateInputs" && pending.TryGetValue(chat, out var waiting) && waiting is TemplatePending t)
        {
            await client.SendTextAsync(chat, BotMessages.Text("templateNeedsPhotos", persian, PhotoCount(t.Min, t.Max, persian)), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { await RefusedAsync(chat, persian, ex, ct); }
    }

    // ---- The buttons under a result: edit it, make another version, send the original file ----

    /// <summary>Editing a picture goes back into its chat: that chat is the one talking from now on, whichever it was before.</summary>
    private async Task StartEditAsync(AppDbContext db, long chat, bool persian, AppUser user, Guid id, CancellationToken ct)
    {
        var project = await db.Generations.AsNoTracking().Where(g => g.Id == id && g.UserId == user.Id && g.Status == RunStatus.Completed && g.LocalImagePath != null)
            .Select(g => (Guid?)g.ProjectId).FirstOrDefaultAsync(ct);
        if (project is null) { await SayAsync(chat, "NotFound", persian, ct); return; }
        pending[chat] = new EditPending(id, DateTime.UtcNow);
        await TalkInAsync(db, chat, project, ct);
        await SayAsync(chat, "editPrompt", persian, ct);
    }
    /// <summary>An edit continues the image's own conversation, in its own project, exactly as Edit does on the web.</summary>
    private async Task EditAsync(long chat, bool persian, Guid parent, string text, IReadOnlyList<string> files, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await UserAsync(db, chat, ct) is not { } user) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        try
        {
            var source = await db.Generations.AsNoTracking().FirstOrDefaultAsync(g => g.Id == parent && g.UserId == user.Id, ct) ?? throw new KeyNotFoundException();
            if (await InputsAsync(db, scope, chat, persian, user, files, ct) is not { } inputs) return;
            var generation = await scope.ServiceProvider.GetRequiredService<GenerationService>().SubmitAsync(Viewer.For(user), source.ProjectId, text, source.Provider, Operation.Edit, source.Id, inputs, ct);
            pending.TryRemove(chat, out _);
            await TrackAsync(db, chat, persian, generation, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { pending.TryRemove(chat, out _); await RefusedAsync(chat, persian, ex, ct); }
    }
    /// <summary>The same request once more: same words, same photos, same template, so the model draws it afresh. The earlier image is not sent back in.</summary>
    private async Task RepeatAsync(long chat, bool persian, Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await UserAsync(db, chat, ct) is not { } user) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        try
        {
            var source = await db.Generations.AsNoTracking().Include(g => g.Inputs).FirstOrDefaultAsync(g => g.Id == id && g.UserId == user.Id && g.Operation != Operation.Reply, ct) ?? throw new KeyNotFoundException();
            // An edit or branch puts its parent first by itself; a generation's own references, lineage included, go as they were.
            var inputs = source.Inputs.OrderBy(i => i.Order).Where(i => source.Operation == Operation.Generate || i.SourceGenerationId != source.ParentGenerationId)
                .Select(i => new InputRef(i.UploadId, i.SourceGenerationId)).ToList();
            var template = source.TemplateId is { } t ? new TemplateUse(t, source.TemplateTitle ?? "", source.HiddenPrompt ?? "") : null;
            var generation = await scope.ServiceProvider.GetRequiredService<GenerationService>().SubmitAsync(Viewer.For(user), source.ProjectId, source.Prompt, source.Provider, source.Operation,
                source.Operation == Operation.Generate ? null : source.ParentGenerationId, inputs, ct, template);
            await TrackAsync(db, chat, persian, generation, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { await RefusedAsync(chat, persian, ex, ct); }
    }
    /// <summary>As a file, so it arrives at the size and quality it was made; Telegram recompresses anything sent as a photo. It is still what the person is
    /// handed, so a gift's carries its mark like any other delivery of it.</summary>
    private async Task SendOriginalAsync(AppDbContext db, IServiceScope scope, long chat, bool persian, AppUser user, Guid id, CancellationToken ct)
    {
        var stored = await db.Generations.AsNoTracking().Where(g => g.Id == id && g.UserId == user.Id).Select(g => new { g.LocalImagePath, g.PackageId }).FirstOrDefaultAsync(ct);
        var images = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        if (stored?.LocalImagePath is not { } original || !File.Exists(images.Resolve(original))) { await SayAsync(chat, "NotFound", persian, ct); return; }
        var path = images.Resolve(await scope.ServiceProvider.GetRequiredService<Watermark>().DeliveredAsync(db, stored.PackageId, original, ct));
        await client.SendDocumentAsync(chat, await File.ReadAllBytesAsync(path, ct), $"loomi-{id:N}{Path.GetExtension(path)}", null, ct);
    }

    // ---- The chat's conversation: each chat is one project, as each conversation is on the site ----

    /// <summary>The next message in this chat's conversation, the way it would be typed under the last answer on the site: the picture already there is
    /// not sent again, and photos with the message go as new references. A chat with no conversation, or one whose project is gone, starts one.</summary>
    private async Task SubmitAsync(long chat, bool persian, string prompt, IReadOnlyList<string> files, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await UserAsync(db, chat, ct);
        if (user == null) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        try
        {
            var current = await CurrentProjectAsync(db, chat, user.Id, ct);
            // Asked before any photo is fetched, so a message refused for it leaves no uploads behind; SendAsync asks again for itself.
            if (current is { } talking && await db.Generations.Where(g => g.ProjectId == talking).Unfinished().AnyAsync(ct)) throw new InvalidOperationException("ProjectBusy");
            if (await InputsAsync(db, scope, chat, persian, user, files, ct) is not { } inputs) return;
            var service = scope.ServiceProvider.GetRequiredService<GenerationService>();
            var generation = current is { } project
                ? await service.SendAsync(Viewer.For(user), project, prompt, inputs, null, ct)
                : await service.StartAsync(Viewer.For(user), null, prompt, inputs, null, ct);
            await TrackAsync(db, chat, persian, generation, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { await RefusedAsync(chat, persian, ex, ct); }
    }
    /// <summary>The project this chat is talking in, while it still exists and is still this user's. An owner's viewer reaches every project, so ownership
    /// is checked here by hand: a chat never talks in someone else's.</summary>
    private static async Task<Guid?> CurrentProjectAsync(AppDbContext db, long chat, Guid user, CancellationToken ct)
    {
        var project = await db.TelegramLinks.AsNoTracking().Where(l => l.ChatId == chat).Select(l => l.ProjectId).FirstOrDefaultAsync(ct);
        return project is { } id && await db.Projects.AnyAsync(p => p.Id == id && p.UserId == user, ct) ? id : null;
    }
    /// <summary>The project the chat's next message goes to; null makes the next message start a new one.</summary>
    private static Task TalkInAsync(AppDbContext db, long chat, Guid? project, CancellationToken ct) =>
        db.TelegramLinks.Where(l => l.ChatId == chat).ExecuteUpdateAsync(s => s.SetProperty(l => l.ProjectId, project), ct);
    /// <summary>What "New chat" is on the site. A half-made template or edit would catch the next message instead, so it is dropped too.</summary>
    private async Task NewChatAsync(AppDbContext db, long chat, bool persian, CancellationToken ct)
    {
        pending.TryRemove(chat, out _);
        await TalkInAsync(db, chat, null, ct);
        await SayAsync(chat, "newChat", persian, ct);
    }
    private async Task RefusedAsync(long chat, bool persian, Exception ex, CancellationToken ct)
    {
        var said = BotMessages.Text(ex is KeyNotFoundException ? "NotFound" : ex.Message, persian, browser.Value.MaxInputs);
        if (ex.Message is "InsufficientCredits" or "PackageLimitReached")
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            said += await Joining(scope, db).WhereToBuyAsync(await UserAsync(db, chat, ct), persian, ct);
        }
        await client.SendTextAsync(chat, said, ct);
    }
    /// <summary>Each photo becomes an upload of this user's, judged by its bytes like one picked in the browser. Null, having said why, when one cannot be used.</summary>
    private async Task<List<InputRef>?> InputsAsync(AppDbContext db, IServiceScope scope, long chat, bool persian, AppUser user, IReadOnlyList<string> files, CancellationToken ct)
    {
        var inputs = new List<InputRef>();
        // More photos than a generation can carry is worth saying out loud: silently dropping the rest would look like the bot ignored them.
        if (files.Count > browser.Value.MaxInputs) await client.SendTextAsync(chat, BotMessages.Text("TooManyInputs", persian, browser.Value.MaxInputs), ct);
        foreach (var file in files.Take(browser.Value.MaxInputs))
        {
            if (await UploadAsync(db, scope, user.Id, file, ct) is not { } upload) { await SayAsync(chat, "photoRejected", persian, ct); return null; }
            inputs.Add(new InputRef(upload, null));
        }
        return inputs;
    }
    private async Task<Guid?> UploadAsync(AppDbContext db, IServiceScope scope, Guid user, string fileId, CancellationToken ct)
    {
        var bytes = await client.DownloadAsync(fileId, Controllers.UploadsController.MaxFileBytes, ct);
        if (bytes == null || ImageStorage.ExtensionFor(bytes) == null) return null;
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        var upload = new Upload { UserId = user, Bytes = bytes.Length };
        upload.Path = await storage.SaveUploadAsync(user, upload.Id, bytes, ct);
        upload.ContentType = ImageStorage.ContentTypeFor(upload.Path);
        db.Uploads.Add(upload); await db.SaveChangesAsync(ct);
        return upload.Id;
    }

    // ---- Where the work stands, and the result ----

    /// <summary>Records that this chat asked for the work, and posts the one line that will say where it stands until the result replaces it. The chat is
    /// talking in the work's project from here on, whatever made it: a message, a template, an edit or another version.</summary>
    private async Task TrackAsync(AppDbContext db, long chat, bool persian, Generation generation, CancellationToken ct)
    {
        await TalkInAsync(db, chat, generation.ProjectId, ct);
        var line = StatusLine(generation.Id, generation.UserId, RunStatus.Queued, persian);
        int? message = null;
        try { message = await client.SendTextAsync(chat, line, ct); statusLines[generation.Id] = line; }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Telegram status line could not be sent ({Type})", ex.GetType().Name); }
        db.BotRequests.Add(new BotRequest { GenerationId = generation.Id, ChatId = chat, StatusMessageId = message });
        await db.SaveChangesAsync(ct);
    }
    /// <summary>Waiting: how many are ahead and roughly how long, the same numbers the web shows and never whose. Running: that it is being made.</summary>
    private string StatusLine(Guid generation, Guid user, RunStatus status, bool persian)
    {
        if (status != RunStatus.Queued) return BotMessages.Text("statusWorking", persian);
        var position = queue.For(user).FirstOrDefault(p => p.Id == generation);
        if (position.Id == Guid.Empty) return BotMessages.Text("statusQueued", persian);
        if (position.Ahead == 0) return BotMessages.Text("statusNext", persian);
        return position.EtaSeconds is { } eta
            ? BotMessages.Text("statusAhead", persian, BotMessages.Number(position.Ahead, persian), BotMessages.Number(Math.Max(1, (eta + 59) / 60), persian))
            : BotMessages.Text("statusAheadOnly", persian, BotMessages.Number(position.Ahead, persian));
    }
    private async Task UpdateStatusAsync(long chat, int message, Guid generation, string line, CancellationToken ct)
    {
        if (statusLines.TryGetValue(generation, out var said) && said == line) return;
        try { await client.EditTextAsync(chat, message, line, ct); statusLines[generation] = line; }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Telegram status line could not be edited ({Type})", ex.GetType().Name); }
    }
    /// <summary>A waiting request's place moves as others finish, which no event of its own announces; this looks every few seconds.</summary>
    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); if (bot.Configured) await RefreshWaitingAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Telegram status refresh failed ({Type})", ex.GetType().Name); }
        }
    }
    public async Task RefreshWaitingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var waiting = await db.BotRequests.AsNoTracking().Where(r => r.StatusMessageId != null)
            .Join(db.Generations.Where(g => g.Status == RunStatus.Queued), r => r.GenerationId, g => g.Id, (r, g) => new { r.ChatId, Message = r.StatusMessageId!.Value, g.Id, g.UserId })
            .ToListAsync(ct);
        foreach (var w in waiting) await UpdateStatusAsync(w.ChatId, w.Message, w.Id, StatusLine(w.Id, w.UserId, RunStatus.Queued, persianChats.GetValueOrDefault(w.ChatId)), ct);
    }
    /// <summary>Answers a finished generation in the chat that asked for it, and keeps its status line current until then. Only work that came from the
    /// bot: the same person's work on the web is already in front of them, and a photo arriving unasked for in a chat would be a surprise, not a service.</summary>
    public async Task DeliverAsync(GenerationEvent moved, CancellationToken ct)
    {
        if (!bot.Configured) return;
        var final = moved.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Answered;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.BotRequests.AsNoTracking().FirstOrDefaultAsync(r => r.GenerationId == moved.Id, ct);
        long chat;
        if (request is not null) chat = request.ChatId;
        else
        {
            // Work queued before requests were recorded is recognised the old way: the bot's own project, answered in the chat linked to its user.
            if (!final || await db.TelegramLinks.AsNoTracking().Where(l => l.UserId == moved.UserId).Select(l => (long?)l.ChatId).FirstOrDefaultAsync(ct) is not { } linked) return;
            if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == moved.ProjectId && p.UserId == moved.UserId && p.Title == BotProject, ct)) return;
            chat = linked;
        }
        var persian = persianChats.GetValueOrDefault(chat);
        if (!final)
        {
            if (request?.StatusMessageId is { } line) await UpdateStatusAsync(chat, line, moved.Id, StatusLine(moved.Id, moved.UserId, moved.Status, persian), ct);
            return;
        }
        // The result takes the status line's place rather than arriving under a line that still says it is being made.
        if (request?.StatusMessageId is { } done && statusLines.TryRemove(moved.Id, out _))
            try { await client.DeleteAsync(chat, done, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Telegram status line could not be removed ({Type})", ex.GetType().Name); }
        // The model answered in words: they are the person's to read, and whatever they send next continues that conversation.
        if (moved.Status == RunStatus.Answered)
        {
            var words = string.IsNullOrWhiteSpace(moved.ReplyText) ? BotMessages.Text("Answered", persian) : moved.ReplyText!;
            await client.SendTextAsync(chat, words.Length > 3900 ? words[..3900] + "…" : words, ct);
            return;
        }
        if (moved.Status != RunStatus.Completed || moved.ImagePath == null)
        {
            await client.SendTextAsync(chat, BotMessages.Text(moved.Status == RunStatus.Cancelled ? "Cancelled" : moved.ErrorMessage ?? "AutomationFailed", persian), ct);
            return;
        }
        var images = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        if (!File.Exists(images.Resolve(moved.ImagePath))) { await SayAsync(chat, "InputMissing", persian, ct); return; }
        var package = await db.Generations.AsNoTracking().Where(g => g.Id == moved.Id).Select(g => g.PackageId).FirstOrDefaultAsync(ct);
        var path = images.Resolve(await scope.ServiceProvider.GetRequiredService<Watermark>().DeliveredAsync(db, package, moved.ImagePath, ct));
        var said = moved.TemplateTitle is { } title
            ? BotMessages.Text("templateCaptionTitle", persian, title) + (string.IsNullOrWhiteSpace(moved.Prompt) ? "" : "\n" + moved.Prompt)
            : moved.Prompt;
        var caption = said.Length > 900 ? said[..900] + "…" : said;
        // A reply's words answered a question in a conversation that has moved on since, so sending them again would not make another version.
        var repeatable = await db.Generations.AsNoTracking().Where(g => g.Id == moved.Id).Select(g => g.Operation != Operation.Reply).FirstOrDefaultAsync(ct);
        var first = new List<BotButton> { new(BotMessages.Text("btnEdit", persian), $"edit:{moved.Id:N}") };
        if (repeatable) first.Add(new(BotMessages.Text("btnAgain", persian), $"again:{moved.Id:N}"));
        await client.SendPhotoAsync(chat, await File.ReadAllBytesAsync(path, ct), $"loomi-{moved.Id}{Path.GetExtension(path)}", caption.Length == 0 ? null : caption, ct,
            Keys(first, Row(new BotButton(BotMessages.Text("btnFile", persian), $"file:{moved.Id:N}"), new BotButton(BotMessages.Text("btnNewChat", persian), "new"))));
    }
}
