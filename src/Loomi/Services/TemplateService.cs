using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>A template's prompt as it travels with one piece of work: which template, what it was called then, and the words only the AI reads.</summary>
public record TemplateUse(Guid Id, string Title, string HiddenPrompt);
/// <summary>Templates in use: a person picks one, sends their photos and perhaps a note, and the owner's prompt goes with them unseen. The owner makes each
/// template's sample either by uploading one or by having the prompt drawn, and a drawn sample becomes the template's own once it is finished.</summary>
public class TemplateService(AppDbContext db, GenerationService generations, IImageStorage storage)
{
    public const string SampleProjectPrefix = "Sample · ";
    /// <summary>What the AI is sent: the template's prompt, then whatever the person added. Everything a person sees of the work is only the second part.</summary>
    public static string PromptFor(Generation g) =>
        string.IsNullOrWhiteSpace(g.HiddenPrompt) ? g.Prompt : string.IsNullOrWhiteSpace(g.Prompt) ? g.HiddenPrompt : g.HiddenPrompt + "\n\n" + g.Prompt;
    /// <summary>"none", "ready", "generating" or "failed": where a template's sample stands, read off the work drawing it when there is some.</summary>
    public static string SampleState(PromptTemplate t, RunStatus? drawing) =>
        t.SampleGenerationId is null ? (t.SamplePath is null ? "none" : "ready")
        : drawing is null or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Answered ? "failed" : "generating";

    public async Task<Generation> UseAsync(Viewer viewer, Guid templateId, string? note, IReadOnlyList<InputRef>? inputs, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive && t.SamplePath != null, ct) ?? throw new KeyNotFoundException();
        var refs = (inputs ?? []).ToList();
        // A template asks for the person's own photos, so a reference to an earlier result does not count as one.
        if (refs.Count < template.MinInputs || refs.Count > template.MaxInputs || refs.Any(r => r.UploadId is null || r.GenerationId is not null)) throw new InvalidOperationException("TemplateInputs");
        note = note?.Trim() ?? "";
        if (note.Length > 1000) throw new InvalidOperationException("InvalidPrompt");
        var project = new ImageProject { UserId = viewer.Id, Title = template.Title };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        try
        {
            // The sample goes as a copy the person owns, like any reference of theirs: it is then theirs to see in the history, and it is kept for as long
            // as the work that used it, whatever happens to the template later.
            if (template.SendSample && template.SamplePath is { } sample && File.Exists(storage.Resolve(sample)))
            {
                var bytes = await File.ReadAllBytesAsync(storage.Resolve(sample), ct);
                var upload = new Upload { UserId = viewer.Id, Bytes = bytes.Length };
                upload.Path = await storage.SaveUploadAsync(viewer.Id, upload.Id, bytes, ct);
                upload.ContentType = ImageStorage.ContentTypeFor(upload.Path);
                db.Uploads.Add(upload);
                await db.SaveChangesAsync(ct);
                refs.Add(new InputRef(upload.Id, null));
            }
            return await generations.SubmitAsync(viewer, project.Id, note, template.Provider, Operation.Generate, null, refs, ct, new TemplateUse(template.Id, template.Title, template.Prompt));
        }
        catch
        {
            // Refused work leaves no empty project behind in the person's list.
            db.ChangeTracker.Clear();
            await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }
    /// <summary>Draws the template's prompt in a project of the owner's own, where they can watch it like any other work and follow it up.</summary>
    public async Task<Generation> DrawSampleAsync(Viewer owner, Guid templateId, IReadOnlyList<InputRef>? inputs, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId, ct) ?? throw new KeyNotFoundException();
        var project = new ImageProject { UserId = owner.Id, Title = SampleProjectPrefix + template.Title };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        try
        {
            var drawing = await generations.SubmitAsync(owner, project.Id, template.Prompt, template.Provider, Operation.Generate, null, inputs, ct);
            template.SampleGenerationId = drawing.Id;
            template.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return drawing;
        }
        catch
        {
            db.ChangeTracker.Clear();
            await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }
    /// <summary>A finished drawing becomes the template's sample, copied so it stays when the owner tidies away the project it was drawn in.</summary>
    public async Task AdoptSamplesAsync(CancellationToken ct)
    {
        foreach (var template in await db.Templates.Where(t => t.SampleGenerationId != null).ToListAsync(ct))
        {
            var drawing = await db.Generations.AsNoTracking().FirstOrDefaultAsync(g => g.Id == template.SampleGenerationId, ct);
            if (drawing is { Status: RunStatus.Completed, LocalImagePath: { } path } && File.Exists(storage.Resolve(path)))
                await SetSampleAsync(template, await File.ReadAllBytesAsync(storage.Resolve(path), ct), ct);
        }
        await db.SaveChangesAsync(ct);
    }
    /// <summary>Replaces the sample, saved under a new name each time so no browser keeps showing the old one.</summary>
    public async Task SetSampleAsync(PromptTemplate template, byte[] bytes, CancellationToken ct)
    {
        var previous = template.SamplePath;
        template.SamplePath = await storage.SaveTemplateAsync(template.Id, bytes, ct);
        template.SampleGenerationId = null;
        template.UpdatedAt = DateTime.UtcNow;
        if (previous is not null && previous != template.SamplePath) storage.Delete(previous);
    }
}
