using System.Text.Json.Serialization;
using Loomi.Models;
using Loomi.Services;
namespace Loomi.Controllers.V1;
// Every member is nullable, as ChangePasswordRequest explains: a non-nullable one makes [ApiController] answer bad input with a ProblemDetails
// document instead of a stable error code.
/// <summary>One picture sent with a message: an upload made earlier through POST /api/uploads, or the bytes themselves. Exactly one is set.</summary>
public record ImageInput(Guid? UploadId, string? Base64);
public record NewChatRequest(string? Title, string? Prompt, List<ImageInput>? Images, string? Provider);
/// <summary>Provider only matters for a chat's first message; after that the conversation stays with the provider it began with.</summary>
public record NewMessageRequest(string? Prompt, List<ImageInput>? Images, string? Provider);
public record UseTemplateRequest(string? Note, List<ImageInput>? Images);

/// <summary>Ahead is null until the dispatcher has placed the message; EtaSeconds is null while no account can take it at all.</summary>
public record QueueSpot(int? Ahead, int? EtaSeconds);
/// <summary>One message of a chat, which is a job that finishes later. Prompt is only what the caller wrote: a template's own prompt travels beside
/// it, hidden, and is never part of anything sent back.</summary>
public record MessageDto(Guid Id, Guid ChatId, string Status, string Stage, string Prompt, string? Text, string? ImageUrl, string? Error, QueueSpot? Queue, DateTime CreatedAt, string? TemplateTitle)
{
    public static bool IsFinal(RunStatus status) => status is RunStatus.Completed or RunStatus.Answered or RunStatus.Failed or RunStatus.Cancelled;
    /// <summary>Six coarse states a client can branch on; every step a run passes through on the site is "running", and Stage names it.</summary>
    public static string StatusOf(RunStatus status) => status switch
    {
        RunStatus.Queued => "queued", RunStatus.Completed => "completed", RunStatus.Answered => "answered",
        RunStatus.Failed => "failed", RunStatus.Cancelled => "cancelled", _ => "running"
    };
    public static string ImageUrlOf(Guid id) => $"/api/v1/messages/{id}/image";
    public static MessageDto From(Generation g, QueueEstimates queue)
    {
        QueueSpot? spot = null;
        if (g.Status == RunStatus.Queued)
        {
            // The estimates are the work's own user's, which is not the caller when the owner looks at a member's message.
            var position = queue.For(g.UserId).FirstOrDefault(p => p.Id == g.Id);
            spot = position.Id == g.Id ? new(position.Ahead, position.EtaSeconds) : new(null, null);
        }
        return new(g.Id, g.ProjectId, StatusOf(g.Status), g.Status.ToString(), g.Prompt, g.ReplyText, g.LocalImagePath is null ? null : ImageUrlOf(g.Id),
                   g.ErrorMessage, spot, Wire.Utc(g.CreatedAt), g.TemplateTitle);
    }
}
/// <summary>A chat is one Loomi project, which is one conversation with the AI. Busy means a message in it is unfinished, so the next must wait.</summary>
public record ChatDto(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt, int MessageCount, bool Busy, string? LatestImageUrl);
/// <summary>Message is left out, not sent as null, when a chat is created without one.</summary>
public record ChatStarted(ChatDto Chat, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MessageDto? Message);
public record MessageSent(MessageDto Message);
public record ChatDetail(ChatDto Chat, List<MessageDto> Messages);
/// <summary>NextBefore is what to pass as before= for the next page, or null on the last one.</summary>
public record ChatPage(List<ChatDto> Chats, DateTime? NextBefore);
public record WaitResult(bool Done, MessageDto Message);
/// <summary>What choosing a template takes: its sample, what it asks for and, for a member, its price. Never its prompt.</summary>
public record TemplateDto(Guid Id, string Title, string Category, string? Instructions, int MinImages, int MaxImages, string? SampleUrl, int? Price);
public record TemplateList(List<TemplateDto> Templates);
public record Limits(int MaxImages, long MaxImageBytes, int MaxPromptLength);
/// <summary>Credits is null for the owner, who is never charged; DailyQuota and DailyRemaining are null when there is no daily ceiling. Packages are the
/// caller's timed packages: running ones first, then those waiting to start, then those that ended in the last thirty days.</summary>
public record MeDto(Guid Id, string Username, string Role, int? Credits, int? DailyQuota, int DailyUsed, int? DailyRemaining, Limits Limits, IReadOnlyList<PackageDto> Packages);
public record ErrorDto(string Error);

internal static class Wire
{
    /// <summary>SQLite hands times back without a kind. They were written as UTC, and a public contract says so with a Z.</summary>
    public static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);
}
