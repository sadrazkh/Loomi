using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers.V1;
/// <summary>Templates as a member chooses them: the same ones /api/templates lists, active and with a sample, and never their prompt. Using one starts
/// a chat of its own, named after it.</summary>
[Route("api/v1/templates")]
public class TemplatesController(AppDbContext db, TemplateService templates, IImageStorage storage, QueueEstimates queue, IOptions<BrowserOptions> browser) : ApiV1Controller
{
    [HttpGet, ProducesResponseType<TemplateList>(200)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await templates.AdoptSamplesAsync(ct);
        var categories = await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        var visible = await db.Templates.AsNoTracking().Where(t => t.IsActive && t.SamplePath != null).OrderBy(t => t.Order).ThenBy(t => t.Title).ToListAsync(ct);
        // A member sees what one use costs them; the owner is not charged, so a number would suggest a price that does not apply.
        var prices = new Dictionary<Provider, int?>();
        foreach (var provider in visible.Select(t => t.Provider).Distinct())
            prices[provider] = Me.IsOwner ? null : await db.PriceAsync(provider, Operation.Generate, ct);
        return Ok(new TemplateList(categories.SelectMany(c => visible.Where(t => t.CategoryId == c.Id).Select(t =>
            new TemplateDto(t.Id, t.Title, c.Name, t.Instructions, t.MinInputs, t.MaxInputs, $"/api/templates/{t.Id}/sample?v={t.UpdatedAt.Ticks}", prices[t.Provider]))).ToList()));
    }
    /// <summary>The note is only what the caller adds; the template's prompt goes with it unseen.</summary>
    [HttpPost("{id:guid}/use"), RequestSizeLimit(Pictures.MaxBodyBytes), ProducesResponseType<ChatStarted>(202), ProducesResponseType<ErrorDto>(409)]
    public async Task<IActionResult> Use(Guid id, UseTemplateRequest? request, CancellationToken ct)
    {
        request ??= new(null, null);
        if (request.Note?.Trim().Length > 1000) return Invalid("InvalidPrompt");
        return await Pictures.WithAsync(db, storage, Me.Id, request.Images, browser.Value.MaxInputs, async refs =>
        {
            var g = await templates.UseAsync(Me, id, request.Note, refs, ct);
            return Accepted($"/api/v1/messages/{g.Id}", new ChatStarted(await Chats.OneAsync(db, Me, g.ProjectId, ct), MessageDto.From(g, queue)));
        }, ct);
    }
}
