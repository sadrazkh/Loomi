using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>A span of local days ending today: today alone, the last 7, the last 30, or everything.</summary>
public enum UsagePeriod { Today, Week, Month, All }
public record TemplateCount(string Title, int Count);
/// <summary>One person's use over a period. CreditsLeft is null for an owner, who is never charged; DailyQuota zero means no daily limit.</summary>
public record UserUsage(Guid UserId, string Username, UserRole Role, bool IsDisabled, int Requests, int Images, int Answers, int Failed, int Cancelled,
    int NewChats, int FollowUps, int Web, int Bot, int Api, int CreditsSpent, int? CreditsLeft, int UsedToday, int DailyQuota, DateTime? LastActive,
    IReadOnlyList<TemplateCount> Templates);
/// <summary>Everybody's use over a period, busiest first, people who did nothing included. Since is the UTC instant the period starts, null for all time.</summary>
public record UsageSummary(UsagePeriod Period, DateTime? Since, int ActiveUsers, int Requests, int Images, int Failed, int CreditsSpent, IReadOnlyList<UserUsage> Users);
/// <summary>One request as the owner reads it in a person's history.</summary>
public record UsageItem(Guid Id, Guid ProjectId, string ProjectTitle, DateTime CreatedAt, string Prompt, Operation Operation, RunStatus Status, Channel Channel,
    string? TemplateTitle, int CreditCost, bool HasImage, string? ReplyText, string? ErrorMessage);
public class ReportOptions
{
    /// <summary>The local hour from which the day's summary goes to the owner in Telegram.</summary>
    public int DailyHour { get; set; } = 23;
}
/// <summary>What the owner reads about everybody's use, on the page and in the bot. Counted from the requests and the credit ledger each time it is asked,
/// the way the daily quota is, so there is no counter to drift out of step with what actually happened.</summary>
public class UsageReport(AppDbContext db)
{
    public const string DailyKey = "Reports.Daily", DailySentKey = "Reports.DailySentOn";
    public const int MaxPage = 50;
    private const int TopTemplates = 3;
    private sealed record Row(Guid UserId, RunStatus Status, Operation Operation, Channel Channel, string? TemplateTitle);
    /// <summary>Local midnight, so a period is whole days as the people using Loomi count them; the week and the month end with today.</summary>
    public static DateTime? Since(UsagePeriod period) => period switch
    {
        UsagePeriod.Today => Quota.Midnight(),
        UsagePeriod.Week => Quota.Midnight().AddDays(-6),
        UsagePeriod.Month => Quota.Midnight().AddDays(-29),
        _ => null,
    };
    /// <summary>The names the page and the API use. Nothing given means today.</summary>
    public static bool TryParse(string? value, out UsagePeriod period)
    {
        UsagePeriod? found = string.IsNullOrEmpty(value) ? UsagePeriod.Today : value.ToLowerInvariant() switch
        {
            "today" => UsagePeriod.Today, "week" => UsagePeriod.Week, "month" => UsagePeriod.Month, "all" => UsagePeriod.All, _ => null,
        };
        period = found ?? UsagePeriod.Today;
        return found != null;
    }
    public async Task<UsageSummary> SummaryAsync(UsagePeriod period, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        var rows = await ReadAsync(period, users.Select(u => u.Id).ToList(), ct);
        var ordered = rows.OrderByDescending(u => u.Requests).ThenByDescending(u => u.LastActive).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
        return new(period, Since(period), ordered.Count(u => u.Requests > 0), ordered.Sum(u => u.Requests), ordered.Sum(u => u.Images), ordered.Sum(u => u.Failed),
            ordered.Sum(u => u.CreditsSpent), ordered);
    }
    public async Task<UserUsage?> UserAsync(Guid user, UsagePeriod period, CancellationToken ct) =>
        await db.Users.AnyAsync(u => u.Id == user, ct) ? (await ReadAsync(period, [user], ct)).Single() : null;
    /// <summary>SQLite hands times back without their kind. They were written as UTC, and said so they reach a browser as UTC rather than as its own clock.</summary>
    private static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc);
    /// <summary>Newest first. A page is at most <see cref="MaxPage"/> long, however many are asked for.</summary>
    public async Task<IReadOnlyList<UsageItem>> RequestsAsync(Guid user, int skip, int take, CancellationToken ct) =>
        (await db.Generations.AsNoTracking().Where(g => g.UserId == user).OrderByDescending(g => g.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxPage))
            .Select(g => new UsageItem(g.Id, g.ProjectId, g.Project.Title, g.CreatedAt, g.Prompt, g.Operation, g.Status, g.Channel, g.TemplateTitle, g.CreditCost,
                g.LocalImagePath != null, g.ReplyText, g.ErrorMessage))
            .ToListAsync(ct)).Select(i => i with { CreatedAt = Utc(i.CreatedAt) }).ToList();
    /// <summary>On unless the owner has switched it off.</summary>
    public async Task<bool> DailyEnabledAsync(CancellationToken ct) => await SettingAsync(DailyKey, ct) != "off";
    public Task SetDailyEnabledAsync(bool enabled, CancellationToken ct) => SetSettingAsync(DailyKey, enabled ? "on" : "off", ct);
    public Task<string?> SettingAsync(string key, CancellationToken ct) => db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    public async Task SetSettingAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null) db.Settings.Add(new AppSetting { Key = key, Value = value }); else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
    private async Task<List<UserUsage>> ReadAsync(UsagePeriod period, List<Guid> ids, CancellationToken ct)
    {
        var since = Since(period);
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        var rows = (await db.Generations.AsNoTracking().Where(g => ids.Contains(g.UserId) && (since == null || g.CreatedAt >= since))
            .Select(g => new Row(g.UserId, g.Status, g.Operation, g.Channel, g.TemplateTitle)).ToListAsync(ct)).ToLookup(r => r.UserId);
        // A charge and its refund cancel out, so what is left is what the person actually paid for; grants and adjustments are the owner's, not use.
        var spent = await db.CreditEntries.AsNoTracking().Where(c => ids.Contains(c.UserId) && (c.Kind == CreditKind.Charge || c.Kind == CreditKind.Refund) && (since == null || c.CreatedAt >= since))
            .GroupBy(c => c.UserId).Select(x => new { x.Key, Sum = x.Sum(c => c.Amount) }).ToDictionaryAsync(x => x.Key, x => -x.Sum, ct);
        var balances = await db.CreditEntries.AsNoTracking().Where(c => ids.Contains(c.UserId)).GroupBy(c => c.UserId)
            .Select(x => new { x.Key, Sum = x.Sum(c => c.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var last = await db.Generations.AsNoTracking().Where(g => ids.Contains(g.UserId)).GroupBy(g => g.UserId)
            .Select(x => new { x.Key, Last = x.Max(g => g.CreatedAt) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var today = await db.UsageAsync(ct);
        return users.Select(u =>
        {
            var mine = rows[u.Id].ToList();
            var templates = mine.Where(r => !string.IsNullOrEmpty(r.TemplateTitle)).GroupBy(r => r.TemplateTitle!)
                .Select(x => new TemplateCount(x.Key, x.Count())).OrderByDescending(t => t.Count).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).Take(TopTemplates).ToList();
            return new UserUsage(u.Id, u.Username, u.Role, u.IsDisabled, mine.Count,
                mine.Count(r => r.Status == RunStatus.Completed), mine.Count(r => r.Status == RunStatus.Answered),
                mine.Count(r => r.Status == RunStatus.Failed), mine.Count(r => r.Status == RunStatus.Cancelled),
                mine.Count(r => r.Operation is Operation.Generate or Operation.Branch), mine.Count(r => r.Operation is Operation.Reply or Operation.Edit),
                mine.Count(r => r.Channel == Channel.Web), mine.Count(r => r.Channel == Channel.Bot), mine.Count(r => r.Channel == Channel.Api),
                spent.GetValueOrDefault(u.Id), u.Role == UserRole.Owner ? null : balances.GetValueOrDefault(u.Id), today.GetValueOrDefault(u.Id), u.DailyQuota,
                last.TryGetValue(u.Id, out var at) ? Utc(at) : null, templates);
        }).ToList();
    }
}
