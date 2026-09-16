namespace Loomi.Models;

public enum Operation { Generate, Edit, Branch }
// Cancelled is appended: the values are stored as integers, so an existing row must keep the number it was written with.
public enum RunStatus { Queued, OpeningBrowser, OpeningChatGPT, SendingPrompt, WaitingForResponse, GeneratingImage, DownloadingImage, Completed, Failed, Cancelled }
public enum UserRole { Owner, Member }
/// <summary>A person who signs in to Loomi. Distinct from the ChatGPT accounts their work runs on.</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    /// <summary>Lowercased Username, so lookups and uniqueness do not depend on how it was typed.</summary>
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Member;
    /// <summary>Generations this user may start per day. Zero blocks them without deleting the account.</summary>
    public int DailyQuota { get; set; } = 10;
    public bool IsDisabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>One signed-in ChatGPT account the owner has connected, with its own browser profile.</summary>
public class BrowserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    /// <summary>Folder name under Storage/profiles. Kept in the row so renaming the label cannot orphan a session.</summary>
    public string ProfileDirectory { get; set; } = "";
    /// <summary>Generations this account may run per day, to stay under the site's own limit.</summary>
    public int DailyCap { get; set; } = 25;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class ImageProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
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
    /// <summary>Copied from the project so quota can be counted without joining.</summary>
    public Guid UserId { get; set; }
    /// <summary>The ChatGPT account this ran on. Null until the dispatcher claims it.</summary>
    public Guid? AccountId { get; set; }
    public string Prompt { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentGenerationId { get; set; }
    public string? LocalImagePath { get; set; }
    public string? ConversationUrl { get; set; }
    public Operation Operation { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? ErrorMessage { get; set; }
}
