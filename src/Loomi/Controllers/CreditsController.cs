using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
public record CreditEntryDto(Guid Id, int Amount, CreditKind Kind, Guid? GenerationId, string? Note, DateTime CreatedAt);
[ApiController, Authorize, Route("api/credits")]
public class CreditsController(AppDbContext db) : ControllerBase
{
    /// <summary>The caller's own balance and recent movements. Reading anyone else's goes through the owner-only user routes.</summary>
    [HttpGet] public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = Viewer.From(User).Id;
        return Ok(new { balance = await db.BalanceAsync(me, ct), entries = await LedgerAsync(db, me, ct) });
    }
    internal static async Task<List<CreditEntryDto>> LedgerAsync(AppDbContext db, Guid user, CancellationToken ct) =>
        await db.CreditEntries.AsNoTracking().Where(e => e.UserId == user).OrderByDescending(e => e.CreatedAt).Take(50)
            .Select(e => new CreditEntryDto(e.Id, e.Amount, e.Kind, e.GenerationId, e.Note, e.CreatedAt)).ToListAsync(ct);
}
