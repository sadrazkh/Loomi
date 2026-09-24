using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
namespace Loomi.Telegram;
public class TelegramOptions
{
    /// <summary>No token means the bot is not part of this deployment: nothing polls and nothing is logged about it.</summary>
    public string? BotToken { get; set; }
    /// <summary>Only so Settings can tell a user which chat to open; linking works without it.</summary>
    public string? BotUsername { get; set; }
    /// <summary>Long-poll hold, in seconds. Telegram allows up to 50.</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>How long after the last photo of an album to wait before treating it as one request.</summary>
    public int MediaGroupMs { get; set; } = 1500;
}
/// <summary>The whole of the Bot API this server uses. Above it everything is testable without a socket; below it nothing knows about Loomi.</summary>
public interface ITelegramClient
{
    Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct);
    Task SendTextAsync(long chat, string text, CancellationToken ct);
    Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct);
    /// <summary>The file's bytes, or null when it is over the cap or the Bot API will not hand it over.</summary>
    Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct);
    /// <summary>The bot's username for a token, or null when Telegram does not recognise it. Asked before a token is kept.</summary>
    Task<string?> WhoAmIAsync(string token, CancellationToken ct);
}
public class TelegramClient(BotSettings settings, IOptions<TelegramOptions> options) : ITelegramClient
{
    private (string Token, ITelegramBotClient Client)? built;
    private readonly int poll = Math.Clamp(options.Value.PollSeconds, 1, 50);
    /// <summary>Built for whatever token is current, and again when the owner changes it; nothing is constructed while there is none.</summary>
    private ITelegramBotClient Bot
    {
        get
        {
            var token = settings.Token ?? throw new InvalidOperationException("TelegramNotConfigured");
            if (built is { } current && current.Token == token) return current.Client;
            var client = new TelegramBotClient(token);
            built = (token, client);
            return client;
        }
    }
    // A malformed token throws in the constructor and an unknown one on the call; either way the answer is "not a bot".
    public async Task<string?> WhoAmIAsync(string token, CancellationToken ct)
    {
        try { return (await new TelegramBotClient(token).GetMeAsync(ct)).Username; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
    public async Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) =>
        await Bot.GetUpdatesAsync(offset, timeout: poll, allowedUpdates: [UpdateType.Message], cancellationToken: ct);
    public Task SendTextAsync(long chat, string text, CancellationToken ct) => Bot.SendTextMessageAsync(chat, text, cancellationToken: ct);
    public async Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct)
    {
        using var stream = new MemoryStream(photo);
        await Bot.SendPhotoAsync(chat, InputFile.FromStream(stream, name), caption: caption, cancellationToken: ct);
    }
    public async Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct)
    {
        var file = await Bot.GetFileAsync(fileId, ct);
        if (file.FilePath == null || file.FileSize > maxBytes) return null;
        using var buffer = new MemoryStream();
        await Bot.DownloadFileAsync(file.FilePath, buffer, ct);
        // Telegram's own size is advisory; the bytes that actually arrived are what counts.
        return buffer.Length > maxBytes ? null : buffer.ToArray();
    }
}
