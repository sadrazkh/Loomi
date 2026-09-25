using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Repositories;
public class ProjectRepository(AppDbContext db)
{
    /// <summary>One shape wherever a project is handed out, so a chat just started reads exactly like the same chat in the list.</summary>
    private static IQueryable<object> Shaped(IQueryable<ImageProject> projects) => projects
        .Select(x => (object)new { x.Id, x.Title, x.CreatedAt, x.UpdatedAt, x.Status, x.ConversationUrl, GenerationCount = x.Generations.Count, CoverId = x.Generations.Where(g => g.LocalImagePath != null).OrderByDescending(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefault() });
    public Task<List<object>> ListAsync(Viewer viewer, CancellationToken ct) => Shaped(db.Projects.AsNoTracking().OwnedBy(viewer).OrderByDescending(x => x.UpdatedAt)).ToListAsync(ct);
    public Task<object?> GetAsync(Viewer viewer, Guid id, CancellationToken ct) => Shaped(db.Projects.AsNoTracking().OwnedBy(viewer).Where(x => x.Id == id)).FirstOrDefaultAsync(ct);
}
