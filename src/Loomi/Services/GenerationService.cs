using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Services;
public class GenerationService(AppDbContext db, IOptions<BrowserOptions> browser)
{
    public async Task<Generation> SubmitAsync(Viewer viewer, Guid projectId, string prompt, Provider provider, Operation operation, Guid? parentId, IReadOnlyList<InputRef>? inputs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000) throw new InvalidOperationException("InvalidPrompt");
        var refs = inputs ?? [];
        if (refs.Count > browser.Value.MaxInputs) throw new InvalidOperationException("TooManyInputs");
        // Serializable SQLite transaction keeps queue admission and deletion consistent.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.CountAsync(g => g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled, ct) >= 25) throw new InvalidOperationException("QueueFull");
        // Refused here rather than silently parked in the queue: a limit is only actionable while the person who hit it is still looking at it.
        if (await db.RemainingAsync(project.UserId, ct) == 0) throw new InvalidOperationException("QuotaExceeded");
        // Work for a provider no account serves would wait for ever, so it is refused while the person can still pick another. A workspace with no
        // accounts at all is a different thing: nothing is connected yet, the queue holds the work, and /api/auth/status is what explains the wait.
        if (await db.Accounts.AnyAsync(a => a.IsEnabled, ct) && !await db.Accounts.AnyAsync(a => a.IsEnabled && a.Provider == provider, ct)) throw new InvalidOperationException("NoAccount");
        if (parentId != null)
        {
            var parent = await db.Generations.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == parentId, ct);
            if (parent == null || parent.ProjectId != projectId || parent.Status != RunStatus.Completed || parent.LocalImagePath == null) throw new InvalidOperationException("InvalidParent");
        }
        var ordered = await InputsAsync(viewer, parentId, refs, ct);
        if (ordered.Count > browser.Value.MaxInputs) throw new InvalidOperationException("TooManyInputs");
        // One earlier image from the same project is a lineage, so the tree still shows it; several are a set with no single parent.
        if (parentId == null && ordered is [{ SourceGenerationId: { } only }] && await db.Generations.AnyAsync(x => x.Id == only && x.ProjectId == projectId, ct)) parentId = only;
        // The project's user, not the actor: an owner helping out must not spend their own quota or take the work over.
        var generation = new Generation { ProjectId = projectId, UserId = project.UserId, Provider = provider, Prompt = prompt.Trim(), Operation = operation, ParentGenerationId = parentId, Inputs = ordered };
        // The owner pays for the provider accounts themselves, so charging them their own credits would be bookkeeping with nothing behind it,
        // and work whose user no longer exists has no balance to spend — the same reasoning the daily quota uses.
        if (await db.Users.AsNoTracking().Where(u => u.Id == project.UserId).Select(u => (UserRole?)u.Role).FirstOrDefaultAsync(ct) == UserRole.Member)
        {
            var price = await db.PriceAsync(provider, operation, ct);
            if (price > 0)
            {
                if (await db.BalanceAsync(project.UserId, ct) < price) throw new InvalidOperationException("InsufficientCredits");
                generation.CreditCost = price;
                db.CreditEntries.Add(new CreditEntry { UserId = project.UserId, Amount = -price, Kind = CreditKind.Charge, GenerationId = generation.Id });
            }
        }
        db.Generations.Add(generation);
        project.UpdatedAt = DateTime.UtcNow; project.Status = "Queued";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return generation;
    }
    /// <summary>The parent leads, then the references in the order given, each named once. An id that is not the caller's is a miss like any other; one that is theirs but unfinished is refused.</summary>
    private async Task<List<GenerationInput>> InputsAsync(Viewer viewer, Guid? parentId, IReadOnlyList<InputRef> refs, CancellationToken ct)
    {
        var ordered = new List<GenerationInput>();
        if (parentId != null) ordered.Add(new GenerationInput { SourceGenerationId = parentId });
        foreach (var r in refs)
        {
            if ((r.UploadId is null) == (r.GenerationId is null)) throw new InvalidOperationException("InvalidInput");
            if (r.UploadId is { } upload)
            {
                if (ordered.Any(i => i.UploadId == upload)) continue;
                if (!await db.Uploads.OwnedBy(viewer).AnyAsync(u => u.Id == upload, ct)) throw new KeyNotFoundException();
                ordered.Add(new GenerationInput { UploadId = upload });
            }
            else
            {
                if (ordered.Any(i => i.SourceGenerationId == r.GenerationId)) continue;
                var source = await db.Generations.AsNoTracking().OwnedBy(viewer).Where(x => x.Id == r.GenerationId).Select(x => new { x.Status, x.LocalImagePath }).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
                if (source.Status != RunStatus.Completed || source.LocalImagePath == null) throw new InvalidOperationException("InvalidInput");
                ordered.Add(new GenerationInput { SourceGenerationId = r.GenerationId });
            }
        }
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return ordered;
    }
}
