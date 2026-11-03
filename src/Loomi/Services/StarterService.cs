using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>One template of the pack, as written in Data/StarterTemplates.json.</summary>
public record StarterTemplate(string Key, string Category, string Title, string Instructions, string Prompt, int MinInputs, int MaxInputs, int Order);
/// <summary>What pressing "add" did: categories made, templates added, and those of the pack that were already there and stayed as they are.</summary>
public record StarterAdded(int Categories, int Added, int Existing);
/// <summary>Where the samples stand, for the owner's page. Waiting is why no new sample is starting: "drawing", "people" or "capacity". Total and Done
/// belong to the run, so they are zero with none on; ImagesLeftToday is null with no account connected, which is not the same as none left.</summary>
public record SampleStatus(bool On, int WithoutSample, int Failed, int Total, int Done, string? Drawing, string? Waiting, int PackTotal, int PackAdded, int? ImagesLeftToday);
/// <summary>A run as Settings remembers it, so a restart carries on where it was. By is the owner who pressed start: the samples are made in their
/// projects, on their say. Skipped holds what the queue refused outright, which leaves no drawing on the template to tell the story.</summary>
public record SampleRun(Guid By, DateTime StartedAt, int Total, List<Guid> Skipped);
/// <summary>The pack, read once from the embedded file: it ships with the build and only changes with one.</summary>
public static class StarterPack
{
    private static readonly Lazy<IReadOnlyList<StarterTemplate>> pack = new(() =>
    {
        using var file = typeof(StarterPack).Assembly.GetManifestResourceStream("Loomi.Data.StarterTemplates.json") ?? throw new InvalidOperationException("StarterPackMissing");
        return JsonSerializer.Deserialize<List<StarterTemplate>>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    });
    public static IReadOnlyList<StarterTemplate> All => pack.Value;
}
/// <summary>The starter pack on the owner's side: adding it, and the run that makes the samples it has none of. Each sample costs an image of the accounts'
/// day, so a run only ever starts because the owner pressed start; the worker then calls <see cref="StepAsync"/>. The drawing itself is
/// <see cref="TemplateService.DrawSampleAsync"/>, like a sample asked for by hand, which is why the owner is not charged and a template stays out of
/// people's sight until its sample exists.</summary>
public class StarterService(AppDbContext db, TemplateService templates, GrowthSettings settings)
{
    public const string RunKey = "Starter.Samples";
    private const string Off = "off";
    /// <summary>The part of the accounts' day a run leaves alone, as one share in this many. Waiting for an empty queue only protects the people here now: a
    /// run that spends the day's last image would leave whoever comes this evening in a queue nothing can serve until midnight. A share and not a number, so
    /// accounts with small caps of their own are not kept back for ever; the run simply goes on the next day.</summary>
    public const int ReservedShare = 5;
    private sealed record Missing(PromptTemplate Template, Generation? Drawing);

    /// <summary>Adds what is missing and touches nothing else, so a template the owner has edited stays theirs. A category is only made for a template
    /// about to go in it: one the owner renamed does not come back under its old name when everything of it is already there. A template the owner
    /// deleted does come back, since nothing remembers it; one save, so a refused pack leaves nothing half-added.</summary>
    public async Task<StarterAdded> AddPackAsync(CancellationToken ct)
    {
        var have = (await db.Templates.AsNoTracking().Where(t => t.StarterKey != null).Select(t => t.StarterKey!).ToListAsync(ct)).ToHashSet();
        var missing = StarterPack.All.Where(s => !have.Contains(s.Key)).ToList();
        var categories = await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ToListAsync(ct);
        var ids = categories.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Id);
        var order = categories.Count == 0 ? 0 : categories[^1].Order + 1;
        var made = 0;
        foreach (var starter in missing)
        {
            if (!ids.TryGetValue(starter.Category, out var category))
            {
                var fresh = new TemplateCategory { Name = starter.Category, Order = order++ };
                db.TemplateCategories.Add(fresh);
                ids[starter.Category] = category = fresh.Id;
                made++;
            }
            db.Templates.Add(new PromptTemplate
            {
                CategoryId = category, StarterKey = starter.Key, Title = starter.Title, Instructions = starter.Instructions, Prompt = starter.Prompt,
                MinInputs = starter.MinInputs, MaxInputs = starter.MaxInputs, Order = starter.Order, IsActive = true
            });
        }
        await db.SaveChangesAsync(ct);
        return new(made, missing.Count, StarterPack.All.Count - missing.Count);
    }

    public async Task<SampleStatus> StatusAsync(CancellationToken ct)
    {
        await templates.AdoptSamplesAsync(ct);
        var missing = await MissingAsync(ct);
        var drawing = await DrawingAsync(ct);
        var keys = StarterPack.All.Select(s => s.Key).ToList();
        var packAdded = await db.Templates.CountAsync(t => t.StarterKey != null && keys.Contains(t.StarterKey), ct);
        var left = (await CapacityAsync(null, ct))?.Left;
        if (await RunAsync(ct) is not { } current)
            return new(false, missing.Count, missing.Count(m => m.Template.SampleGenerationId != null && (m.Drawing is null || Finished(m.Drawing.Status))), 0, 0, drawing, null, StarterPack.All.Count, packAdded, left);
        var (run, _) = current;
        var (owed, failed) = Split(run, missing);
        var waiting = drawing != null ? "drawing" : owed.FirstOrDefault() is { } next ? await WaitingAsync(next.Provider, ct) : null;
        return new(true, missing.Count, failed, run.Total, Math.Max(0, run.Total - owed.Count - failed), drawing, waiting, StarterPack.All.Count, packAdded, left);
    }
    /// <summary>Pressing start twice, or from a second tab, keeps the run already going. Nothing to make means nothing to start.</summary>
    public async Task<SampleStatus> StartAsync(Guid owner, CancellationToken ct)
    {
        await templates.AdoptSamplesAsync(ct);
        if (await RunAsync(ct) is null)
        {
            var total = await db.Templates.CountAsync(t => t.IsActive && t.SamplePath == null, ct);
            if (total > 0) await settings.SetAsync(RunKey, JsonSerializer.Serialize(new SampleRun(owner, DateTime.UtcNow, total, [])), ct);
        }
        return await StatusAsync(ct);
    }
    /// <summary>No new sample starts. One already asked for stays: it may be drawing as this runs, and it is adopted as usual once it is done.</summary>
    public async Task<SampleStatus> StopAsync(CancellationToken ct)
    {
        await settings.SetAsync(RunKey, Off, ct);
        return await StatusAsync(ct);
    }
    /// <summary>One look at the run; at most one sample asked for.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        if (await RunAsync(ct) is not { } current) return;
        var (run, written) = current;
        // Nobody else adopts a finished sample unless a page happens to be asking, and the run has to know it is finished to go on.
        await templates.AdoptSamplesAsync(ct);
        if (await DrawingAsync(ct) != null) return;
        var (owed, _) = Split(run, await MissingAsync(ct));
        if (owed.FirstOrDefault() is not { } next || !await db.Users.AnyAsync(u => u.Id == run.By && u.Role == UserRole.Owner && !u.IsDisabled, ct)) { await settings.SetAsync(RunKey, Off, ct); return; }
        if (await WaitingAsync(next.Provider, ct) != null) return;
        try { await templates.DrawSampleAsync(new Viewer(run.By, true), next.Id, null, ct); }
        catch (KeyNotFoundException) { /* deleted a moment ago; the next look does not find it */ }
        // Both hold for every template and pass with time, so the run waits rather than crossing off what it could not start.
        catch (InvalidOperationException ex) when (ex.Message is "QueueFull" or "QuotaExceeded") { }
        // This one template is refused, as with no account for its provider, and will be again: one that is not passed over would stop the whole run here.
        // Only if the row is still what was read, so a stop pressed this very moment is not written over.
        catch (InvalidOperationException)
        {
            var passed = JsonSerializer.Serialize(run with { Skipped = [.. run.Skipped, next.Id] });
            await db.Settings.Where(s => s.Key == RunKey && s.Value == written).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, passed), ct);
        }
    }

    /// <summary>A row that cannot be read is no run at all: images are only spent on a clear instruction.</summary>
    private async Task<(SampleRun Run, string Written)?> RunAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(RunKey, ct) is not { } written || written == Off) return null;
        try { return JsonSerializer.Deserialize<SampleRun>(written) is { } run ? (run, written) : null; }
        catch (JsonException) { return null; }
    }
    /// <summary>Active templates with no sample, in the order the owner's list shows within a category, so a run stopped early has reached every category.</summary>
    private async Task<List<Missing>> MissingAsync(CancellationToken ct)
    {
        var missing = await db.Templates.AsNoTracking().Where(t => t.IsActive && t.SamplePath == null).OrderBy(t => t.Order).ThenBy(t => t.Title).ToListAsync(ct);
        var ids = missing.Where(t => t.SampleGenerationId != null).Select(t => t.SampleGenerationId!.Value).ToList();
        var drawings = await db.Generations.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        return missing.Select(t => new Missing(t, t.SampleGenerationId is { } id ? drawings.GetValueOrDefault(id) : null)).ToList();
    }
    private static bool Finished(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Answered;
    /// <summary>What this run still owes and how many it has given up on. Only templates that existed at the start count, since the owner agreed to that
    /// many images and no more. A drawing this run ended badly is left on its template, which keeps saying it failed; one from before the run gets another
    /// try, and so does one a restart cut short, which says nothing about the template. A drawing that finished without being adopted has lost its file.</summary>
    private static (List<PromptTemplate> Owed, int Failed) Split(SampleRun run, List<Missing> missing)
    {
        var part = missing.Where(m => m.Template.CreatedAt <= run.StartedAt).ToList();
        bool GaveUp(Missing m) => run.Skipped.Contains(m.Template.Id) || m.Drawing is { } d && Finished(d.Status) && d.CreatedAt >= run.StartedAt && d.ErrorMessage != "Interrupted";
        return (part.Where(m => !GaveUp(m)).Select(m => m.Template).ToList(), part.Count(GaveUp));
    }
    /// <summary>The title of a sample being drawn, by this run or by hand: one at a time means one, whoever asked. Read off every template, not only those
    /// missing a sample, because the owner may be replacing one that has one.</summary>
    private async Task<string?> DrawingAsync(CancellationToken ct)
    {
        var open = await db.Templates.AsNoTracking().Where(t => t.SampleGenerationId != null).Select(t => new { t.Title, Drawing = t.SampleGenerationId!.Value }).ToListAsync(ct);
        var ids = open.Select(o => o.Drawing).ToList();
        var live = await db.Generations.AsNoTracking().Where(g => ids.Contains(g.Id)).Unfinished().Select(g => g.Id).ToListAsync(ct);
        return open.FirstOrDefault(o => live.Contains(o.Drawing))?.Title;
    }
    /// <summary>Why the next sample should not start yet, if there is a reason.</summary>
    private async Task<string?> WaitingAsync(Provider provider, CancellationToken ct)
    {
        // Being drawn counts as well as waiting. Two accounts could do both, but with one a sample queued now would go ahead of that member's next
        // request: the queue takes turns between people, and the owner has had no turn.
        var owners = db.Users.Where(u => u.Role == UserRole.Owner).Select(u => u.Id);
        if (await db.Generations.AsNoTracking().Where(g => !owners.Contains(g.UserId)).Unfinished().AnyAsync(ct)) return "people";
        return await CapacityAsync(provider, ct) is { } capacity && capacity.Left <= capacity.Cap / ReservedShare ? "capacity" : null;
    }
    /// <summary>What the accounts can still draw today and what they can draw in a day, by the caps the dispatcher itself keeps to. Null with no account,
    /// where the queue holds the work until one is connected.</summary>
    private async Task<(int Left, int Cap)?> CapacityAsync(Provider? provider, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsEnabled && (provider == null || a.Provider == provider)).Select(a => new { a.Id, a.DailyCap }).ToListAsync(ct);
        if (accounts.Count == 0) return null;
        var used = await db.UsageByAccountAsync(ct);
        return (accounts.Sum(a => Math.Max(0, a.DailyCap - used.GetValueOrDefault(a.Id))), accounts.Sum(a => a.DailyCap));
    }
}
