using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Loomi.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Security;
/// <summary>Personal access tokens shaped lm_&lt;prefix8&gt;_&lt;secret32&gt;. The prefix finds the row and the SHA-256 of the whole token proves it; with 190 bits of random secret a slow hash would buy nothing.</summary>
public static class ApiTokens
{
    public const string Scheme = "Token", Selector = "Auto", PrefixClaim = "loomi:token";
    public const int Length = 44;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    public static (string Token, string Prefix, string Hash) Mint()
    {
        var prefix = RandomNumberGenerator.GetString(Alphabet, 8);
        var token = $"lm_{prefix}_{RandomNumberGenerator.GetString(Alphabet, 32)}";
        return (token, prefix, HashOf(token));
    }
    public static string HashOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    /// <summary>The prefix of a well-formed token, or null. Shape is settled before the database is asked, so junk never becomes a query.</summary>
    public static string? PrefixOf(string token) =>
        token.Length == Length && token.StartsWith("lm_") && token[11] == '_' && token[3..11].All(char.IsAsciiLetterOrDigit) && token[12..].All(char.IsAsciiLetterOrDigit) ? token[3..11] : null;
    public static bool Matches(string token, string storedHash) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashOf(token)), Encoding.ASCII.GetBytes(storedHash));
    public static bool IsToken(ClaimsPrincipal user) => user.Identity?.AuthenticationType == Scheme;
    public static bool IsBearer(HttpRequest request) => request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
/// <summary>Turns a bearer header into the same principal a cookie sign-in produces. Role and disabled flag are read fresh on every call, so a token never outlives a change to its user.</summary>
public class TokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>How far LastUsedAt may lag, so a busy client does not turn every read into a write.</summary>
    private static readonly TimeSpan UseGranularity = TimeSpan.FromMinutes(1);
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiTokens.IsBearer(Request)) return AuthenticateResult.NoResult();
        // A token is for the API alone: it opens neither the desktop proxy nor the hub, whatever its user could do there with a cookie.
        if (!Request.Path.StartsWithSegments("/api")) return AuthenticateResult.Fail("TokenScope");
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        if (ApiTokens.PrefixOf(token) is not { } prefix) return AuthenticateResult.Fail("InvalidToken");
        var db = Context.RequestServices.GetRequiredService<AppDbContext>();
        var found = await db.ApiTokens.AsNoTracking().Where(t => t.Prefix == prefix && t.RevokedAt == null)
            .Join(db.Users.Where(u => !u.IsDisabled), t => t.UserId, u => u.Id, (t, u) => new { t.Id, t.Hash, t.LastUsedAt, UserId = u.Id, u.Username, u.Role })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (found == null || !ApiTokens.Matches(token, found.Hash)) return AuthenticateResult.Fail("InvalidToken");
        var now = DateTime.UtcNow;
        if (found.LastUsedAt == null || now - found.LastUsedAt.Value > UseGranularity)
            await db.ApiTokens.Where(t => t.Id == found.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, found.UserId.ToString()), new(ClaimTypes.Name, found.Username), new(ClaimTypes.Role, found.Role.ToString()), new(ApiTokens.PrefixClaim, prefix)], ApiTokens.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiTokens.Scheme));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401; Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
/// <summary>Refuses a bearer-authenticated caller. Token management is done from a signed-in session only, so a leaked token cannot mint its own successors or hide itself from the list.</summary>
public class SessionOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (ApiTokens.IsToken(context.HttpContext.User)) context.Result = new ObjectResult(new { error = "SessionRequired" }) { StatusCode = 403 };
    }
}
