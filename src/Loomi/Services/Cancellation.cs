using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Stopping work, wherever the request came from. One place, so the bot and the API cannot drift apart over what a cancel does to the credits.</summary>
public static class Cancellation
{
    public static IQueryable<Generation> Unfinished(this IQueryable<Generation> generations) =>
        generations.Where(g => g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled);
    /// <summary>A queued row is simply written; one already running has to be interrupted, and the run itself records the outcome.</summary>
    public static async Task<Generation> CancelAsync(AppDbContext db, Generation g, CancellationToken ct)
    {
        if (GenerationWorker.Interrupt(g.Id)) return g;
        g.Status = RunStatus.Cancelled;
        g.Project.Status = await db.Generations.AnyAsync(x => x.ProjectId == g.ProjectId && x.Id != g.Id && x.Status == RunStatus.Queued, ct) ? "Queued" : "Ready";
        await db.SaveChangesAsync(ct);
        await db.RefundAsync(g.Id, ct);
        return g;
    }
}
