using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Security;
/// <summary>The bot's one-time way into the web, for an account with no password. It is a credential sent through a chat, so it is long, short-lived, spent on
/// first use, and only its hash is kept: a copy of the database signs nobody in.</summary>
public static class LoginLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int Bytes = 32;
    public static string HashOf(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    /// <summary>One live link per person: asking again replaces the last one, so only the link most recently sent can work. Expired rows of anyone go too,
    /// since nothing else ever reads them.</summary>
    public static async Task<string> IssueAsync(AppDbContext db, Guid user, DateTime now, CancellationToken ct)
    {
        await db.LoginLinks.Where(l => (l.UserId == user && l.UsedAt == null) || l.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));
        db.LoginLinks.Add(new LoginLink { Hash = HashOf(code), UserId = user, ExpiresAt = now + Lifetime });
        await db.SaveChangesAsync(ct);
        return code;
    }
    /// <summary>The person a good link belongs to, with the link spent, or null. The role is asked here as well as when the link was given: one asked for
    /// as a member must not become owner access because the account was promoted in those ten minutes.</summary>
    public static async Task<AppUser?> RedeemAsync(AppDbContext db, string? code, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var hash = HashOf(code);
        var user = await db.LoginLinks.AsNoTracking().Where(l => l.Hash == hash && l.UsedAt == null && l.ExpiresAt > now)
            .Join(db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.Role == UserRole.Member), l => l.UserId, u => u.Id, (_, u) => u).FirstOrDefaultAsync(ct);
        if (user is null) return null;
        // The affected-row count is the whole of the mutual exclusion: of two requests with one link, one finds it already spent.
        return await db.LoginLinks.Where(l => l.Hash == hash && l.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.UsedAt, now), ct) == 1 ? user : null;
    }
}
