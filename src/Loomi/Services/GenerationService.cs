using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
public class GenerationService(AppDbContext db)
{
    public async Task<Generation> SubmitAsync(Viewer viewer, Guid projectId, string prompt, Provider provider, Operation operation, Guid? parentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000) throw new InvalidOperationException("InvalidPrompt");
        // Serializable SQLite transaction keeps queue admission and deletion consistent.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.CountAsync(g => g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled, ct) >= 25) throw new InvalidOperationException("QueueFull");
        // Refused here rather than silently parked in the queue: a limit is only actionable while the person who hit it is still looking at it.
        if (await db.RemainingAsync(project.UserId, ct) == 0) throw new InvalidOperationException("QuotaExceeded");
        if (parentId != null)
        {
            var parent = await db.Generations.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == parentId, ct);
            if (parent == null || parent.ProjectId != projectId || parent.Status != RunStatus.Completed || parent.LocalImagePath == null) throw new InvalidOperationException("InvalidParent");
        }
        // The project's user, not the actor: an owner helping out must not spend their own quota or take the work over.
        var generation = new Generation { ProjectId = projectId, UserId = project.UserId, Provider = provider, Prompt = prompt.Trim(), Operation = operation, ParentGenerationId = parentId };
        db.Generations.Add(generation);
        project.UpdatedAt = DateTime.UtcNow; project.Status = "Queued";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return generation;
    }
}
