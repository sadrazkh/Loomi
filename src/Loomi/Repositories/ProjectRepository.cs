using Loomi.Data;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Repositories;
public class ProjectRepository(AppDbContext db)
{
    public Task<List<object>> ListAsync(CancellationToken ct) => db.Projects.AsNoTracking().OrderByDescending(x => x.UpdatedAt)
        .Select(x => (object)new { x.Id, x.Title, x.CreatedAt, x.UpdatedAt, x.Status, x.ConversationUrl, GenerationCount = x.Generations.Count, CoverId = x.Generations.Where(g => g.LocalImagePath != null).OrderByDescending(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefault() }).ToListAsync(ct);
}
