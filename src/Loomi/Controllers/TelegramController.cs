using Loomi.Data;
using Loomi.Security;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Loomi.Models;
namespace Loomi.Controllers;
/// <summary>Binding a Telegram chat to this account. Session only, for the same reason token management is: a link is a standing credential that can
/// spend the account's credits from a chat the holder of a leaked token would control, so a token must not be able to mint one.</summary>
[ApiController, Authorize, SessionOnly, Route("api/telegram")]
public class TelegramController(AppDbContext db, BotSettings bot, ITelegramClient client) : ControllerBase
{
    public record BotRequest(string? Token);
    private Guid Me => Viewer.From(User).Id;
    private bool Configured => bot.Configured;
    [HttpGet("link")] public async Task<IActionResult> Status(CancellationToken ct)
    {
        var link = await db.TelegramLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == Me, ct);
        var now = DateTime.UtcNow;
        // A code already asked for is shown again rather than replaced, so reloading the page does not invalidate the one being typed.
        var code = link != null ? null : await db.LinkCodes.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == Me && c.ExpiresAt > now, ct);
        // Where the bot comes from and a hint of its token are the owner's business; everyone else only needs to know which chat to open.
        var owner = User.IsInRole(nameof(UserRole.Owner));
        return Ok(new { linked = link != null, linkedAt = link?.LinkedAt, code = code?.Code, expiresAt = code?.ExpiresAt, botUsername = bot.Username, configured = Configured,
                        botSource = owner ? bot.Source : null, tokenHint = owner ? AccountSecrets.Hint(bot.Token) : null });
    }
    [HttpPost("link")] public async Task<IActionResult> Create(CancellationToken ct)
    {
        if (!Configured) return Conflict(new { error = "TelegramNotConfigured" });
        if (await db.TelegramLinks.AnyAsync(l => l.UserId == Me, ct)) return Conflict(new { error = "AlreadyLinked" });
        var code = await Linking.IssueAsync(db, Me, DateTime.UtcNow, ct);
        return Created("/api/telegram/link", new { code.Code, code.ExpiresAt, botUsername = bot.Username });
    }
    /// <summary>The owner gives the workspace its bot from here rather than from the server's configuration. Telegram is asked first, so a mistyped token
    /// is refused with a reason instead of leaving a bot that silently never answers; the token is sealed before it is stored and never shown again.</summary>
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPut("bot")] public async Task<IActionResult> SetBot(BotRequest request, CancellationToken ct)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 200 || await client.WhoAmIAsync(token, ct) is not { } username) return BadRequest(new { error = "InvalidBotToken" });
        await bot.SetAsync(token, username, ct);
        return Ok(new { botUsername = username });
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpDelete("bot")] public async Task<IActionResult> ClearBot(CancellationToken ct)
    {
        await bot.ClearAsync(ct);
        return NoContent();
    }
    [HttpDelete("link")] public async Task<IActionResult> Delete(CancellationToken ct)
    {
        var removed = await db.TelegramLinks.Where(l => l.UserId == Me).ExecuteDeleteAsync(ct);
        await db.LinkCodes.Where(c => c.UserId == Me).ExecuteDeleteAsync(ct);
        return removed == 1 ? NoContent() : throw new KeyNotFoundException();
    }
}
