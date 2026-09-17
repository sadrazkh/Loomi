using System.ComponentModel.DataAnnotations;
using Loomi.Data;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
public record CreateAccount([Required, StringLength(64, MinimumLength = 1)] string Label, Provider Provider = Provider.ChatGPT, string? ApiKey = null);
public record UpdateAccount([StringLength(64, MinimumLength = 1)] string? Label, [Range(0, 500)] int? DailyCap, bool? IsEnabled);
/// <summary>KeyHint is all an API account ever shows of its key: enough to tell two apart, never enough to be one.</summary>
public record AccountDto(Guid Id, Provider Provider, AccountKind Kind, string Label, int DailyCap, bool IsEnabled, DateTime? LastUsedAt, DateTime CreatedAt, string State, bool Busy, bool SpentToday, string? DesktopUrl, string? KeyHint)
{
    public static AccountDto From(ProviderAccount a, string state, bool busy, bool spent, string? desktop, string? keyHint = null) => new(a.Id, a.Provider, a.Kind, a.Label, a.DailyCap, a.IsEnabled, a.LastUsedAt, a.CreatedAt, state, busy, spent, desktop, keyHint);
}
[ApiController, Authorize, Route("api/auth")]
public class AuthController(AppDbContext db, ProviderRegistry providers, AccountHealth health, AccountSecrets secrets) : ControllerBase
{
    /// <summary>The whole pool at a glance: enough for a member to see the workspace can generate, without exposing individual accounts.</summary>
    [HttpGet("status")] public async Task<IActionResult> Status(CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsEnabled).ToListAsync(ct);
        var byProvider = accounts.GroupBy(a => a.Provider).Select(g => new { provider = g.Key.ToString(), accounts = g.Count(), connected = g.Count(a => providers.For(a.Provider).StateOf(a) == "Connected") });
        return Ok(new { ready = accounts.Any(a => providers.For(a.Provider).StateOf(a) == "Connected"), stalled = await StalledAsync(accounts, ct), desktopUrl = await DesktopAsync(), providers = byProvider });
    }
    /// <summary>Why nothing is moving, when something is waiting. Silence here is what makes a parked account look like a broken queue.</summary>
    private async Task<string?> StalledAsync(List<ProviderAccount> accounts, CancellationToken ct)
    {
        if (!await db.Generations.AnyAsync(g => g.Status == RunStatus.Queued, ct)) return null;
        if (accounts.Count == 0) return "NoAccount";
        foreach (var a in accounts)
            if (!health.IsSpent(a.Id) && providers.For(a.Provider).StateOf(a) is not ("LoginRequired" or "VerificationRequired") && await db.UsedOnAsync(a.Id, ct) < a.DailyCap) return null;
        return "AllAccountsSpent";
    }
    private async Task<string?> DesktopAsync()
    {
        foreach (var provider in providers.All)
            if (await provider.DesktopUrlAsync() is { } url) return url;
        return null;
    }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpGet("accounts")] public async Task<IActionResult> Accounts(CancellationToken ct)
    {
        var desktop = await DesktopAsync();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return Ok(accounts.Select(a => { var p = providers.For(a.Provider); return AccountDto.From(a, p.StateOf(a), p.IsBusy(a), health.IsSpent(a.Id), desktop, AccountSecrets.Hint(secrets.Reveal(a.Secret))); }));
    }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpPost("accounts")] public async Task<IActionResult> Create(CreateAccount request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Label)) return BadRequest(new { error = "InvalidLabel" });
        if (!providers.Has(request.Provider)) return BadRequest(new { error = "NoProvider" });
        var key = request.ApiKey?.Trim();
        // What an account is made of follows from its provider: a browser account has a profile and no key, an API account the other way round.
        var api = request.Provider == Provider.Gemini;
        if (api && string.IsNullOrEmpty(key)) return BadRequest(new { error = "ApiKeyRequired" });
        if (!api && !string.IsNullOrEmpty(key)) return BadRequest(new { error = "ApiKeyNotAllowed" });
        if (key is { Length: > 400 }) return BadRequest(new { error = "InvalidApiKey" });
        // The folder is generated, never taken from the label: it is an identifier, not a name the owner can shape.
        var account = new ProviderAccount { Provider = request.Provider, Kind = api ? AccountKind.ApiKey : AccountKind.Browser, Label = request.Label.Trim(),
                                            ProfileDirectory = api ? null : "account-" + Guid.NewGuid().ToString("n")[..12], Secret = api ? secrets.Protect(key!) : null };
        db.Accounts.Add(account); await db.SaveChangesAsync(ct);
        var provider = providers.For(account.Provider);
        return Created($"/api/auth/accounts/{account.Id}", AccountDto.From(account, provider.StateOf(account), false, false, await provider.DesktopUrlAsync(), AccountSecrets.Hint(key)));
    }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpGet("accounts/{id:guid}/status")] public async Task<IActionResult> AccountStatus(Guid id, CancellationToken ct)
    { var a = await AccountAsync(id, ct); return Ok(await providers.For(a.Provider).StatusAsync(a)); }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpPost("accounts/{id:guid}/connect")] public async Task<IActionResult> ConnectAccount(Guid id, CancellationToken ct)
    { var a = await AccountAsync(id, ct); return Ok(await providers.For(a.Provider).ConnectAsync(a, ct)); }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpPost("accounts/{id:guid}/reset")] public async Task<IActionResult> ResetAccount(Guid id, CancellationToken ct)
    { var a = await AccountAsync(id, ct); await providers.For(a.Provider).ResetAsync(a, ct); return NoContent(); }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpPatch("accounts/{id:guid}")] public async Task<IActionResult> Update(Guid id, UpdateAccount request, CancellationToken ct)
    {
        var account = await AccountAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(request.Label)) account.Label = request.Label.Trim();
        if (request.DailyCap is { } cap) account.DailyCap = cap;
        if (request.IsEnabled is { } enabled) account.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        var provider = providers.For(account.Provider);
        return Ok(AccountDto.From(account, provider.StateOf(account), provider.IsBusy(account), health.IsSpent(id), await provider.DesktopUrlAsync(), AccountSecrets.Hint(secrets.Reveal(account.Secret))));
    }
    [Authorize(Roles = nameof(UserRole.Owner))] [HttpDelete("accounts/{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var account = await AccountAsync(id, ct);
        var provider = providers.For(account.Provider);
        if (provider.IsBusy(account)) throw new InvalidOperationException("BrowserBusy");
        // The profile holds the login, so removing the row has to take the cookies with it.
        await provider.ResetAsync(account, ct);
        await provider.DiscardAsync(id);
        db.Accounts.Remove(account); await db.SaveChangesAsync(ct);
        return NoContent();
    }
    private async Task<ProviderAccount> AccountAsync(Guid id, CancellationToken ct) => await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException();
}
