using System.Security.Claims;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Security;
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
    [HttpGet] public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = User.Identity?.IsAuthenticated == true ? await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Viewer.From(User).Id && !x.IsDisabled, ct) : null;
        return Ok(new { authenticated = user != null, username = user?.Username, role = user?.Role, dailyQuota = user?.DailyQuota, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken, devAccessKey = DevelopmentAccess.Hint(environment, config["Security:AccessKey"]) });
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
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }
}
