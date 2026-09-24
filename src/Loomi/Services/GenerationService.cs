using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Services;
public class GenerationService(AppDbContext db, IOptions<BrowserOptions> browser)
{
    /// <summary>A title for a project that starts from a message rather than a name: the message's opening words.</summary>
    public static string TitleFrom(string prompt)
    {
        var words = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.Length == 0 ? "…" : words.Length <= 60 ? words : words[..60].TrimEnd() + "…";
    }
    /// <summary>One message into a project. A project is one conversation with the AI: while that conversation can be reached, the message continues
    /// it on the account that holds it, and nothing already in it is sent again. When it cannot be reached, because its account was removed or
    /// switched off, the project's latest picture opens a fresh conversation, which the project then carries on in. An empty project starts one.</summary>
    public async Task<Generation> SendAsync(Viewer viewer, Guid projectId, string prompt, IReadOnlyList<InputRef>? inputs, Provider? provider, CancellationToken ct)
    {
        if (!await db.Projects.OwnedBy(viewer).AnyAsync(p => p.Id == projectId, ct)) throw new KeyNotFoundException();
        // A conversation takes one message at a time, as it does on the site itself: the next can only follow the answer to this one.
        if (await db.Generations.Where(g => g.ProjectId == projectId).Unfinished().AnyAsync(ct)) throw new InvalidOperationException("ProjectBusy");
        var last = await db.Generations.AsNoTracking().Where(g => g.ProjectId == projectId && (g.Status == RunStatus.Completed || g.Status == RunStatus.Answered) && g.ConversationUrl != null)
            .OrderByDescending(g => g.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is { AccountId: { } holder } && await db.Accounts.AnyAsync(a => a.Id == holder && a.IsEnabled, ct))
            return await SubmitAsync(viewer, projectId, prompt, last.Provider, Operation.Reply, last.Id, inputs, ct);
        var picture = await db.Generations.AsNoTracking().Where(g => g.ProjectId == projectId && g.Status == RunStatus.Completed && g.LocalImagePath != null)
            .OrderByDescending(g => g.CreatedAt).FirstOrDefaultAsync(ct);
        if (picture != null) return await SubmitAsync(viewer, projectId, prompt, picture.Provider, Operation.Branch, picture.Id, inputs, ct);
        return await SubmitAsync(viewer, projectId, prompt, provider ?? Provider.ChatGPT, Operation.Generate, null, inputs, ct);
    }
    /// <summary>A new project that begins with a message, named by the message's opening words unless a title is given. Refused work leaves no empty
    /// project behind.</summary>
    public async Task<Generation> StartAsync(Viewer viewer, string? title, string prompt, IReadOnlyList<InputRef>? inputs, Provider? provider, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(title) ? TitleFrom(prompt) : title.Trim();
        var project = new ImageProject { UserId = viewer.Id, Title = name.Length <= 160 ? name : name[..160] };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        try { return await SendAsync(viewer, project.Id, prompt, inputs, provider, ct); }
        catch
        {
            db.ChangeTracker.Clear();
            await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }
    /// <summary>The picture a conversation made last, whose conversation can still be reached: commenting on it is simply the next message there.</summary>
    private async Task<bool> LatestInReachAsync(Viewer viewer, Guid picture, CancellationToken ct)
    {
        var parent = await db.Generations.AsNoTracking().OwnedBy(viewer).FirstOrDefaultAsync(g => g.Id == picture, ct);
        if (parent is not { Status: RunStatus.Completed, ConversationUrl: { } conversation, AccountId: { } holder } || !await db.Accounts.AnyAsync(a => a.Id == holder && a.IsEnabled, ct)) return false;
        return !await db.Generations.AnyAsync(g => g.ProjectId == parent.ProjectId && g.ConversationUrl == conversation && g.Status == RunStatus.Completed && g.CreatedAt > parent.CreatedAt, ct);
    }
    /// <summary>With a template, the prompt is only what the person added and may be empty: the template's own prompt travels beside it, hidden.</summary>
    public async Task<Generation> SubmitAsync(Viewer viewer, Guid projectId, string prompt, Provider provider, Operation operation, Guid? parentId, IReadOnlyList<InputRef>? inputs, CancellationToken ct, TemplateUse? template = null)
    {
        if ((template is null && string.IsNullOrWhiteSpace(prompt)) || prompt.Length > 12000) throw new InvalidOperationException("InvalidPrompt");
        // Editing the picture a conversation just made is the next message in that conversation: the picture is already there, so it is not sent again.
        // An older picture, or one whose conversation is out of reach, still goes along so the AI knows which one is meant.
        if (operation == Operation.Edit && parentId is { } edited && await LatestInReachAsync(viewer, edited, ct)) operation = Operation.Reply;
        var refs = inputs ?? [];
        if (refs.Count > browser.Value.MaxInputs) throw new InvalidOperationException("TooManyInputs");
        // Serializable SQLite transaction keeps queue admission and deletion consistent.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw new KeyNotFoundException();
        if (await db.Generations.AsQueryable().Unfinished().CountAsync(ct) >= 25) throw new InvalidOperationException("QueueFull");
        // Refused here rather than silently parked in the queue: a limit is only actionable while the person who hit it is still looking at it.
        if (await db.RemainingAsync(project.UserId, ct) == 0) throw new InvalidOperationException("QuotaExceeded");
        // Work for a provider no account serves would wait for ever, so it is refused while the person can still pick another. A workspace with no
        // accounts at all is a different thing: nothing is connected yet, the queue holds the work, and /api/auth/status is what explains the wait.
        if (await db.Accounts.AnyAsync(a => a.IsEnabled, ct) && !await db.Accounts.AnyAsync(a => a.IsEnabled && a.Provider == provider, ct)) throw new InvalidOperationException("NoAccount");
        Guid? pinned = null;
        if (parentId != null)
        {
            var parent = await db.Generations.OwnedBy(viewer).FirstOrDefaultAsync(x => x.Id == parentId, ct);
            if (operation == Operation.Reply)
            {
                // A reply goes into the thread the turn left behind, on the account that holds it; without both there is nothing to continue.
                if (parent == null || parent.ProjectId != projectId || parent.Status is not (RunStatus.Completed or RunStatus.Answered) || parent.ConversationUrl == null
                    || parent.AccountId is not { } holder || !await db.Accounts.AnyAsync(a => a.Id == holder && a.IsEnabled, ct)) throw new InvalidOperationException("NoConversation");
                pinned = holder;
            }
            else
            {
                if (parent == null || parent.ProjectId != projectId || parent.Status != RunStatus.Completed || parent.LocalImagePath == null) throw new InvalidOperationException("InvalidParent");
                // An edit continues the parent's conversation too, so it waits for the account that holds it rather than opening a thread it cannot see.
                if (operation == Operation.Edit && parent.AccountId is { } holder && parent.ConversationUrl != null && await db.Accounts.AnyAsync(a => a.Id == holder && a.IsEnabled, ct)) pinned = holder;
            }
        }
        // A reply sends what the person attached and nothing else: the picture before it is already in the conversation.
        var ordered = await InputsAsync(viewer, operation == Operation.Reply ? null : parentId, refs, ct);
        if (ordered.Count > browser.Value.MaxInputs) throw new InvalidOperationException("TooManyInputs");
        // One earlier image from the same project is a lineage, so the tree still shows it; several are a set with no single parent.
        if (parentId == null && ordered is [{ SourceGenerationId: { } only }] && await db.Generations.AnyAsync(x => x.Id == only && x.ProjectId == projectId, ct)) parentId = only;
        // The project's user, not the actor: an owner helping out must not spend their own quota or take the work over.
        var generation = new Generation { ProjectId = projectId, UserId = project.UserId, Provider = provider, Prompt = prompt.Trim(), Operation = operation, ParentGenerationId = parentId, Inputs = ordered, AccountId = pinned,
                                          TemplateId = template?.Id, TemplateTitle = template?.Title, HiddenPrompt = template?.HiddenPrompt };
        // The owner pays for the provider accounts themselves, so charging them their own credits would be bookkeeping with nothing behind it,
        // and work whose user no longer exists has no balance to spend — the same reasoning the daily quota uses.
        if (await db.Users.AsNoTracking().Where(u => u.Id == project.UserId).Select(u => (UserRole?)u.Role).FirstOrDefaultAsync(ct) == UserRole.Member)
        {
            var price = await db.PriceAsync(provider, operation, ct);
            if (price > 0)
            {
                if (await db.BalanceAsync(project.UserId, ct) < price) throw new InvalidOperationException("InsufficientCredits");
                generation.CreditCost = price;
                db.CreditEntries.Add(new CreditEntry { UserId = project.UserId, Amount = -price, Kind = CreditKind.Charge, GenerationId = generation.Id });
            }
        }
        db.Generations.Add(generation);
        project.UpdatedAt = DateTime.UtcNow; project.Status = "Queued";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return generation;
    }
    /// <summary>The parent leads, then the references in the order given, each named once. An id that is not the caller's is a miss like any other; one that is theirs but unfinished is refused.</summary>
    private async Task<List<GenerationInput>> InputsAsync(Viewer viewer, Guid? parentId, IReadOnlyList<InputRef> refs, CancellationToken ct)
    {
        var ordered = new List<GenerationInput>();
        if (parentId != null) ordered.Add(new GenerationInput { SourceGenerationId = parentId });
        foreach (var r in refs)
        {
            if ((r.UploadId is null) == (r.GenerationId is null)) throw new InvalidOperationException("InvalidInput");
            if (r.UploadId is { } upload)
            {
                if (ordered.Any(i => i.UploadId == upload)) continue;
                if (!await db.Uploads.OwnedBy(viewer).AnyAsync(u => u.Id == upload, ct)) throw new KeyNotFoundException();
                ordered.Add(new GenerationInput { UploadId = upload });
            }
            else
            {
                if (ordered.Any(i => i.SourceGenerationId == r.GenerationId)) continue;
                var source = await db.Generations.AsNoTracking().OwnedBy(viewer).Where(x => x.Id == r.GenerationId).Select(x => new { x.Status, x.LocalImagePath }).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
                if (source.Status != RunStatus.Completed || source.LocalImagePath == null) throw new InvalidOperationException("InvalidInput");
                ordered.Add(new GenerationInput { SourceGenerationId = r.GenerationId });
            }
        }
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return ordered;
    }
}
