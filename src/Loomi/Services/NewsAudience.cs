using System.Globalization;
using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>How many people each kind of news would reach right now, whatever its switch says, so the owner can read the numbers before turning one on.</summary>
public record NewsReach(int NewTemplates, int WinBack, int LowCredit, int WaitingTemplates, DateTime? LastDigestAt);
/// <summary>Who the bot's own news is for. The worker and the owner's card ask the same questions here, so the number on the card is the number who would hear.</summary>
public class NewsAudience(AppDbContext db, GrowthSettings growth, IImageStorage storage)
{
    private static readonly TimeSpan QuietFor = TimeSpan.FromDays(14), InviteEvery = TimeSpan.FromDays(30);
    /// <summary>Telegram refuses a photo larger than this, and the owner may keep a sample of twice that.</summary>
    private const long MaxPhotoBytes = 10 * 1024 * 1024;
    private const int LowCreditImages = 2;
    /// <summary>Members only, with a chat, not disabled, and not having asked for it to stop. The owner reads about all this on the bot page instead.</summary>
    public IQueryable<TelegramLink> Reachable() => db.TelegramLinks.Where(l => !l.NotificationsOff && db.Users.Any(u => u.Id == l.UserId && u.Role == UserRole.Member && !u.IsDisabled));
    /// <summary>Someone who has used Loomi and then gone quiet, and has not been invited back lately. Someone who never asked for anything is not coming back to anything.</summary>
    public IQueryable<TelegramLink> InvitedBack(DateTime utcNow)
    {
        var active = utcNow - QuietFor;
        var invited = utcNow - InviteEvery;
        return Reachable().Where(l => (l.LastNudgeAt == null || l.LastNudgeAt < invited) && db.Generations.Any(g => g.UserId == l.UserId) && !db.Generations.Any(g => g.UserId == l.UserId && g.CreatedAt > active));
    }
    /// <summary>What a person's next image costs in the common case. A price nobody set is zero, and then nobody can be low.</summary>
    public Task<int> ImagePriceAsync(CancellationToken ct) => db.PriceAsync(Provider.ChatGPT, Operation.Generate, ct);
    /// <summary>People who have used Loomi and are about to be refused for credits, with what they have. A running Unlimited or Daily package pays before
    /// credits do, so its holder is not about to be refused; one that is only scheduled pays for nothing yet.</summary>
    public async Task<List<(TelegramLink Link, int Credits)>> RunningLowAsync(DateTime utcNow, int price, CancellationToken ct)
    {
        if (price <= 0) return [];
        var links = await Reachable().Where(l => l.LowCreditNudgeAt == null && db.Generations.Any(g => g.UserId == l.UserId)
            && !db.CreditPackages.Any(p => p.UserId == l.UserId && p.Kind != PackageKind.Credits && p.StartsAt <= utcNow && p.EndsAt > utcNow)).ToListAsync(ct);
        var credits = await SpendableAsync(links.Select(l => l.UserId).ToList(), utcNow, ct);
        return links.Select(l => (l, credits.GetValueOrDefault(l.UserId))).Where(x => x.Item2 < LowCreditImages * price).ToList();
    }
    /// <summary>Forgets having told someone they were low once their credits are back above the line, so the next dip tells them again. Only credits count
    /// here: a Daily package hides the low balance for a while, it does not fill it.</summary>
    public async Task ClearToppedUpAsync(DateTime utcNow, CancellationToken ct)
    {
        var told = await db.TelegramLinks.Where(l => l.LowCreditNudgeAt != null).ToListAsync(ct);
        if (told.Count == 0) return;
        var line = LowCreditImages * await ImagePriceAsync(ct);
        var credits = await SpendableAsync(told.Select(l => l.UserId).ToList(), utcNow, ct);
        foreach (var link in told.Where(l => credits.GetValueOrDefault(l.UserId) >= line)) link.LowCreditNudgeAt = null;
        await db.SaveChangesAsync(ct);
    }
    /// <summary>The regular balance plus what the running Credits packages have left, the same money <see cref="Credits.ChargeAsync"/> would reach for. Per
    /// person in one go, so a large workspace is not added up row by row.</summary>
    private async Task<Dictionary<Guid, int>> SpendableAsync(List<Guid> users, DateTime utcNow, CancellationToken ct)
    {
        var spendable = await db.CreditEntries.AsNoTracking().Where(e => e.PackageId == null && users.Contains(e.UserId)).GroupBy(e => e.UserId)
            .Select(x => new { x.Key, Sum = x.Sum(e => e.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var running = await db.CreditPackages.AsNoTracking().Where(p => p.Kind == PackageKind.Credits && users.Contains(p.UserId)).ActiveAt(utcNow).ToListAsync(ct);
        var left = await db.LeftAsync(running, ct);
        foreach (var package in running) spendable[package.UserId] = spendable.GetValueOrDefault(package.UserId) + left[package.Id];
        return spendable;
    }
    /// <summary>What the next digest would cover, newest first, tracked so it can be stamped. A sample Telegram cannot be shown would make a digest that fails for
    /// everyone and a button that answers "not found", so its template keeps waiting until the owner replaces it.</summary>
    public async Task<List<PromptTemplate>> WaitingTemplatesAsync(CancellationToken ct) =>
        (await db.Templates.Where(t => t.IsActive && t.SamplePath != null && t.AnnouncedAt == null).OrderByDescending(t => t.CreatedAt).ToListAsync(ct))
            .Where(t => new FileInfo(SamplePath(t)) is { Exists: true, Length: <= MaxPhotoBytes }).ToList();
    public string SamplePath(PromptTemplate template) => storage.Resolve(template.SamplePath!);
    /// <summary>Read as UTC, however it was written, so the browser turns it into its own clock. Null until one has gone out, or when the last one had to be taken back.</summary>
    public async Task<DateTime?> LastDigestAsync(CancellationToken ct) =>
        DateTime.TryParse(await growth.GetAsync(GrowthSettings.NotifyDigestAt, ct), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    public async Task<NewsReach> ReachAsync(DateTime utcNow, CancellationToken ct) => new(
        await Reachable().CountAsync(ct),
        await InvitedBack(utcNow).CountAsync(ct),
        (await RunningLowAsync(utcNow, await ImagePriceAsync(ct), ct)).Count,
        (await WaitingTemplatesAsync(ct)).Count,
        await LastDigestAsync(ct));
}
