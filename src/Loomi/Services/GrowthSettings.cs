using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>The owner's switches for what reaches people outside the workspace. Everything that lets a stranger in or sends a message nobody asked for
/// starts off, so a deploy never does either by itself; a missing row is the default.</summary>
public class GrowthSettings(AppDbContext db)
{
    public const string SignupOpen = "Signup.Open", SignupGiftImages = "Signup.GiftImages", SignupDailyLimit = "Signup.DailyLimit";
    public const string NotifyNewTemplates = "Notify.NewTemplates", NotifyWinBack = "Notify.WinBack", NotifyLowCredit = "Notify.LowCredit", NotifyDigestAt = "Notify.DigestAt";
    /// <summary>The site's price rows as JSON, written by the owner, and where people go to buy: nothing is shown until the owner has written it.</summary>
    public const string SitePrices = "Site.Prices", SiteContact = "Site.Contact";
    public const int DefaultGiftImages = 5, DefaultSignupDailyLimit = 20;
    public Task<string?> GetAsync(string key, CancellationToken ct) => db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    public async Task<bool> OnAsync(string key, CancellationToken ct) => await GetAsync(key, ct) == "on";
    public async Task<int> IntAsync(string key, int fallback, CancellationToken ct) => int.TryParse(await GetAsync(key, ct), out var value) ? value : fallback;
    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null) db.Settings.Add(new AppSetting { Key = key, Value = value }); else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
    public Task SetOnAsync(string key, bool on, CancellationToken ct) => SetAsync(key, on ? "on" : "off", ct);
}
