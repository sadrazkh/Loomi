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
}
public class TelegramClient(IOptions<TelegramOptions> options) : ITelegramClient
{
    // Built on first use, so a deployment without a token never constructs one and the worker's own check is the only gate.
    private readonly Lazy<ITelegramBotClient> bot = new(() => new TelegramBotClient(options.Value.BotToken ?? ""));
    private readonly int poll = Math.Clamp(options.Value.PollSeconds, 1, 50);
    public async Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) =>
        await bot.Value.GetUpdatesAsync(offset, timeout: poll, allowedUpdates: [UpdateType.Message], cancellationToken: ct);
    public Task SendTextAsync(long chat, string text, CancellationToken ct) => bot.Value.SendTextMessageAsync(chat, text, cancellationToken: ct);
    public async Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct)
    {
        using var stream = new MemoryStream(photo);
        await bot.Value.SendPhotoAsync(chat, InputFile.FromStream(stream, name), caption: caption, cancellationToken: ct);
    }
    public async Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct)
    {
        var file = await bot.Value.GetFileAsync(fileId, ct);
        if (file.FilePath == null || file.FileSize > maxBytes) return null;
        using var buffer = new MemoryStream();
        await bot.Value.DownloadFileAsync(file.FilePath, buffer, ct);
        // Telegram's own size is advisory; the bytes that actually arrived are what counts.
        return buffer.Length > maxBytes ? null : buffer.ToArray();
    }
}
