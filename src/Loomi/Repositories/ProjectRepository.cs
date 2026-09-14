using Loomi.Data;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Repositories;
public class ProjectRepository(AppDbContext db)
{
    public Task<List<object>> ListAsync(Viewer viewer, CancellationToken ct) => db.Projects.AsNoTracking().OwnedBy(viewer).OrderByDescending(x => x.UpdatedAt)
        .Select(x => (object)new { x.Id, x.Title, x.CreatedAt, x.UpdatedAt, x.Status, x.ConversationUrl, GenerationCount = x.Generations.Count, CoverId = x.Generations.Where(g => g.LocalImagePath != null).OrderByDescending(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefault() }).ToListAsync(ct);
}
