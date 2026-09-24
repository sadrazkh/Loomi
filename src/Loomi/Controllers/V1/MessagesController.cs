using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers.V1;
/// <summary>Messages, each a job that finishes somewhere between thirty seconds and several minutes after it was sent. A caller can ask, wait on
/// one with a long poll, or follow it as a stream of events; all three read the same row and say the same thing.</summary>
[Route("api/v1/messages")]
public class MessagesController(AppDbContext db, IImageStorage storage, QueueEstimates queue, GenerationEvents events, IOptions<JsonOptions> json) : ApiV1Controller
{
    /// <summary>How often a waiting caller looks at the row whatever it has heard. Cancelling queued work and a queue position moving announce
    /// nothing, and an announcement can fall between two reads.</summary>
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);
    /// <summary>Often enough that a proxy between here and the caller never sees an idle stream.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    /// <summary>Another user's message is a miss, not a refusal, so ids cannot be probed.</summary>
    private Task<Generation?> FindAsync(Guid id, CancellationToken ct) => db.Generations.AsNoTracking().OwnedBy(Me).FirstOrDefaultAsync(g => g.Id == id, ct);
    private async Task<Generation> MineAsync(Guid id, CancellationToken ct) => await FindAsync(id, ct) ?? throw new KeyNotFoundException();

    [HttpGet("{id:guid}"), ProducesResponseType<MessageDto>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(MessageDto.From(await MineAsync(id, ct), queue));

    /// <summary>Answers the moment the message is finished, or after timeout seconds with done false and where it stands, whichever comes first.
    /// The clamp keeps one call from holding a connection for longer than a proxy would.</summary>
    [HttpGet("{id:guid}/wait"), ProducesResponseType<WaitResult>(200)]
    public async Task<IActionResult> Wait(Guid id, int timeout = 60, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeout, 1, 120));
        using var moved = new Movement(events, id);
        while (true)
        {
            var g = await MineAsync(id, ct);
            var left = until - DateTime.UtcNow;
            if (MessageDto.IsFinal(g.Status) || left <= TimeSpan.Zero) return Ok(new WaitResult(MessageDto.IsFinal(g.Status), MessageDto.From(g, queue)));
            await moved.WaitAsync(left < Recheck ? left : Recheck, ct);
        }
    }

    /// <summary>Server-sent events: the message as it stands, again every time it changes, a comment line when nothing has been said for a while,
    /// and the end of the stream after its final state.</summary>
    [HttpGet("{id:guid}/events"), ProducesResponseType(typeof(string), 200, "text/event-stream")]
    public async Task Events(Guid id, CancellationToken ct)
    {
        using var moved = new Movement(events, id);
        // Read before anything is written, so a miss is still an ordinary 404.
        var g = await MineAsync(id, ct);
        Response.ContentType = "text/event-stream";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        string? sent = null;
        var spoke = DateTime.UtcNow;
        try
        {
            while (true)
            {
                var current = JsonSerializer.Serialize(MessageDto.From(g, queue), json.Value.JsonSerializerOptions);
                if (current != sent) { await WriteAsync($"event: status\ndata: {current}\n\n", ct); sent = current; spoke = DateTime.UtcNow; }
                if (MessageDto.IsFinal(g.Status)) return;
                if (DateTime.UtcNow - spoke >= Heartbeat) { await WriteAsync(": keep-alive\n\n", ct); spoke = DateTime.UtcNow; }
                var beat = Heartbeat - (DateTime.UtcNow - spoke);
                await moved.WaitAsync(beat < Recheck ? beat : Recheck, ct);
                // Gone mid-stream means its chat was deleted: nothing more will ever be said about it.
                if (await FindAsync(id, ct) is not { } next) return;
                g = next;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* the caller went away */ }
    }
    private async Task WriteAsync(string text, CancellationToken ct) { await Response.WriteAsync(text, ct); await Response.Body.FlushAsync(ct); }

    /// <summary>The same checks as GenerationsController's image: the caller's own message, naming a file that is still there.</summary>
    [HttpGet("{id:guid}/image"), ProducesResponseType(typeof(Stream), 200, "image/png", "image/jpeg", "image/webp")]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var g = await MineAsync(id, ct);
        var path = g.LocalImagePath is null ? null : storage.Resolve(g.LocalImagePath);
        if (path is null || !System.IO.File.Exists(path)) return NotFound(new ErrorDto("NoImage"));
        return PhysicalFile(path, ImageStorage.ContentTypeFor(path), enableRangeProcessing: true);
    }

    /// <summary>Queued work stops at once; running work is interrupted and records its own end a moment later, which wait reports. Cancelling a
    /// finished message changes nothing and is not an error: the answer says how it ended.</summary>
    [HttpPost("{id:guid}/cancel"), ProducesResponseType<MessageDto>(200)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var g = await db.Generations.Include(x => x.Project).OwnedBy(Me).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (!MessageDto.IsFinal(g.Status)) g = await Cancellation.CancelAsync(db, g, ct);
        return Ok(MessageDto.From(g, queue));
    }
}
