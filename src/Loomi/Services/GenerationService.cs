using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
public class GenerationService(AppDbContext db)
{
    public async Task<Generation> SubmitAsync(Guid projectId, string prompt, Operation operation, Guid? parentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000) throw new InvalidOperationException("InvalidPrompt");
        // Serializable SQLite transaction keeps queue admission and deletion consistent.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.FindAsync([projectId], ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.CountAsync(g => g.Status != RunStatus.Completed && g.Status != RunStatus.Failed, ct) >= 25) throw new InvalidOperationException("QueueFull");
        if (parentId != null)
        {
            var parent = await db.Generations.FindAsync([parentId], ct);
            if (parent == null || parent.ProjectId != projectId || parent.Status != RunStatus.Completed || parent.LocalImagePath == null) throw new InvalidOperationException("InvalidParent");
        }
        var generation = new Generation { ProjectId = projectId, Prompt = prompt.Trim(), Operation = operation, ParentGenerationId = parentId };
        db.Generations.Add(generation);
        project.UpdatedAt = DateTime.UtcNow; project.Status = "Queued";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return generation;
    }
}
