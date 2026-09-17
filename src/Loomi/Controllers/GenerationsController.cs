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
    private async Task<Generation> MineAsync(Guid id, CancellationToken ct) => await db.Generations.Include(x => x.Inputs).OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(GenerationDto.From(await MineAsync(id, ct)));
    [HttpPost("{id:guid}/edit")] public Task<IActionResult> Edit(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Edit, ct);
    [HttpPost("{id:guid}/branch")] public Task<IActionResult> Branch(Guid id, PromptRequest r, CancellationToken ct) => Submit(id, r, Operation.Branch, ct);
    private async Task<IActionResult> Submit(Guid id, PromptRequest r, Operation operation, CancellationToken ct)
    {
        var parent = await MineAsync(id, ct);
        var g = await service.SubmitAsync(Me, parent.ProjectId, r.Prompt, parent.Provider, operation, id, r.Inputs, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    /// <summary>Everything not finished. An owner sees the whole queue; everyone else only their own, so cancelling cannot reach across users.</summary>
    private IQueryable<Generation> Unfinished() => db.Generations.Include(g => g.Project).Include(g => g.Inputs).OwnedBy(Me).Unfinished();
    [HttpPost("{id:guid}/cancel")] public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var g = await Unfinished().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        return Ok(GenerationDto.From(await CancelAsync(g, ct)));
    }
    [HttpPost("cancel")] public async Task<IActionResult> CancelAll(CancellationToken ct)
    {
        var cancelled = 0;
        foreach (var g in await Unfinished().ToListAsync(ct)) { await CancelAsync(g, ct); cancelled++; }
        return Ok(new { cancelled });
    }
    private Task<Generation> CancelAsync(Generation g, CancellationToken ct) => Cancellation.CancelAsync(db, g, ct);
    [HttpGet("{id:guid}/image")] public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var g = await db.Generations.AsNoTracking().OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g?.LocalImagePath == null) return NotFound();
        var path = storage.Resolve(g.LocalImagePath);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, ImageStorage.ContentTypeFor(path), enableRangeProcessing: true);
    }
}
