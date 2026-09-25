using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Repositories;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
[ApiController, Authorize, Route("api/projects")]
public class ProjectsController(AppDbContext db, ProjectRepository projects, GenerationService generations, IImageStorage storage) : ControllerBase
{
    private Viewer Me => Viewer.From(User);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await projects.ListAsync(Me, ct));
    [HttpPost] public async Task<IActionResult> Create(CreateProject request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { error = "InvalidTitle" });
        // Naming a project must not depend on a browser: the account is chosen when the work is dispatched, and it opens its own chat then.
        var project = new ImageProject { UserId = Me.Id, Title = request.Title.Trim() };
        db.Projects.Add(project); await db.SaveChangesAsync(ct);
        return Created($"/api/projects/{project.Id}", new { project.Id, project.Title, project.CreatedAt, project.UpdatedAt, project.Status, project.ConversationUrl });
    }
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        return Ok(new { p.Id, p.Title, p.Status, p.CreatedAt, p.UpdatedAt, p.ConversationUrl });
    }
    [HttpGet("{id:guid}/generations")] public async Task<IActionResult> ListGenerations(Guid id, CancellationToken ct)
    {
        if (!await db.Projects.OwnedBy(Me).AnyAsync(x => x.Id == id, ct)) return NotFound();
        return Ok((await db.Generations.AsNoTracking().Include(g => g.Inputs).Where(g => g.ProjectId == id).OrderBy(g => g.CreatedAt).ToListAsync(ct)).Select(GenerationDto.From));
    }
    /// <summary>Opens a new conversation inside the project. Kept for callers written before a project was one conversation; the web sends messages.</summary>
    [HttpPost("{id:guid}/generate")] public async Task<IActionResult> Generate(Guid id, PromptRequest request, CancellationToken ct)
    {
        var g = await generations.SubmitAsync(Me, id, request.Prompt, request.Provider ?? Provider.ChatGPT, Operation.Generate, null, request.Inputs, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    /// <summary>The next message in the project's one conversation. Whether that continues it, reopens it from the latest picture or starts it is the
    /// server's call, so the caller never picks a turn to reply to and never sends a picture the conversation already has.</summary>
    [HttpPost("{id:guid}/messages")] public async Task<IActionResult> Message(Guid id, PromptRequest request, CancellationToken ct)
    {
        var g = await generations.SendAsync(Me, id, request.Prompt, request.Inputs, request.Provider, ct);
        return Accepted($"/api/generations/{g.Id}", GenerationDto.From(g));
    }
    /// <summary>A new chat: a new project and its first message in one call, so a refused message leaves no empty project behind. A picture from
    /// another chat goes along as a reference, which is how a branch becomes a chat of its own.</summary>
    [HttpPost("start")] public async Task<IActionResult> Start(StartChat request, CancellationToken ct)
    {
        var g = await generations.StartAsync(Me, request.Title, request.Prompt, request.Inputs, request.Provider, ct);
        return Created($"/api/projects/{g.ProjectId}", new { project = await projects.GetAsync(Me, g.ProjectId, ct), generation = GenerationDto.From(g) });
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.AnyAsync(g => g.ProjectId == id && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled && g.Status != RunStatus.Answered, ct)) throw new InvalidOperationException("ProjectBusy");
        await db.Generations.Where(g => g.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.ParentGenerationId, (Guid?)null), ct);
        db.Projects.Remove(project); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        storage.DeleteProject(id); return NoContent();
    }
}
