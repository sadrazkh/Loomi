using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
// Nullable on purpose, as in UsersController; Kind is a name rather than the enum so a misspelt one is InvalidPackage too, not a ProblemDetails.
public record GrantPackage(string? Kind, int? Amount, int? Days, string? Name, string? Note, bool? StartAfterCurrent, bool? Notify);
public record ExtendPackage(int? Days);
/// <summary>Timed packages. The owner hands them to members by hand, since payment happens outside the app; anyone signed in reads their own.</summary>
[ApiController, Authorize, Route("api")]
public class PackagesController(AppDbContext db, ITelegramClient telegram, BotSettings bot, ILogger<PackagesController> logger) : ControllerBase
{
    private const int MaxDays = 3650, MaxName = 80, MaxNote = 400;
    private const string Owner = nameof(UserRole.Owner);
    private static BadRequestObjectResult Invalid() => new(new { error = "InvalidPackage" });
    /// <summary>The range each kind's Amount may take: credits, a daily cap where zero is none, or free images a day.</summary>
    private static bool ValidAmount(PackageKind kind, int amount) => kind switch
    {
        PackageKind.Credits => amount is >= 1 and <= 1_000_000,
        PackageKind.Unlimited => amount is >= 0 and <= 10_000,
        _ => amount is >= 1 and <= 10_000,
    };
    /// <summary>By name, as every response writes it. A number that happens to parse is not a kind.</summary>
    private static PackageKind? KindOf(string? name) =>
        name is null ? null : Enum.GetValues<PackageKind>().Cast<PackageKind?>().FirstOrDefault(k => k.ToString()!.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    private async Task<AppUser> UserAsync(Guid id, CancellationToken ct) => await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();
    private async Task<CreditPackage> PackageAsync(Guid user, Guid id, CancellationToken ct) =>
        await db.CreditPackages.FirstOrDefaultAsync(p => p.Id == id && p.UserId == user, ct) ?? throw new KeyNotFoundException();
    private async Task<PackageDto> DescribeAsync(CreditPackage package, DateTime now, CancellationToken ct) => (await db.DescribeAsync([package], now, withNote: true, ct)).Single();

    /// <summary>The caller's own. The owner's note about a package is not theirs to read.</summary>
    [HttpGet("packages")] public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await db.MineAsync(Viewer.From(User).Id, DateTime.UtcNow, ct));

    [Authorize(Roles = Owner), HttpGet("users/{id:guid}/packages")] public async Task<IActionResult> List(Guid id, CancellationToken ct)
    {
        await UserAsync(id, ct);
        var packages = await db.CreditPackages.AsNoTracking().Where(p => p.UserId == id).ToListAsync(ct);
        return Ok(await db.DescribeAsync(packages.OrderByDescending(p => p.CreatedAt).ToList(), DateTime.UtcNow, withNote: true, ct));
    }
    /// <summary>Starting after the current one queues it behind the latest of its kind still running or waiting, so a renewal bought early is not
    /// spent at the same time as the package it renews.</summary>
    [Authorize(Roles = Owner), HttpPost("users/{id:guid}/packages")] public async Task<IActionResult> Grant(Guid id, GrantPackage request, CancellationToken ct)
    {
        var user = await UserAsync(id, ct);
        var name = request.Name?.Trim() ?? "";
        var note = request.Note?.Trim();
        // The owner is never charged, so a package of theirs would be one that does nothing.
        if (user.Role != UserRole.Member || KindOf(request.Kind) is not { } kind || request.Amount is not { } amount || !ValidAmount(kind, amount)
            || request.Days is not (>= 1 and <= MaxDays) || name.Length > MaxName || note?.Length > MaxNote) return Invalid();
        var now = DateTime.UtcNow;
        var starts = now;
        if (request.StartAfterCurrent == true)
        {
            // Running or waiting is simply not yet ended: an ended one, by hand or by time, has an end that has passed.
            var current = await db.CreditPackages.AsNoTracking().Where(p => p.UserId == id && p.Kind == kind && p.EndsAt > now).Select(p => p.EndsAt).ToListAsync(ct);
            if (current.Count > 0) starts = DateTime.SpecifyKind(current.Max(), DateTimeKind.Utc);
        }
        var package = new CreditPackage { UserId = id, Kind = kind, Amount = amount, Name = name.Length == 0 ? Packages.DefaultName(kind, amount) : name, StartsAt = starts,
                                          EndsAt = starts.AddDays(request.Days.Value), CreatedAt = now, ByUserId = Viewer.From(User).Id, Note = string.IsNullOrEmpty(note) ? null : note };
        db.CreditPackages.Add(package);
        await db.SaveChangesAsync(ct);
        if (request.Notify == true) await NotifyAsync(package, now, ct);
        return Created($"/api/users/{id}/packages", await DescribeAsync(package, now, ct));
    }
    /// <summary>Counted from its end, or from now once it has ended, so extending an ended package brings it back for that many days. Its notices are
    /// cleared, so the stretched package is reminded about and announced again when it really ends.</summary>
    [Authorize(Roles = Owner), HttpPost("users/{id:guid}/packages/{packageId:guid}/extend")] public async Task<IActionResult> Extend(Guid id, Guid packageId, ExtendPackage request, CancellationToken ct)
    {
        if (request.Days is not (>= 1 and <= MaxDays)) return Invalid();
        var package = await PackageAsync(id, packageId, ct);
        var now = DateTime.UtcNow;
        package.EndsAt = (package.EndsAt > now ? package.EndsAt : now).AddDays(request.Days.Value);
        package.EndedEarlyAt = null; package.ReminderSentAt = null; package.EndNoticeSentAt = null;
        await db.SaveChangesAsync(ct);
        return Ok(await DescribeAsync(package, now, ct));
    }
    /// <summary>Stops it now. One still waiting to start never does: its start moves to now as well, so it ends where it began. One already over is left as it is.</summary>
    [Authorize(Roles = Owner), HttpPost("users/{id:guid}/packages/{packageId:guid}/end")] public async Task<IActionResult> End(Guid id, Guid packageId, CancellationToken ct)
    {
        var package = await PackageAsync(id, packageId, ct);
        var now = DateTime.UtcNow;
        if (package.EndsAt > now)
        {
            if (package.StartsAt > now) package.StartsAt = now;
            package.EndsAt = now; package.EndedEarlyAt = now;
            await db.SaveChangesAsync(ct);
        }
        return Ok(await DescribeAsync(package, now, ct));
    }
    /// <summary>Best effort: the package is given whether or not Telegram hears about it, and a bot that is down never fails the grant.</summary>
    private async Task NotifyAsync(CreditPackage package, DateTime now, CancellationToken ct)
    {
        if (!bot.Configured) return;
        try
        {
            if (package.StartsAt > now)
                await PackageReminderWorker.TellAsync(db, telegram, logger, package.UserId, "packageScheduled",
                    persian => [package.Name, PackageReminderWorker.Date(package.StartsAt, persian), PackageReminderWorker.Date(package.EndsAt, persian)], ct);
            else
                await PackageReminderWorker.TellAsync(db, telegram, logger, package.UserId, "packageGranted", persian => [package.Name, PackageReminderWorker.Date(package.EndsAt, persian)], ct);
        }
        // Never the exception text: it can carry the bot token from a request URL.
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Package notice could not be sent ({Type})", ex.GetType().Name); }
    }
}
