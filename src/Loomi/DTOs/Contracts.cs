using System.ComponentModel.DataAnnotations;
using Loomi.Models;
namespace Loomi.DTOs;
public record CreateProject([Required, StringLength(160, MinimumLength = 1)] string Title);
/// <summary>One reference image: an upload of the caller's or a finished generation. Exactly one id is set.</summary>
public record InputRef(Guid? UploadId, Guid? GenerationId);
public record PromptRequest([Required, StringLength(12000, MinimumLength = 1)] string Prompt, List<InputRef>? Inputs = null);
public record InputDto(Guid? UploadId, Guid? GenerationId, string Url)
{
    public static InputDto From(GenerationInput i) => new(i.UploadId, i.SourceGenerationId, i.UploadId is { } upload ? $"/api/uploads/{upload}" : $"/api/generations/{i.SourceGenerationId}/image");
}
public record LoginRequest([Required, StringLength(64, MinimumLength = 1)] string Username, [Required, StringLength(512, MinimumLength = 1)] string Password);
public record GenerationDto(Guid Id, Guid ProjectId, string Prompt, DateTime CreatedAt, Guid? ParentGenerationId, string? ImageUrl, string? ConversationUrl, Operation Operation, RunStatus Status, string? ErrorMessage, List<InputDto> Inputs)
{
    public static GenerationDto From(Generation g) => new(g.Id, g.ProjectId, g.Prompt, g.CreatedAt, g.ParentGenerationId, g.LocalImagePath == null ? null : $"/api/generations/{g.Id}/image", g.ConversationUrl, g.Operation, g.Status, g.ErrorMessage, g.Inputs.OrderBy(i => i.Order).Select(InputDto.From).ToList());
}
