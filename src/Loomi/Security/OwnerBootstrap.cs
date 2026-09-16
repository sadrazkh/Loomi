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
        if (await db.Users.AnyAsync(ct)) return;
        var owner = new AppUser { Username = Username, NormalizedUsername = Username, Role = UserRole.Owner, DailyQuota = int.MaxValue };
        owner.PasswordHash = passwords.Hash(owner, accessKey);
        db.Users.Add(owner);
        await db.SaveChangesAsync(ct);
        // Work saved before Loomi had users carries an empty UserId. It is this owner's; left alone it would belong to nobody and vanish from their workspace.
        await db.Projects.Where(x => x.UserId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, owner.Id), ct);
        await db.Generations.Where(x => x.UserId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, owner.Id), ct);
    }
    /// <summary>An install that logged in through the old single-browser flow has a "default" profile but no account row. Adopt it, or its ChatGPT login is stranded.</summary>
    private static async Task AdoptAccountAsync(AppDbContext db, string storageRoot, CancellationToken ct)
    {
        if (await db.Accounts.AnyAsync(ct)) return;
        if (!Directory.Exists(Path.Combine(storageRoot, "profiles", "default"))) return;
        db.Accounts.Add(new ProviderAccount { Provider = Provider.ChatGPT, Kind = AccountKind.Browser, Label = "ChatGPT", ProfileDirectory = "default" });
        await db.SaveChangesAsync(ct);
    }
}
