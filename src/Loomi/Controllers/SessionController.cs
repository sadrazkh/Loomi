using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Loomi.DTOs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Loomi.Controllers;
[ApiController, Route("api/session")]
public class SessionController(IConfiguration config, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet] public IActionResult Get() => Ok(new { authenticated = User.Identity?.IsAuthenticated == true, csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    [HttpPost("login"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(request.AccessKey));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(config["Security:AccessKey"]!));
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return Unauthorized(new { error = "InvalidAccessKey" });
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")], CookieAuthenticationDefaults.AuthenticationScheme)));
        return Ok(new { authenticated = true });
    }
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }
}
