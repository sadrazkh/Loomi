using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
public record CreateToken(string? Name);
public record TokenDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);
/// <summary>The caller's own tokens. The secret appears exactly once, in the creation response; from then on only its prefix identifies it.</summary>
[ApiController, Authorize, SessionOnly, Route("api/tokens")]
public class TokensController(AppDbContext db) : ControllerBase
{
    public const int MaxActive = 20;
    private const int SqliteUniqueViolation = 2067;
    private Guid Me => Viewer.From(User).Id;
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await ListAsync(db, Me, ct));
    [HttpPost] public async Task<IActionResult> Create(CreateToken request, CancellationToken ct)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 64) return BadRequest(new { error = "InvalidTokenName" });
        if (await db.ApiTokens.CountAsync(t => t.UserId == Me && t.RevokedAt == null, ct) >= MaxActive) return Conflict(new { error = "TooManyTokens" });
        // The prefix carries 47 bits, so a collision is a once-in-a-lifetime event; the unique index still refuses it and a fresh one is minted.
        for (var attempt = 0; ; attempt++)
        {
            var (token, prefix, hash) = ApiTokens.Mint();
            var row = new ApiToken { UserId = Me, Name = name, Prefix = prefix, Hash = hash };
            db.ApiTokens.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (attempt < 2 && e.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation }) { db.Entry(row).State = EntityState.Detached; continue; }
            return Created($"/api/tokens/{row.Id}", new { row.Id, row.Name, row.Prefix, row.CreatedAt, token });
        }
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) => await RevokeAsync(db, Me, id, ct) ? NoContent() : throw new KeyNotFoundException();
    internal static Task<List<TokenDto>> ListAsync(AppDbContext db, Guid user, CancellationToken ct) =>
        db.ApiTokens.AsNoTracking().Where(t => t.UserId == user && t.RevokedAt == null).OrderBy(t => t.CreatedAt)
            .Select(t => new TokenDto(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt)).ToListAsync(ct);
    /// <summary>False for another user's token and for one already revoked: both are a miss to the caller, so ids cannot be probed.</summary>
    internal static async Task<bool> RevokeAsync(AppDbContext db, Guid user, Guid id, CancellationToken ct) =>
        await db.ApiTokens.Where(t => t.Id == id && t.UserId == user && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct) == 1;
}
