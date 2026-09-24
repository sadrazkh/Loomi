using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.Extensions.Options;
namespace Loomi.Telegram;
/// <summary>Which bot this workspace runs, as it stands now. The owner sets it in the app, where the token is sealed before it is stored; a deployment
/// may instead set it in configuration, which counts only while the app holds none. A change takes effect at once: the client builds for the new
/// token and the workers wake, with no restart.</summary>
public class BotSettings(IServiceScopeFactory scopes, AccountSecrets secrets, IOptions<TelegramOptions> options)
{
    public const string TokenKey = "Telegram.BotToken", UsernameKey = "Telegram.BotUsername";
    private volatile Bot? stored;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Bot(string Token, string? Username);
    public string? Token => stored?.Token ?? Blank(options.Value.BotToken);
    public string? Username => stored is { } bot ? bot.Username : Blank(options.Value.BotUsername);
    public bool Configured => Token is not null;
    /// <summary>"app" when the owner set it here, "config" when it comes from the deployment, null when there is none.</summary>
    public string? Source => stored is not null ? "app" : Configured ? "config" : null;
    /// <summary>Completes the next time the bot changes, so a worker waiting for a token, or polling with the old one, starts over.</summary>
    public Task Changed => Volatile.Read(ref changed).Task;
    /// <summary>A token sealed under a key ring that has since gone is treated as none: the owner enters it again, rather than every request failing.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var token = await db.Settings.FindAsync([TokenKey], ct);
        var username = await db.Settings.FindAsync([UsernameKey], ct);
        stored = token is not null && secrets.Reveal(token.Value) is { } revealed ? new(revealed, username?.Value) : null;
        Signal();
    }
    public async Task SetAsync(string token, string username, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PutAsync(db, TokenKey, secrets.Protect(token), ct);
        await PutAsync(db, UsernameKey, username, ct);
        await db.SaveChangesAsync(ct);
        stored = new(token, username);
        Signal();
    }
    public async Task ClearAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Settings.RemoveRange(db.Settings.Where(s => s.Key == TokenKey || s.Key == UsernameKey));
        await db.SaveChangesAsync(ct);
        stored = null;
        Signal();
    }
    private static async Task PutAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        if (await db.Settings.FindAsync([key], ct) is { } existing) existing.Value = value;
        else db.Settings.Add(new AppSetting { Key = key, Value = value });
    }
    private void Signal() => Interlocked.Exchange(ref changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
