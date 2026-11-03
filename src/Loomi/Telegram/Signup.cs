using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Telegram;
/// <summary>What pressing the button came to: the message key to answer with, the account now behind the chat when there is one, whether it was made just
/// now, and how many gift images it came with.</summary>
public readonly record struct SignupOutcome(string Key, AppUser? User, bool Created = false, int GiftImages = 0);
/// <summary>An account made from a chat. A chat always maps to the same name, so whatever a chat does with its link it comes back to the account it had,
/// which is what puts a second gift out of reach.</summary>
public static class Signup
{
    public const string GiftName = "هدیه‌ی شروع";
    public const int GiftDays = 30;
    /// <summary>The same name for the same chat, with the chat's id not readable from it, since owners and reports show usernames around.</summary>
    public static string UsernameFor(long chat) =>
        "tg-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chat.ToString(CultureInfo.InvariantCulture))))[..12];
    /// <summary>Accounts the bot made since midnight, which is what the daily limit counts.</summary>
    public static Task<int> TodayAsync(AppDbContext db, CancellationToken ct)
    {
        var since = Quota.Midnight();
        return db.Users.AsNoTracking().CountAsync(u => u.FromBot && u.CreatedAt >= since, ct);
    }
    /// <summary>The gift a new account gets as things stand. With images free there is nothing to give, so none is promised.</summary>
    public static async Task<int> GiftImagesAsync(AppDbContext db, GrowthSettings growth, CancellationToken ct) =>
        await db.PriceAsync(Provider.ChatGPT, Operation.Generate, ct) > 0 ? Math.Max(0, await growth.IntAsync(GrowthSettings.SignupGiftImages, GrowthSettings.DefaultGiftImages, ct)) : 0;
    /// <summary>Safe to press any number of times. The transaction takes SQLite's write lock from its first statement, so a second press waits for the
    /// first to commit and then finds its account; the daily limit is held the same way. Every refusal leaves the database as it was.</summary>
    public static async Task<SignupOutcome> SignUpAsync(AppDbContext db, GrowthSettings growth, long chat, DateTime now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A link to a disabled account still counts as a link: it says the chat has been turned away, not that it is new here.
        if (await db.TelegramLinks.AsNoTracking().Where(l => l.ChatId == chat).Select(l => (Guid?)l.UserId).FirstOrDefaultAsync(ct) is { } holder)
            return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == holder && !u.IsDisabled, ct) is { } linked ? new("signupAlready", linked) : new("signupDisabled", null);
        var name = UsernameFor(chat);
        if (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == name, ct) is { } known)
        {
            // Only an account this chat made itself: one the owner happened to give the same name is not this chat's to take.
            if (known.IsDisabled || !known.FromBot) return new("signupDisabled", null);
            if (await db.TelegramLinks.AnyAsync(l => l.UserId == known.Id, ct)) return new("linkUserTaken", null);
            db.TelegramLinks.Add(new TelegramLink { UserId = known.Id, ChatId = chat, LinkedAt = now });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new("signupBack", known);
        }
        if (await TodayAsync(db, ct) >= await growth.IntAsync(GrowthSettings.SignupDailyLimit, GrowthSettings.DefaultSignupDailyLimit, ct)) return new("signupFull", null);
        var user = new AppUser { Username = name, NormalizedUsername = name, Role = UserRole.Member, FromBot = true, PasswordHash = "", CreatedAt = now };
        db.Users.Add(user);
        db.TelegramLinks.Add(new TelegramLink { UserId = user.Id, ChatId = chat, LinkedAt = now });
        var images = await GiftImagesAsync(db, growth, ct);
        if (images > 0)
            db.CreditPackages.Add(new CreditPackage { UserId = user.Id, Kind = PackageKind.Credits, IsGift = true, Name = GiftName, Amount = images * await db.PriceAsync(Provider.ChatGPT, Operation.Generate, ct),
                                                      StartsAt = now, EndsAt = now.AddDays(GiftDays), CreatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(images > 0 ? "signupDone" : "signupDonePlain", user, true, images);
    }
}
