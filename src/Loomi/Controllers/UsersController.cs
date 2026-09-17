using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
// Nullable on purpose: a non-nullable string here would make [ApiController] answer bad input with its own ProblemDetails instead of a stable error code.
public record CreateUser(string? Username, string? Password, int? DailyQuota, int? Credits);
public record GrantCredits(int? Amount, string? Note);
public record UpdateUser(int? DailyQuota, UserRole? Role, bool? IsDisabled);
public record ResetPassword(string? Password);
public record UserDto(Guid Id, string Username, UserRole Role, int DailyQuota, bool IsDisabled, DateTime CreatedAt, int UsedToday, int Credits)
{
    public static UserDto From(AppUser u, int used, int credits = 0) => new(u.Id, u.Username, u.Role, u.DailyQuota, u.IsDisabled, u.CreatedAt, used, credits);
}
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/users")]
public class UsersController(AppDbContext db, PasswordService passwords, IImageStorage storage) : ControllerBase
{
    private const int MinPassword = 8, MaxQuota = 10000;
    /// <summary>SQLITE_CONSTRAINT_UNIQUE. The only unique index this endpoint can violate is the one on NormalizedUsername.</summary>
    private const int SqliteUniqueViolation = 2067;
    private Viewer Me => Viewer.From(User);
    private static bool ValidName(string name) => name.Length is >= 3 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private Task<Dictionary<Guid, int>> UsageAsync(CancellationToken ct) => db.UsageAsync(ct);
    private async Task<AppUser> UserAsync(Guid id, CancellationToken ct) => await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();
    /// <summary>True while somebody other than this user can still reach the owner-only routes.</summary>
    private Task<bool> AnotherOwnerAsync(Guid id, CancellationToken ct) => db.Users.AnyAsync(u => u.Id != id && u.Role == UserRole.Owner && !u.IsDisabled, ct);

    [HttpGet] public async Task<IActionResult> List(CancellationToken ct)
    {
        var usage = await UsageAsync(ct);
        var balances = await db.CreditEntries.AsNoTracking().GroupBy(e => e.UserId).Select(x => new { x.Key, Total = x.Sum(e => e.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(ct);
        return Ok(users.Select(u => UserDto.From(u, usage.GetValueOrDefault(u.Id), balances.GetValueOrDefault(u.Id))));
    }
    [HttpPost] public async Task<IActionResult> Create(CreateUser request, CancellationToken ct)
    {
        var username = (request.Username ?? "").Trim();
        if (!ValidName(username)) return BadRequest(new { error = "InvalidUsername" });
        if ((request.Password ?? "").Length < MinPassword) return BadRequest(new { error = "WeakPassword" });
        if (request.DailyQuota is < 0 or > MaxQuota) return BadRequest(new { error = "InvalidQuota" });
        if (request.Credits is < 0 or > 1000000) return BadRequest(new { error = "InvalidCredits" });
        var normalized = username.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct)) return Conflict(new { error = "DuplicateUsername" });
        var user = new AppUser { Username = username, NormalizedUsername = normalized, DailyQuota = request.DailyQuota ?? 10 };
        user.PasswordHash = passwords.Hash(user, request.Password!);
        db.Users.Add(user);
        if (request.Credits is > 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = request.Credits.Value, Kind = CreditKind.Grant, ByUserId = Me.Id, Note = "opening balance" });
        // The check above cannot see a row a concurrent request has not committed yet; the unique index is what actually decides.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation }) { return Conflict(new { error = "DuplicateUsername" }); }
        return Created($"/api/users/{user.Id}", UserDto.From(user, 0, request.Credits ?? 0));
    }
    [HttpPatch("{id:guid}")] public async Task<IActionResult> Update(Guid id, UpdateUser request, CancellationToken ct)
    {
        if (request.DailyQuota is < 0 or > MaxQuota) return BadRequest(new { error = "InvalidQuota" });
        var user = await UserAsync(id, ct);
        var role = request.Role ?? user.Role;
        var disabled = request.IsDisabled ?? user.IsDisabled;
        if (disabled && id == Me.Id) return Conflict(new { error = "CannotDisableSelf" });
        // Demoting or disabling the last owner leaves nobody who can undo it, so the workspace would be locked for everyone.
        if ((role != UserRole.Owner || disabled) && !await AnotherOwnerAsync(id, ct)) return Conflict(new { error = "LastOwner" });
        if (request.DailyQuota is { } quota) user.DailyQuota = quota;
        user.Role = role; user.IsDisabled = disabled;
        await db.SaveChangesAsync(ct);
        return Ok(UserDto.From(user, (await UsageAsync(ct)).GetValueOrDefault(id), await db.BalanceAsync(id, ct)));
    }
    [HttpPost("{id:guid}/password")] public async Task<IActionResult> Password(Guid id, ResetPassword request, CancellationToken ct)
    {
        if ((request.Password ?? "").Length < MinPassword) return BadRequest(new { error = "WeakPassword" });
        var user = await UserAsync(id, ct);
        user.PasswordHash = passwords.Hash(user, request.Password!);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
    [HttpGet("{id:guid}/credits")] public async Task<IActionResult> Credits(Guid id, CancellationToken ct)
    {
        await UserAsync(id, ct);
        return Ok(new { balance = await db.BalanceAsync(id, ct), entries = await CreditsController.LedgerAsync(db, id, ct) });
    }
    /// <summary>A grant adds, an adjustment corrects; a negative amount is the second, and the ledger keeps who did it either way.</summary>
    [HttpPost("{id:guid}/credits")] public async Task<IActionResult> Grant(Guid id, GrantCredits request, CancellationToken ct)
    {
        if (request.Amount is null or 0 or < -1000000 or > 1000000) return BadRequest(new { error = "InvalidCredits" });
        await UserAsync(id, ct);
        db.CreditEntries.Add(new CreditEntry { UserId = id, Amount = request.Amount.Value, Kind = request.Amount > 0 ? CreditKind.Grant : CreditKind.Adjust, ByUserId = Me.Id, Note = request.Note?.Trim() });
        await db.SaveChangesAsync(ct);
        return Ok(new { balance = await db.BalanceAsync(id, ct) });
    }
    [SessionOnly, HttpGet("{id:guid}/tokens")] public async Task<IActionResult> Tokens(Guid id, CancellationToken ct)
    {
        await UserAsync(id, ct);
        return Ok(await TokensController.ListAsync(db, id, ct));
    }
    [SessionOnly, HttpDelete("{id:guid}/tokens/{tokenId:guid}")] public async Task<IActionResult> RevokeToken(Guid id, Guid tokenId, CancellationToken ct) =>
        await TokensController.RevokeAsync(db, id, tokenId, ct) ? NoContent() : throw new KeyNotFoundException();
    /// <summary>A project is the unit of ownership: what sits inside one goes with it, and nothing outside one is touched.</summary>
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == Me.Id) return Conflict(new { error = "CannotDeleteSelf" });
        var user = await UserAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Generations.AnyAsync(g => g.UserId == id && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled, ct)) return Conflict(new { error = "UserBusy" });
        var projects = await db.Projects.Where(p => p.UserId == id).Select(p => p.Id).ToListAsync(ct);
        // A generation of theirs inside somebody else's project is that project's history. Re-home it instead of deleting it, or the other user loses a step.
        foreach (var stray in await db.Generations.Include(g => g.Project).Where(g => g.UserId == id && !projects.Contains(g.ProjectId)).ToListAsync(ct)) stray.UserId = stray.Project.UserId;
        await db.SaveChangesAsync(ct);
        var doomed = await db.Generations.Where(g => projects.Contains(g.ProjectId)).Select(g => g.Id).ToListAsync(ct);
        // ParentGenerationId is Restrict, so every reference into the doomed rows has to go first, including one from a project that survives.
        await db.Generations.Where(g => g.ParentGenerationId != null && doomed.Contains(g.ParentGenerationId!.Value)).ExecuteUpdateAsync(s => s.SetProperty(g => g.ParentGenerationId, (Guid?)null), ct);
        await db.Projects.Where(p => p.UserId == id).ExecuteDeleteAsync(ct);
        // Tokens have no foreign key to the user; without this they would sit unreachable for ever.
        await db.ApiTokens.Where(t => t.UserId == id).ExecuteDeleteAsync(ct);
        // A Telegram link outliving its user would leave a chat that the next user of that id inherits; the unique index would also refuse a fresh link.
        await db.TelegramLinks.Where(l => l.UserId == id).ExecuteDeleteAsync(ct);
        await db.LinkCodes.Where(c => c.UserId == id).ExecuteDeleteAsync(ct);
        db.Users.Remove(user); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        foreach (var project in projects) storage.DeleteProject(project);
        return NoContent();
    }
}
