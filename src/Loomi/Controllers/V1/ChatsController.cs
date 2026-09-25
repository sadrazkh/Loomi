using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers.V1;
/// <summary>Chats, each one Loomi project and one conversation with the AI, and the messages sent into them. A message is accepted at once and
/// finishes later; /api/v1/messages is where a caller waits for it.</summary>
[Route("api/v1/chats")]
public class ChatsController(AppDbContext db, GenerationService generations, IImageStorage storage, QueueEstimates queue, IOptions<BrowserOptions> browser) : ApiV1Controller
{
    /// <summary>Most recently active first. A chat moves to the top when a message is sent into it, so before= pages by that same moment.</summary>
    [HttpGet, ProducesResponseType<ChatPage>(200)]
    public async Task<IActionResult> List(int limit = 20, DateTimeOffset? before = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var chats = db.Projects.AsNoTracking().OwnedBy(Me);
        if (before is { } b) { var cut = b.UtcDateTime; chats = chats.Where(p => p.UpdatedAt < cut); }
        var page = await Chats.ListAsync(chats.OrderByDescending(p => p.UpdatedAt).Take(limit + 1), ct);
        return Ok(new ChatPage(page.Take(limit).ToList(), page.Count > limit ? page[limit - 1].UpdatedAt : null));
    }
    /// <summary>With a prompt, the chat begins with it as its first message and is named by it unless a title is given. Without one, the chat is
    /// only a name, and its first message comes later.</summary>
    [HttpPost, RequestSizeLimit(Pictures.MaxBodyBytes), ProducesResponseType<ChatStarted>(201), ProducesResponseType<ErrorDto>(409)]
    public async Task<IActionResult> Create(NewChatRequest? request, CancellationToken ct)
    {
        request ??= new(null, null, null, null);
        var title = request.Title?.Trim();
        if (title is { Length: > 160 }) return Invalid("InvalidTitle");
        if (request.Prompt is null)
        {
            if (string.IsNullOrEmpty(title)) return Invalid("InvalidTitle");
            if (request.Images is { Count: > 0 }) return Invalid("InvalidPrompt");
            var project = new ImageProject { UserId = Me.Id, Title = title };
            db.Projects.Add(project); await db.SaveChangesAsync(ct);
            return Created($"/api/v1/chats/{project.Id}", new ChatStarted(await Chats.OneAsync(db, Me, project.Id, ct), null));
        }
        if (PromptError(request.Prompt) is { } bad) return Invalid(bad);
        if (!TryProvider(request.Provider, out var provider)) return Invalid("InvalidProvider");
        return await Pictures.WithAsync(db, storage, Me.Id, request.Images, browser.Value.MaxInputs, async refs =>
        {
            var g = await generations.StartAsync(Me, title, request.Prompt!, refs, provider, ct);
            return Created($"/api/v1/chats/{g.ProjectId}", new ChatStarted(await Chats.OneAsync(db, Me, g.ProjectId, ct), MessageDto.From(g, queue)));
        }, ct);
    }
    [HttpGet("{id:guid}"), ProducesResponseType<ChatDetail>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var chat = await Chats.OneAsync(db, Me, id, ct);
        var messages = await db.Generations.AsNoTracking().Where(g => g.ProjectId == id).OrderBy(g => g.CreatedAt).ToListAsync(ct);
        return Ok(new ChatDetail(chat, messages.Select(g => MessageDto.From(g, queue)).ToList()));
    }
    /// <summary>ProjectsController's rules: refused while anything in the chat is unfinished, lineage links cut first, the files after the rows.</summary>
    [HttpDelete("{id:guid}"), ProducesResponseType(204), ProducesResponseType<ErrorDto>(409)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.Where(g => g.ProjectId == id).Unfinished().AnyAsync(ct)) throw new InvalidOperationException("ProjectBusy");
        await db.Generations.Where(g => g.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.ParentGenerationId, (Guid?)null), ct);
        db.Projects.Remove(project); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        storage.DeleteProject(id);
        return NoContent();
    }
    /// <summary>The next message in the conversation. Nothing already in it is sent again, and the chat takes one message at a time: while the last
    /// is unfinished this is 409 ProjectBusy, as it is on the site itself.</summary>
    [HttpPost("{id:guid}/messages"), RequestSizeLimit(Pictures.MaxBodyBytes), ProducesResponseType<MessageSent>(202), ProducesResponseType<ErrorDto>(409)]
    public async Task<IActionResult> Send(Guid id, NewMessageRequest request, CancellationToken ct)
    {
        if (PromptError(request.Prompt) is { } bad) return Invalid(bad);
        if (!TryProvider(request.Provider, out var provider)) return Invalid("InvalidProvider");
        return await Pictures.WithAsync(db, storage, Me.Id, request.Images, browser.Value.MaxInputs, async refs =>
        {
            var g = await generations.SendAsync(Me, id, request.Prompt!, refs, provider, ct);
            return Accepted($"/api/v1/messages/{g.Id}", new MessageSent(MessageDto.From(g, queue)));
        }, ct);
    }
}
/// <summary>Chats as the API shows them, counted and summarised in one query.</summary>
internal static class Chats
{
    public static async Task<List<ChatDto>> ListAsync(IQueryable<ImageProject> projects, CancellationToken ct) =>
        (await projects.Select(p => new
        {
            p.Id, p.Title, p.CreatedAt, p.UpdatedAt, Count = p.Generations.Count,
            Busy = p.Generations.Any(g => g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled && g.Status != RunStatus.Answered),
            Latest = p.Generations.Where(g => g.LocalImagePath != null).OrderByDescending(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefault()
        }).ToListAsync(ct))
        .Select(p => new ChatDto(p.Id, p.Title, Wire.Utc(p.CreatedAt), Wire.Utc(p.UpdatedAt), p.Count, p.Busy, p.Latest is { } latest ? MessageDto.ImageUrlOf(latest) : null)).ToList();
    /// <summary>Another user's chat is a miss, not a refusal, so ids cannot be probed.</summary>
    public static async Task<ChatDto> OneAsync(AppDbContext db, Viewer me, Guid id, CancellationToken ct) =>
        (await ListAsync(db.Projects.AsNoTracking().OwnedBy(me).Where(p => p.Id == id), ct)).FirstOrDefault() ?? throw new KeyNotFoundException();
}
