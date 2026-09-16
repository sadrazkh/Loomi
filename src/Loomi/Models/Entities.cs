namespace Loomi.Models;

public enum Operation { Generate, Edit, Branch }
// Cancelled is appended: the values are stored as integers, so an existing row must keep the number it was written with.
public enum RunStatus { Queued, OpeningBrowser, OpeningChatGPT, SendingPrompt, WaitingForResponse, GeneratingImage, DownloadingImage, Completed, Failed, Cancelled }
public enum UserRole { Owner, Member }
/// <summary>Which service turns a prompt into an image. Stored as an int, so new members are only ever appended.</summary>
public enum Provider { ChatGPT, Gemini }
/// <summary>How an account reaches its provider: a driven browser session, or a stored API key.</summary>
public enum AccountKind { Browser, ApiKey }
/// <summary>Why a credit row exists. Grant/Adjust are the owner's doing; Charge/Refund follow a generation.</summary>
public enum CreditKind { Grant, Charge, Refund, Adjust }
/// <summary>A person who signs in to Loomi. Distinct from the provider accounts their work runs on.</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    /// <summary>Lowercased Username, so lookups and uniqueness do not depend on how it was typed.</summary>
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Member;
    /// <summary>Optional per-day ceiling on generations. Zero means no daily limit; credits are the real currency.</summary>
    public int DailyQuota { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>One connected account of one provider. A browser account has a profile; an API account has a secret.</summary>
public class ProviderAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public AccountKind Kind { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Folder name under Storage/profiles for a browser account; null for an API account. Kept in the row so renaming the label cannot orphan a session.</summary>
    public string? ProfileDirectory { get; set; }
    /// <summary>Protected API key for an API account; null for a browser account. Never returned or logged.</summary>
    public string? Secret { get; set; }
    /// <summary>Generations this account may run per day, to stay under the provider's own limit.</summary>
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
    /// <summary>Copied from the project so quota and credits can be counted without joining.</summary>
    public Guid UserId { get; set; }
    /// <summary>The provider account this ran on. Null until the dispatcher claims it.</summary>
    public Guid? AccountId { get; set; }
    /// <summary>Which provider the request is for. The dispatcher only hands it to an account of the same provider.</summary>
    public Provider Provider { get; set; }
    public string Prompt { get; set; } = "";
    /// <summary>Credits charged when queued, so a refund on failure returns the exact amount taken.</summary>
    public int CreditCost { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentGenerationId { get; set; }
    public string? LocalImagePath { get; set; }
    public string? ConversationUrl { get; set; }
    public Operation Operation { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? ErrorMessage { get; set; }
    public List<GenerationInput> Inputs { get; set; } = [];
}
/// <summary>A file a user uploaded to feed into a generation, held until it is used or swept.</summary>
public class Upload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Path { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>One reference image for a generation: either an upload or an earlier generation. Exactly one is set.</summary>
public class GenerationInput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GenerationId { get; set; }
    public int Order { get; set; }
    public Guid? UploadId { get; set; }
    public Guid? SourceGenerationId { get; set; }
}
/// <summary>One movement of a user's credit balance. The balance is the sum of these, never a stored column.</summary>
public class CreditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int Amount { get; set; }
    public CreditKind Kind { get; set; }
    public Guid? GenerationId { get; set; }
    public Guid? ByUserId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>What one generation costs. Operation null is the provider's default; a specific operation overrides it.</summary>
public class PricingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public Operation? Operation { get; set; }
    public int Cost { get; set; }
}
/// <summary>A personal access token for the API. Only the hash is kept; the token is shown once at creation.</summary>
public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
/// <summary>A Telegram chat bound to a Loomi user. One chat, one user, both ways.</summary>
public class TelegramLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public long ChatId { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>A short-lived code a user quotes to the bot to prove the chat is theirs.</summary>
public class LinkCode
{
    public string Code { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
}
