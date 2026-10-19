namespace Loomi.Models;

/// <summary>Reply continues the same conversation with words of the person's own, and sends no earlier picture back in with it. Appended, as ints are stored.</summary>
public enum Operation { Generate, Edit, Branch, Reply }
/// <summary>The door a request came through: the web page, the Telegram bot, or a script holding a personal token.</summary>
public enum Channel { Web, Bot, Api }
// Cancelled is appended: the values are stored as integers, so an existing row must keep the number it was written with.
/// <summary>Answered is a finished turn whose outcome was words rather than a picture: nothing failed, and the person has something to read and reply to.</summary>
public enum RunStatus { Queued, OpeningBrowser, OpeningChatGPT, SendingPrompt, WaitingForResponse, GeneratingImage, DownloadingImage, Completed, Failed, Cancelled, Answered }
public enum UserRole { Owner, Member }
/// <summary>Which service turns a prompt into an image. Stored as an int, so new members are only ever appended.</summary>
public enum Provider { ChatGPT, Gemini }
/// <summary>How an account reaches its provider: a driven browser session, or a stored API key.</summary>
public enum AccountKind { Browser, ApiKey }
/// <summary>Why a credit row exists. Grant/Adjust are the owner's doing; Charge/Refund follow a generation.</summary>
public enum CreditKind { Grant, Charge, Refund, Adjust }
/// <summary>What a timed package gives: credits to spend before it ends, free use with an optional daily cap, or a number of free images each day.
/// Stored as an int, so new members are only ever appended.</summary>
public enum PackageKind { Credits, Unlimited, Daily }
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
    /// <summary>What the model said when it answered without a picture. The person's own conversation, returned to them and to nobody else; never logged.</summary>
    public string? ReplyText { get; set; }
    public Operation Operation { get; set; }
    /// <summary>Stamped once, when the request is made. Requests from before it existed read as Web, except those the bot's own records claim.</summary>
    public Channel Channel { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? ErrorMessage { get; set; }
    public List<GenerationInput> Inputs { get; set; } = [];
    /// <summary>The template this was made from, and its title as it read then, so the history still says what was chosen after a template is renamed or deleted.</summary>
    public Guid? TemplateId { get; set; }
    public string? TemplateTitle { get; set; }
    /// <summary>The template's prompt, sent ahead of <see cref="Prompt"/> and never shown: Prompt stays what the person wrote themselves, which is all
    /// that reaches their history, the live updates and the bot.</summary>
    public string? HiddenPrompt { get; set; }
    /// <summary>The package that paid for this request, of any kind. It is how a package's use today is counted, and what the owner's report names.</summary>
    public Guid? PackageId { get; set; }
}
/// <summary>A heading templates are grouped under, in the order the owner chose.</summary>
public class TemplateCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>A ready-made look: a sample people choose by, and the owner's prompt that makes it, which only the owner ever reads.</summary>
public class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What the person is asked to send, in the owner's words: "one clear photo of both faces".</summary>
    public string? Instructions { get; set; }
    public string Prompt { get; set; } = "";
    public Provider Provider { get; set; } = Provider.ChatGPT;
    public int MinInputs { get; set; } = 1;
    public int MaxInputs { get; set; } = 1;
    /// <summary>Sends the sample along as a style reference. Off by default: for a portrait it can carry the sample's face into the result.</summary>
    public bool SendSample { get; set; }
    public string? SamplePath { get; set; }
    /// <summary>A sample being made from the prompt; adopted as <see cref="SamplePath"/> once it completes.</summary>
    public Guid? SampleGenerationId { get; set; }
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>Work that was asked for in a Telegram chat, and the message there that says where it stands. It is how the result finds its way back to
/// that chat wherever the work lives: a template's or an edit's project is not the bot's own.</summary>
public class BotRequest
{
    public Guid GenerationId { get; set; }
    public long ChatId { get; set; }
    public int? StatusMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>A workspace setting the owner changes from the app itself. A secret is sealed before it is stored here.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
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
    /// <summary>Set on a charge or refund made against a Credits package. Such a row is that package's money, never the regular balance's.</summary>
    public Guid? PackageId { get; set; }
}
/// <summary>A timed package the owner hands one member, paid for outside the app. It counts only while StartsAt &lt;= now &lt; EndsAt; what a Credits
/// package has left when it ends is gone, and a day's free images do not carry over. Kept after it ends, so the history still says what paid.</summary>
public class CreditPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public PackageKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Credits for a Credits package; the daily cap of an Unlimited one, zero meaning none; the free images per day of a Daily one.</summary>
    public int Amount { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ByUserId { get; set; }
    /// <summary>The owner's own words about it, such as what was paid. Only the owner ever reads it.</summary>
    public string? Note { get; set; }
    /// <summary>Set when the owner stopped it by hand, so the end notice, meant for a package that simply ran out, is not sent for it.</summary>
    public DateTime? EndedEarlyAt { get; set; }
    /// <summary>Stamped once the ending-soon reminder was handled, even with no chat to send it to, so it is never tried again.</summary>
    public DateTime? ReminderSentAt { get; set; }
    public DateTime? EndNoticeSentAt { get; set; }
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
    /// <summary>The project this chat is talking in: its next message continues that conversation. Null starts a new project, which is what /new asks for.</summary>
    public Guid? ProjectId { get; set; }
}
/// <summary>A short-lived code a user quotes to the bot to prove the chat is theirs.</summary>
public class LinkCode
{
    public string Code { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
}
