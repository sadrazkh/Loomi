using Loomi.Data;
using Loomi.Security;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers;
/// <summary>Binding a Telegram chat to this account. Session only, for the same reason token management is: a link is a standing credential that can
/// spend the account's credits from a chat the holder of a leaked token would control, so a token must not be able to mint one.</summary>
[ApiController, Authorize, SessionOnly, Route("api/telegram")]
public class TelegramController(AppDbContext db, IOptions<TelegramOptions> options) : ControllerBase
{
    private Guid Me => Viewer.From(User).Id;
    private bool Configured => !string.IsNullOrWhiteSpace(options.Value.BotToken);
    [HttpGet("link")] public async Task<IActionResult> Status(CancellationToken ct)
    {
        var link = await db.TelegramLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == Me, ct);
        var now = DateTime.UtcNow;
        // A code already asked for is shown again rather than replaced, so reloading the page does not invalidate the one being typed.
        var code = link != null ? null : await db.LinkCodes.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == Me && c.ExpiresAt > now, ct);
        return Ok(new { linked = link != null, linkedAt = link?.LinkedAt, code = code?.Code, expiresAt = code?.ExpiresAt, botUsername = options.Value.BotUsername, configured = Configured });
    }
    [HttpPost("link")] public async Task<IActionResult> Create(CancellationToken ct)
    {
        if (!Configured) return Conflict(new { error = "TelegramNotConfigured" });
        if (await db.TelegramLinks.AnyAsync(l => l.UserId == Me, ct)) return Conflict(new { error = "AlreadyLinked" });
        var code = await Linking.IssueAsync(db, Me, DateTime.UtcNow, ct);
        return Created("/api/telegram/link", new { code.Code, code.ExpiresAt, botUsername = options.Value.BotUsername });
    }
    [HttpDelete("link")] public async Task<IActionResult> Delete(CancellationToken ct)
    {
        var removed = await db.TelegramLinks.Where(l => l.UserId == Me).ExecuteDeleteAsync(ct);
        await db.LinkCodes.Where(c => c.UserId == Me).ExecuteDeleteAsync(ct);
        return removed == 1 ? NoContent() : throw new KeyNotFoundException();
    }
}
