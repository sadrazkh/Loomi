using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers;
public record TemplateUseRequest(string? Note, List<InputRef>? Inputs);
public record SampleRequest(List<InputRef>? Inputs);
public record CategoryRequest(string? Name, int Order);
public record TemplateRequest(Guid CategoryId, string? Title, string? Instructions, string? Prompt, Provider Provider, int MinInputs, int MaxInputs, bool SendSample, bool IsActive, int Order);
/// <summary>Everyone browses templates and uses them; only the owner reads a template's prompt or changes anything. What a member is sent never
/// carries the prompt: the list, the sample and the work made from it are all it takes to choose one and use it.</summary>
[ApiController, Authorize, Route("api/templates")]
public class TemplatesController(AppDbContext db, TemplateService templates, IImageStorage storage, ImagePreviews previews, IOptions<BrowserOptions> browser) : ControllerBase
{
    private Viewer Me => Viewer.From(User);
    private static string? SampleUrl(PromptTemplate t) => t.SamplePath is null ? null : $"/api/templates/{t.Id}/sample?v={t.UpdatedAt.Ticks}";

    /// <summary>Categories in order, each with the active templates that have a sample to choose by; a category left with none is not shown.</summary>
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct)
    {
        await templates.AdoptSamplesAsync(ct);
        var categories = await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        var visible = await db.Templates.AsNoTracking().Where(t => t.IsActive && t.SamplePath != null).OrderBy(t => t.Order).ThenBy(t => t.Title).ToListAsync(ct);
        // A member sees what one use costs them; the owner is not charged, so a number would suggest a price that does not apply.
        var prices = new Dictionary<Provider, int?>();
        foreach (var provider in visible.Select(t => t.Provider).Distinct())
            prices[provider] = Me.IsOwner ? null : await db.PriceAsync(provider, Operation.Generate, ct);
        return Ok(categories.Select(c => new
        {
            c.Id, c.Name,
            templates = visible.Where(t => t.CategoryId == c.Id).Select(t => new { t.Id, t.Title, t.Instructions, t.MinInputs, t.MaxInputs, sampleUrl = SampleUrl(t), price = prices[t.Provider] }).ToList()
        }).Where(c => c.templates.Count > 0));
    }
    /// <summary>With w, a WebP copy no wider than that, as for a generation's picture: a card needs a fraction of what the sample weighs.</summary>
    [HttpGet("{id:guid}/sample")] public async Task<IActionResult> Sample(Guid id, string? w, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && (t.IsActive || Me.IsOwner), ct) ?? throw new KeyNotFoundException();
        if (template.SamplePath is null || !System.IO.File.Exists(storage.Resolve(template.SamplePath))) return NotFound();
        // The address carries the version, so a sample can be kept a long while and still change the moment the owner replaces it.
        Response.Headers.CacheControl = "private, max-age=604800";
        if (ImagePreviews.WidthFor(w) is { } width && await previews.GetAsync(template.SamplePath, width, ct) is { } preview) return PhysicalFile(preview, "image/webp");
        return PhysicalFile(storage.Resolve(template.SamplePath), ImageStorage.ContentTypeFor(template.SamplePath));
    }
    [HttpPost("{id:guid}/use")] public async Task<IActionResult> Use(Guid id, TemplateUseRequest request, CancellationToken ct)
    {
        var generation = await templates.UseAsync(Me, id, request.Note, request.Inputs, ct);
        return Accepted($"/api/generations/{generation.Id}", new { projectId = generation.ProjectId, generation = GenerationDto.From(generation) });
    }

    // ---- The owner's side: categories, templates and their samples ----

    [Authorize(Roles = nameof(UserRole.Owner)), HttpGet("manage")] public async Task<IActionResult> Manage(CancellationToken ct)
    {
        await templates.AdoptSamplesAsync(ct);
        var all = await db.Templates.AsNoTracking().OrderBy(t => t.Order).ThenBy(t => t.Title).ToListAsync(ct);
        var drawing = all.Where(t => t.SampleGenerationId != null).Select(t => t.SampleGenerationId!.Value).ToList();
        var statuses = await db.Generations.AsNoTracking().Where(g => drawing.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Status, ct);
        return Ok(new
        {
            categories = await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name, c.Order }).ToListAsync(ct),
            templates = all.Select(t => ManageView(t, t.SampleGenerationId is { } g && statuses.TryGetValue(g, out var s) ? s : null))
        });
    }
    private static object ManageView(PromptTemplate t, RunStatus? drawing) => new
    {
        t.Id, t.CategoryId, t.Title, t.Instructions, t.Prompt, t.Provider, t.MinInputs, t.MaxInputs, t.SendSample, t.IsActive, t.Order,
        sampleUrl = SampleUrl(t), sampleState = TemplateService.SampleState(t, drawing), t.UpdatedAt
    };
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPost("categories")] public async Task<IActionResult> CreateCategory(CategoryRequest request, CancellationToken ct)
    {
        if (CategoryName(request) is not { } name) return BadRequest(new { error = "InvalidTemplate" });
        var category = new TemplateCategory { Name = name, Order = request.Order };
        db.TemplateCategories.Add(category); await db.SaveChangesAsync(ct);
        return Ok(new { category.Id, category.Name, category.Order });
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPut("categories/{id:guid}")] public async Task<IActionResult> UpdateCategory(Guid id, CategoryRequest request, CancellationToken ct)
    {
        var category = await db.TemplateCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        if (CategoryName(request) is not { } name) return BadRequest(new { error = "InvalidTemplate" });
        (category.Name, category.Order) = (name, request.Order);
        await db.SaveChangesAsync(ct);
        return Ok(new { category.Id, category.Name, category.Order });
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpDelete("categories/{id:guid}")] public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        var category = await db.TemplateCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        if (await db.Templates.AnyAsync(t => t.CategoryId == id, ct)) throw new InvalidOperationException("CategoryNotEmpty");
        db.TemplateCategories.Remove(category); await db.SaveChangesAsync(ct);
        return NoContent();
    }
    private static string? CategoryName(CategoryRequest request) => request.Name?.Trim() is { Length: > 0 and <= 64 } name ? name : null;

    [Authorize(Roles = nameof(UserRole.Owner)), HttpPost] public async Task<IActionResult> Create(TemplateRequest request, CancellationToken ct)
    {
        var template = new PromptTemplate();
        if (!await ApplyAsync(template, request, ct)) return BadRequest(new { error = "InvalidTemplate" });
        db.Templates.Add(template); await db.SaveChangesAsync(ct);
        return Ok(ManageView(template, null));
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, TemplateRequest request, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!await ApplyAsync(template, request, ct)) return BadRequest(new { error = "InvalidTemplate" });
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(ManageView(template, await DrawingAsync(template, ct)));
    }
    /// <summary>Work already made from it keeps the title it had; only the template and its sample go.</summary>
    [Authorize(Roles = nameof(UserRole.Owner)), HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        db.Templates.Remove(template); await db.SaveChangesAsync(ct);
        if (template.SamplePath is not null) storage.Delete(template.SamplePath);
        return NoContent();
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPost("{id:guid}/sample"), RequestSizeLimit(UploadsController.MaxFileBytes + 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = UploadsController.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadSample(Guid id, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!Request.HasFormContentType || (await Request.ReadFormAsync(ct)).Files is not [var file]) return BadRequest(new { error = "NoFiles" });
        if (file.Length > UploadsController.MaxFileBytes) return BadRequest(new { error = "FileTooLarge" });
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (ImageStorage.ExtensionFor(bytes) is null) return BadRequest(new { error = "InvalidImage" });
        await templates.SetSampleAsync(template, bytes, ct);
        await db.SaveChangesAsync(ct);
        return Ok(ManageView(template, null));
    }
    [Authorize(Roles = nameof(UserRole.Owner)), HttpPost("{id:guid}/sample/generate")] public async Task<IActionResult> DrawSample(Guid id, SampleRequest request, CancellationToken ct)
    {
        var drawing = await templates.DrawSampleAsync(Me, id, request.Inputs, ct);
        return Accepted($"/api/generations/{drawing.Id}", new { projectId = drawing.ProjectId, generationId = drawing.Id });
    }
    private async Task<RunStatus?> DrawingAsync(PromptTemplate t, CancellationToken ct) =>
        t.SampleGenerationId is { } g ? await db.Generations.AsNoTracking().Where(x => x.Id == g).Select(x => (RunStatus?)x.Status).FirstOrDefaultAsync(ct) : null;
    /// <summary>A template with the sample going along as a reference leaves room for one photo fewer: the AI takes at most MaxInputs images in all.</summary>
    private async Task<bool> ApplyAsync(PromptTemplate t, TemplateRequest r, CancellationToken ct)
    {
        var title = r.Title?.Trim(); var prompt = r.Prompt?.Trim(); var instructions = string.IsNullOrWhiteSpace(r.Instructions) ? null : r.Instructions.Trim();
        var ceiling = browser.Value.MaxInputs - (r.SendSample ? 1 : 0);
        if (title is not { Length: > 0 and <= 80 } || prompt is not { Length: > 0 and <= 8000 } || instructions?.Length > 400
            || r.MinInputs < 0 || r.MaxInputs < r.MinInputs || r.MaxInputs > ceiling || !Enum.IsDefined(r.Provider)
            || !await db.TemplateCategories.AnyAsync(c => c.Id == r.CategoryId, ct)) return false;
        (t.CategoryId, t.Title, t.Instructions, t.Prompt, t.Provider, t.MinInputs, t.MaxInputs, t.SendSample, t.IsActive, t.Order) =
            (r.CategoryId, title, instructions, prompt, r.Provider, r.MinInputs, r.MaxInputs, r.SendSample, r.IsActive, r.Order);
        return true;
    }
}
