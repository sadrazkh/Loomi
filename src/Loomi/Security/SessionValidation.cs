using Loomi.Data;
using Loomi.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Security;
/// <summary>Re-reads the signed-in user on every request. Without it a cookie keeps its access for its full 12 hours after the account behind it was disabled, deleted or demoted.</summary>
public static class SessionValidation
{
    public static async Task RejectStalePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var viewer = Viewer.From(context.Principal!);
        var user = await context.HttpContext.RequestServices.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == viewer.Id, context.HttpContext.RequestAborted);
        // The role is checked too: a demoted owner would otherwise carry an Owner claim, and the owner-only routes with it, until the cookie expired.
        if (user is not null && !user.IsDisabled && (user.Role == UserRole.Owner) == viewer.IsOwner) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
