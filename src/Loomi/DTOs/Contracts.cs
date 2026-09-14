using System.ComponentModel.DataAnnotations;
using Loomi.Models;
namespace Loomi.DTOs;
public record CreateProject([Required, StringLength(160, MinimumLength = 1)] string Title);
public record PromptRequest([Required, StringLength(12000, MinimumLength = 1)] string Prompt);
public record LoginRequest([Required, StringLength(64, MinimumLength = 1)] string Username, [Required, StringLength(512, MinimumLength = 1)] string Password);
public record GenerationDto(Guid Id, Guid ProjectId, string Prompt, DateTime CreatedAt, Guid? ParentGenerationId, string? ImageUrl, string? ConversationUrl, Operation Operation, RunStatus Status, string? ErrorMessage)
{
    public static GenerationDto From(Generation g) => new(g.Id, g.ProjectId, g.Prompt, g.CreatedAt, g.ParentGenerationId, g.LocalImagePath == null ? null : $"/api/generations/{g.Id}/image", g.ConversationUrl, g.Operation, g.Status, g.ErrorMessage);
}
