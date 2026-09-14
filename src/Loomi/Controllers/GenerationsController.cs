using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
[ApiController, Authorize, Route("api/generations")]
public class GenerationsController(AppDbContext db, GenerationService service, IImageStorage storage) : ControllerBase
{
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(GenerationDto.From(await db.Generations.FindAsync([id], ct) ?? throw new KeyNotFoundException()));
    [HttpPost("{id:guid}/edit")] public Task<IActionResult> Edit(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Edit, ct);
    [HttpPost("{id:guid}/branch")] public Task<IActionResult> Branch(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Branch, ct);
    private async Task<IActionResult> Submit(Guid id, PromptRequest r, Operation operation, CancellationToken ct)
    {
        var parent = await db.Generations.FindAsync([id], ct) ?? throw new KeyNotFoundException();
        var g = await service.SubmitAsync(parent.ProjectId, r.Prompt, operation, id, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    [HttpGet("{id:guid}/image")] public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var g = await db.Generations.FindAsync([id], ct);
        if (g?.LocalImagePath == null) return NotFound();
        var path = storage.Resolve(g.LocalImagePath);
        if (!System.IO.File.Exists(path)) return NotFound();
        var mime = Path.GetExtension(path) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return PhysicalFile(path, mime, enableRangeProcessing: true);
    }
}
