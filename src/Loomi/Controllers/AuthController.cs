using System.ComponentModel.DataAnnotations;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
public record CreateAccount([Required, StringLength(64, MinimumLength = 1)] string Label);
public record UpdateAccount([StringLength(64, MinimumLength = 1)] string? Label, [Range(0, 500)] int? DailyCap, bool? IsEnabled);
public record AccountDto(Guid Id, string Label, int DailyCap, bool IsEnabled, DateTime? LastUsedAt, DateTime CreatedAt, string State, bool Busy, string? DesktopUrl)
{
    public static AccountDto From(BrowserAccount a, string state, bool busy, string? desktop) => new(a.Id, a.Label, a.DailyCap, a.IsEnabled, a.LastUsedAt, a.CreatedAt, state, busy, desktop);
}
[ApiController, Authorize, Route("api/auth")]
public class AuthController(AppDbContext db, BrowserPool pool, BrowserAutomationService browser) : ControllerBase
{
    [HttpGet("status")] public async Task<IActionResult> Status() => Ok(await browser.StatusAsync());
    [HttpPost("connect")] public async Task<IActionResult> Connect(CancellationToken ct) => Ok(await browser.ConnectAsync(ct));
    [HttpPost("reset")] public async Task<IActionResult> Reset(CancellationToken ct) { await browser.ResetAsync(ct); return NoContent(); }
    /// <summary>States come from the sessions already running: probing every account on every poll would drive one browser per row.</summary>
    [HttpGet("accounts")] public async Task<IActionResult> Accounts(CancellationToken ct)
    {
        var desktop = await pool.DesktopUrlAsync();
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return Ok(accounts.Select(a => AccountDto.From(a, pool.StateOf(a.Id), pool.IsBusy(a.Id), desktop)));
    }
    [HttpPost("accounts")] public async Task<IActionResult> Create(CreateAccount request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Label)) return BadRequest(new { error = "InvalidLabel" });
        // The folder is generated, never taken from the label: it is an identifier, not a name the owner can shape.
        var account = new BrowserAccount { Label = request.Label.Trim(), ProfileDirectory = "account-" + Guid.NewGuid().ToString("n")[..12] };
        db.Accounts.Add(account); await db.SaveChangesAsync(ct);
        return Created($"/api/auth/accounts/{account.Id}", AccountDto.From(account, "Disconnected", false, await pool.DesktopUrlAsync()));
    }
    [HttpGet("accounts/{id:guid}/status")] public async Task<IActionResult> AccountStatus(Guid id, CancellationToken ct) => Ok(await pool.RunAsync(await AccountAsync(id, ct), session => session.StatusAsync()));
    [HttpPost("accounts/{id:guid}/connect")] public async Task<IActionResult> ConnectAccount(Guid id, CancellationToken ct) => Ok(await pool.RunAsync(await AccountAsync(id, ct), session => session.ConnectAsync(ct)));
    [HttpPost("accounts/{id:guid}/reset")] public async Task<IActionResult> ResetAccount(Guid id, CancellationToken ct) { await pool.RunAsync(await AccountAsync(id, ct), session => session.ResetAsync(ct)); return NoContent(); }
    [HttpPatch("accounts/{id:guid}")] public async Task<IActionResult> Update(Guid id, UpdateAccount request, CancellationToken ct)
    {
        var account = await AccountAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(request.Label)) account.Label = request.Label.Trim();
        if (request.DailyCap is { } cap) account.DailyCap = cap;
        if (request.IsEnabled is { } enabled) account.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return Ok(AccountDto.From(account, pool.StateOf(id), pool.IsBusy(id), await pool.DesktopUrlAsync()));
    }
    [HttpDelete("accounts/{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var account = await AccountAsync(id, ct);
        if (pool.IsBusy(id)) throw new InvalidOperationException("BrowserBusy");
        // The profile holds the login, so removing the row has to take the cookies with it.
        await pool.RunAsync(account, session => session.ResetAsync(ct));
        await pool.DiscardAsync(id);
        db.Accounts.Remove(account); await db.SaveChangesAsync(ct);
        return NoContent();
    }
    private async Task<BrowserAccount> AccountAsync(Guid id, CancellationToken ct) => await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException();
}
