using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Repositories;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
[ApiController, Authorize, Route("api/projects")]
public class ProjectsController(AppDbContext db, ProjectRepository projects, GenerationService generations, BrowserAutomationService browser, IImageStorage storage) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await projects.ListAsync(ct));
    [HttpPost] public async Task<IActionResult> Create(CreateProject request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { error = "InvalidTitle" });
        await browser.NewProjectAsync(ct);
        var project = new ImageProject { Title = request.Title.Trim() };
        db.Projects.Add(project); await db.SaveChangesAsync(ct);
        return Created($"/api/projects/{project.Id}", new { project.Id, project.Title, project.CreatedAt, project.UpdatedAt, project.Status, project.ConversationUrl });
    }
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        return Ok(new { p.Id, p.Title, p.Status, p.CreatedAt, p.UpdatedAt, p.ConversationUrl });
    }
    [HttpGet("{id:guid}/generations")] public async Task<IActionResult> ListGenerations(Guid id, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == id, ct)) return NotFound();
        return Ok((await db.Generations.AsNoTracking().Where(g => g.ProjectId == id).OrderBy(g => g.CreatedAt).ToListAsync(ct)).Select(GenerationDto.From));
    }
    [HttpPost("{id:guid}/generate")] public async Task<IActionResult> Generate(Guid id, PromptRequest request, CancellationToken ct)
    {
        var g = await generations.SubmitAsync(id, request.Prompt, Operation.Generate, null, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.FindAsync([id], ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.AnyAsync(g => g.ProjectId == id && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed, ct)) throw new InvalidOperationException("ProjectBusy");
        await db.Generations.Where(g => g.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.ParentGenerationId, (Guid?)null), ct);
        db.Projects.Remove(project); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        storage.DeleteProject(id); return NoContent();
    }
}
