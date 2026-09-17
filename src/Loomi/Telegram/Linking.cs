using System.Security.Cryptography;
using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Telegram;
/// <summary>What the bot learned from a code: the message key to answer with, and the account now behind the chat when there is one.</summary>
public readonly record struct LinkOutcome(string Key, AppUser? User);
/// <summary>The short code that proves a chat belongs to a Loomi account. It is read off one screen and typed into another, so it is short, short-lived,
/// spent on first use, and made only of characters nobody confuses.</summary>
public static class Linking
{
    public const int Length = 8;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    /// <summary>One live code per user: asking again replaces the last one, so a code read out and abandoned cannot be used later.</summary>
    public static async Task<LinkCode> IssueAsync(AppDbContext db, Guid user, DateTime now, CancellationToken ct)
    {
        await db.LinkCodes.Where(c => c.UserId == user || c.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var code = new LinkCode { Code = RandomNumberGenerator.GetString(Alphabet, Length), UserId = user, ExpiresAt = now + Lifetime };
            db.LinkCodes.Add(code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) when (attempt < 2) { db.Entry(code).State = EntityState.Detached; continue; }
            return code;
        }
    }
    /// <summary>Spends a code against a chat. Every refusal leaves the database as it was, and the code survives only if it was never the right one.</summary>
    public static async Task<LinkOutcome> RedeemAsync(AppDbContext db, string code, long chat, DateTime now, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var row = normalized.Length == Length ? await db.LinkCodes.FirstOrDefaultAsync(c => c.Code == normalized, ct) : null;
        if (row == null) return new("linkUnknown", null);
        if (row.ExpiresAt <= now) { db.LinkCodes.Remove(row); await db.SaveChangesAsync(ct); return new("linkExpired", null); }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.UserId && !u.IsDisabled, ct);
        if (user == null) { db.LinkCodes.Remove(row); await db.SaveChangesAsync(ct); return new("linkUnknown", null); }
        var onChat = await db.TelegramLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ChatId == chat, ct);
        // Quoting a fresh code for the account the chat already carries is a no-op, not a refusal.
        if (onChat != null) { if (onChat.UserId != user.Id) return new("linkChatTaken", null); db.LinkCodes.Remove(row); await db.SaveChangesAsync(ct); return new("linked", user); }
        if (await db.TelegramLinks.AnyAsync(l => l.UserId == user.Id, ct)) return new("linkUserTaken", null);
        db.TelegramLinks.Add(new TelegramLink { UserId = user.Id, ChatId = chat });
        db.LinkCodes.Remove(row);
        await db.SaveChangesAsync(ct);
        return new("linked", user);
    }
}
