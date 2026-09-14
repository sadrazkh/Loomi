namespace Loomi.Models;

public enum Operation { Generate, Edit, Branch }
public enum RunStatus { Queued, OpeningBrowser, OpeningChatGPT, SendingPrompt, WaitingForResponse, GeneratingImage, DownloadingImage, Completed, Failed }
public class ImageProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Ready";
    public string? ConversationUrl { get; set; }
    public string BrowserProfileReference { get; set; } = "default";
    public List<Generation> Generations { get; set; } = [];
}
public class Generation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public ImageProject Project { get; set; } = null!;
    public string Prompt { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentGenerationId { get; set; }
    public string? LocalImagePath { get; set; }
    public string? ConversationUrl { get; set; }
    public Operation Operation { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? ErrorMessage { get; set; }
}
