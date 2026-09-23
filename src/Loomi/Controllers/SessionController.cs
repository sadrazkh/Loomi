using System.Security.Claims;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
[ApiController, Route("api/session")]
public class SessionController(AppDbContext db, PasswordService passwords, IConfiguration config, IAntiforgery antiforgery, IWebHostEnvironment environment) : ControllerBase
{
    // Same floor as the owner's own reset in UsersController: a self-service change should not be allowed to end up weaker than an owner-assigned one.
    private const int MinPassword = 8;
    [HttpGet] public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = User.Identity?.IsAuthenticated == true ? await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Viewer.From(User).Id && !x.IsDisabled, ct) : null;
        var used = user == null ? 0 : await db.UsedByAsync(user.Id, ct);
        // Null for an owner: they are not charged, so a number here would suggest a limit that does not exist.
        var credits = user is { Role: UserRole.Member } ? await db.BalanceAsync(user.Id, ct) : (int?)null;
        return Ok(new { authenticated = user != null, username = user?.Username, role = user?.Role, dailyQuota = user?.DailyQuota, dailyUsed = user == null ? null : (int?)used, dailyRemaining = user is { DailyQuota: > 0 } ? (int?)Math.Max(0, user.DailyQuota - used) : null, credits, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken, devAccessKey = user == null ? DevelopmentAccess.Hint(environment, config["Security:AccessKey"]) : null });
    }
    [HttpPost("login"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var normalized = request.Username.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
        if (!passwords.Verify(user, request.Password)) return Unauthorized(new { error = "InvalidCredentials" });
        if (user!.IsDisabled) return Unauthorized(new { error = "AccountDisabled" });
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role.ToString())], CookieAuthenticationDefaults.AuthenticationScheme)));
        return Ok(new { authenticated = true, user.Username, user.Role, user.DailyQuota });
    }
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return NoContent(); }
    /// <summary>A tester's own password change. Session-only: a leaked bearer token must not be able to lock the real owner of the account out of it.</summary>
    [Authorize, SessionOnly, HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == Viewer.From(User).Id, ct) ?? throw new KeyNotFoundException();
        if (!passwords.Verify(user, request.CurrentPassword ?? "")) return Unauthorized(new { error = "InvalidCurrentPassword" });
        if ((request.NewPassword ?? "").Length < MinPassword) return BadRequest(new { error = "WeakPassword" });
        user.PasswordHash = passwords.Hash(user, request.NewPassword!);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
