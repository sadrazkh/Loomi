using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
[ApiController, Authorize, Route("api/generations")]
public class GenerationsController(AppDbContext db, GenerationService service, IImageStorage storage) : ControllerBase
{
    private Viewer Me => Viewer.From(User);
    /// <summary>Another user's id is a miss, not a refusal, so ids cannot be probed.</summary>
    private async Task<Generation> MineAsync(Guid id, CancellationToken ct) => await db.Generations.OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(GenerationDto.From(await MineAsync(id, ct)));
    [HttpPost("{id:guid}/edit")] public Task<IActionResult> Edit(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Edit, ct);
    [HttpPost("{id:guid}/branch")] public Task<IActionResult> Branch(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Branch, ct);
    private async Task<IActionResult> Submit(Guid id, PromptRequest r, Operation operation, CancellationToken ct)
    {
        var parent = await MineAsync(id, ct);
        var g = await service.SubmitAsync(Me, parent.ProjectId, r.Prompt, operation, id, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    [HttpGet("{id:guid}/image")] public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var g = await db.Generations.AsNoTracking().OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g?.LocalImagePath == null) return NotFound();
        var path = storage.Resolve(g.LocalImagePath);
        if (!System.IO.File.Exists(path)) return NotFound();
        var mime = Path.GetExtension(path) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return PhysicalFile(path, mime, enableRangeProcessing: true);
    }
}
