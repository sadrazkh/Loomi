using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Security;
/// <summary>Seeds the first account from the configured access key, so a fresh install still signs in with no setup.</summary>
public static class OwnerBootstrap
{
    public const string Username = "owner";
    public static async Task EnsureAsync(AppDbContext db, PasswordService passwords, string accessKey, string storageRoot, CancellationToken ct = default)
    {
        await AdoptAccountAsync(db, storageRoot, ct);
        await SeedPricesAsync(db, ct);
        await NormaliseOwnerQuotaAsync(db, ct);
        if (await db.Users.AnyAsync(ct)) return;
        var owner = new AppUser { Username = Username, NormalizedUsername = Username, Role = UserRole.Owner, DailyQuota = 0 };
        owner.PasswordHash = passwords.Hash(owner, accessKey);
        db.Users.Add(owner);
        await db.SaveChangesAsync(ct);
        // Work saved before Loomi had users carries an empty UserId. It is this owner's; left alone it would belong to nobody and vanish from their workspace.
        await db.Projects.Where(x => x.UserId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, owner.Id), ct);
        await db.Generations.Where(x => x.UserId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, owner.Id), ct);
    }
    /// <summary>A workspace with no prices would run everything for free and never say why. These are a starting point the owner edits.</summary>
    private static async Task SeedPricesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.PricingRules.AnyAsync(ct)) return;
        db.PricingRules.AddRange(
            new PricingRule { Provider = Provider.ChatGPT, Cost = 10 },
            new PricingRule { Provider = Provider.Gemini, Cost = 5 });
        await db.SaveChangesAsync(ct);
    }
    /// <summary>The sentinel an earlier version used for "no limit" now reads as a ceiling of two billion; zero is what says unlimited.</summary>
    private static Task NormaliseOwnerQuotaAsync(AppDbContext db, CancellationToken ct) =>
        db.Users.Where(u => u.DailyQuota == int.MaxValue).ExecuteUpdateAsync(s => s.SetProperty(u => u.DailyQuota, 0), ct);
    /// <summary>An install that logged in through the old single-browser flow has a "default" profile but no account row. Adopt it, or its ChatGPT login is stranded.</summary>
    private static async Task AdoptAccountAsync(AppDbContext db, string storageRoot, CancellationToken ct)
    {
        if (await db.Accounts.AnyAsync(ct)) return;
        if (!Directory.Exists(Path.Combine(storageRoot, "profiles", "default"))) return;
        db.Accounts.Add(new ProviderAccount { Provider = Provider.ChatGPT, Kind = AccountKind.Browser, Label = "ChatGPT", ProfileDirectory = "default" });
        await db.SaveChangesAsync(ct);
    }
}
