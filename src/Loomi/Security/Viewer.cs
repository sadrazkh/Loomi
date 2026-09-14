using System.Security.Claims;
using Loomi.Models;
namespace Loomi.Security;
/// <summary>Who a request acts as. An owner is not narrowed to their own rows; everyone else is.</summary>
public readonly record struct Viewer(Guid Id, bool IsOwner)
{
    public static Viewer From(ClaimsPrincipal principal) => new(Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty, principal.IsInRole(nameof(UserRole.Owner)));
}
/// <summary>Where ownership is enforced. AppDbContext deliberately has no global filter, so a request-scoped query that skips this reads another user's work.</summary>
public static class Ownership
{
    public static IQueryable<ImageProject> OwnedBy(this IQueryable<ImageProject> projects, Viewer viewer) => projects.Where(x => viewer.IsOwner || x.UserId == viewer.Id);
    public static IQueryable<Generation> OwnedBy(this IQueryable<Generation> generations, Viewer viewer) => generations.Where(x => viewer.IsOwner || x.UserId == viewer.Id);
}
