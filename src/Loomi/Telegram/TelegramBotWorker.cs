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
/// <summary>The bot, in one place. It talks to the same <see cref="GenerationService"/> the web and the API do, so quota, credits and pricing are the
/// same rules however the work arrived; nothing here calls Loomi's own HTTP surface.</summary>
public class TelegramBotWorker(IServiceScopeFactory scopes, ITelegramClient client, GenerationEvents events, IOptions<TelegramOptions> telegram,
                               IOptions<BrowserOptions> browser, ILogger<TelegramBotWorker> logger) : BackgroundService
{
    /// <summary>Everything from a chat lands in this one project, so the bot can tell its own work from the same person's work on the web.</summary>
    public const string BotProject = "Telegram";
    private readonly TelegramOptions settings = telegram.Value;
    /// <summary>The language last seen from a chat. A finished generation arrives with no message behind it, so the answer has to remember how to speak.</summary>
    private readonly ConcurrentDictionary<long, bool> persianChats = new();
    private readonly ConcurrentDictionary<string, Album> albums = new();
    /// <summary>Photos of one album arrive as separate messages with no marker for the last, so the only way to know it is complete is that none has arrived for a moment.</summary>
    private sealed class Album { public long Chat; public bool Persian; public string? Caption; public List<string> Files = []; public DateTime Last; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No token means the bot is not part of this deployment. Say nothing: an operator who did not configure one does not need a warning every start.
        if (string.IsNullOrWhiteSpace(settings.BotToken)) return;
        events.Subscribe(DeliverAsync);
        var albumLoop = Task.Run(() => FlushLoopAsync(stoppingToken), stoppingToken);
        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await client.GetUpdatesAsync(offset, stoppingToken))
                {
                    offset = update.Id + 1;
                    if (update.Message is { } message) await HandleAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Never the exception text: it can carry the bot token from a request URL.
            catch (Exception ex) { logger.LogWarning("Telegram polling failed ({Type})", ex.GetType().Name); await Task.Delay(5000, stoppingToken); }
        }
        events.Unsubscribe(DeliverAsync);
        await albumLoop;
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
            if (string.IsNullOrWhiteSpace(album.Caption)) { await SayAsync(album.Chat, "needCaption", album.Persian, ct); continue; }
            await SubmitAsync(album.Chat, album.Persian, album.Caption!, album.Files, ct);
        }
    }
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var persian = BotMessages.IsPersian(message.From?.LanguageCode);
        persianChats[chat] = persian;
        var text = (message.Text ?? message.Caption ?? "").Trim();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var code = text[6..].Trim();
            if (code.Length == 0) { await SayAsync(chat, "linkFirst", persian, ct); return; }
            var outcome = await Linking.RedeemAsync(db, code, chat, DateTime.UtcNow, ct);
            await client.SendTextAsync(chat, BotMessages.Text(outcome.Key, persian, outcome.User?.Username ?? ""), ct);
            return;
        }
        // Everything past this point acts as a Loomi user, so an unlinked chat stops here and never reaches the queue.
        var user = await UserAsync(db, chat, ct);
        if (user == null) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/help": await SayAsync(chat, "help", persian, ct); return;
            case "/balance": await BalanceAsync(db, chat, user, persian, ct); return;
            case "/status": await StatusAsync(db, chat, user, persian, ct); return;
            case "/cancel": await CancelAsync(db, chat, user, persian, ct); return;
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
            if (text.Length == 0) { await SayAsync(chat, "needCaption", persian, ct); return; }
            await SubmitAsync(chat, persian, text, [file], ct);
            return;
        }
        if (text.Length == 0 || text.StartsWith('/')) { await SayAsync(chat, "help", persian, ct); return; }
        await SubmitAsync(chat, persian, text, [], ct);
    }
    private static async Task<AppUser?> UserAsync(AppDbContext db, long chat, CancellationToken ct) =>
        await db.TelegramLinks.AsNoTracking().Where(l => l.ChatId == chat)
            .Join(db.Users.Where(u => !u.IsDisabled), l => l.UserId, u => u.Id, (_, u) => u).FirstOrDefaultAsync(ct);
    private Task SayAsync(long chat, string key, bool persian, CancellationToken ct) => client.SendTextAsync(chat, BotMessages.Text(key, persian), ct);
    private async Task BalanceAsync(AppDbContext db, long chat, AppUser user, bool persian, CancellationToken ct)
    {
        var used = await db.UsedByAsync(user.Id, ct);
        // The owner is not charged, so a balance for them would be a number with nothing behind it — the same reason the web hides it.
        await client.SendTextAsync(chat, user.Role == UserRole.Owner
            ? BotMessages.Text("balanceOwner", persian, used)
            : BotMessages.Text("balance", persian, await db.BalanceAsync(user.Id, ct), used), ct);
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
    private async Task SubmitAsync(long chat, bool persian, string prompt, IReadOnlyList<string> files, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await UserAsync(db, chat, ct);
        if (user == null) { await SayAsync(chat, "linkFirst", persian, ct); return; }
        var viewer = Viewer.For(user);
        try
        {
            var inputs = new List<InputRef>();
            // More photos than a generation can carry is worth saying out loud: silently dropping the rest would look like the bot ignored them.
            if (files.Count > browser.Value.MaxInputs) await client.SendTextAsync(chat, BotMessages.Text("TooManyInputs", persian, browser.Value.MaxInputs), ct);
            foreach (var file in files.Take(browser.Value.MaxInputs))
            {
                if (await UploadAsync(db, scope, user.Id, file, ct) is not { } upload) { await SayAsync(chat, "photoRejected", persian, ct); return; }
                inputs.Add(new InputRef(upload, null));
            }
            var project = await ProjectAsync(db, user.Id, ct);
            await scope.ServiceProvider.GetRequiredService<GenerationService>()
                .SubmitAsync(viewer, project, prompt, Provider.ChatGPT, Operation.Generate, null, inputs, ct);
            await SayAsync(chat, "queued", persian, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            await client.SendTextAsync(chat, BotMessages.Text(ex is KeyNotFoundException ? "NotFound" : ex.Message, persian, browser.Value.MaxInputs), ct);
        }
    }
    /// <summary>The bot's own project for this user, made the first time they send anything.</summary>
    private static async Task<Guid> ProjectAsync(AppDbContext db, Guid user, CancellationToken ct)
    {
        if (await db.Projects.Where(p => p.UserId == user && p.Title == BotProject).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct) is { } existing) return existing;
        var project = new ImageProject { UserId = user, Title = BotProject };
        db.Projects.Add(project); await db.SaveChangesAsync(ct);
        return project.Id;
    }
    /// <summary>A photo from a chat becomes an ordinary upload of that user's, judged by its bytes exactly like one picked in the browser.</summary>
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
    /// <summary>Answers a finished generation in the chat that asked for it. Only work that came from the bot: the same person's work on the web is
    /// already in front of them, and a photo arriving unasked for in a chat would be a surprise, not a service.</summary>
    public async Task DeliverAsync(GenerationEvent moved, CancellationToken ct)
    {
        if (moved.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.TelegramLinks.AsNoTracking().Where(l => l.UserId == moved.UserId).Select(l => (long?)l.ChatId).FirstOrDefaultAsync(ct) is not { } chat) return;
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == moved.ProjectId && p.UserId == moved.UserId && p.Title == BotProject, ct)) return;
        var persian = persianChats.GetValueOrDefault(chat);
        if (moved.Status != RunStatus.Completed || moved.ImagePath == null)
        {
            await client.SendTextAsync(chat, BotMessages.Text(moved.Status == RunStatus.Cancelled ? "Cancelled" : moved.ErrorMessage ?? "AutomationFailed", persian), ct);
            return;
        }
        var path = scope.ServiceProvider.GetRequiredService<IImageStorage>().Resolve(moved.ImagePath);
        if (!File.Exists(path)) { await SayAsync(chat, "InputMissing", persian, ct); return; }
        var caption = moved.Prompt.Length > 900 ? moved.Prompt[..900] + "…" : moved.Prompt;
        await client.SendPhotoAsync(chat, await File.ReadAllBytesAsync(path, ct), $"loomi-{moved.Id}{Path.GetExtension(path)}", caption, ct);
    }
}
