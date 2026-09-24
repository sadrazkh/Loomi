using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
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
/// <summary>An inline button: the label a person reads and the data the bot gets back when it is pressed (Telegram allows 64 bytes).</summary>
public sealed record BotButton(string Text, string Data);
/// <summary>A command in the menu Telegram shows when a person types "/", described in both languages.</summary>
public sealed record BotCommandText(string Command, string En, string Fa);
/// <summary>The whole of the Bot API this server uses. Above it everything is testable without a socket; below it nothing knows about Loomi.</summary>
public interface ITelegramClient
{
    Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct);
    /// <summary>Returns the message's id, so a status line can be edited or removed later.</summary>
    Task<int> SendTextAsync(long chat, string text, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null);
    Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null);
    /// <summary>A file rather than a photo: Telegram recompresses photos, and a document arrives exactly as it was made.</summary>
    Task SendDocumentAsync(long chat, byte[] file, string name, string? caption, CancellationToken ct);
    Task EditTextAsync(long chat, int message, string text, CancellationToken ct);
    Task DeleteAsync(long chat, int message, CancellationToken ct);
    /// <summary>Stops the spinner Telegram shows on a pressed button.</summary>
    Task AnswerAsync(string callback, CancellationToken ct);
    Task SetCommandsAsync(IReadOnlyList<BotCommandText> commands, CancellationToken ct);
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
        await Bot.GetUpdatesAsync(offset, timeout: poll, allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: ct);
    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<IReadOnlyList<BotButton>>? buttons) =>
        buttons is null or { Count: 0 } ? null : new(buttons.Select(row => row.Select(b => InlineKeyboardButton.WithCallbackData(b.Text, b.Data))));
    public async Task<int> SendTextAsync(long chat, string text, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null) =>
        (await Bot.SendTextMessageAsync(chat, text, replyMarkup: Keyboard(buttons), cancellationToken: ct)).MessageId;
    public async Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null)
    {
        using var stream = new MemoryStream(photo);
        await Bot.SendPhotoAsync(chat, InputFile.FromStream(stream, name), caption: caption, replyMarkup: Keyboard(buttons), cancellationToken: ct);
    }
    public async Task SendDocumentAsync(long chat, byte[] file, string name, string? caption, CancellationToken ct)
    {
        using var stream = new MemoryStream(file);
        await Bot.SendDocumentAsync(chat, InputFile.FromStream(stream, name), caption: caption, cancellationToken: ct);
    }
    public Task EditTextAsync(long chat, int message, string text, CancellationToken ct) => Bot.EditMessageTextAsync(chat, message, text, cancellationToken: ct);
    public Task DeleteAsync(long chat, int message, CancellationToken ct) => Bot.DeleteMessageAsync(chat, message, ct);
    public Task AnswerAsync(string callback, CancellationToken ct) => Bot.AnswerCallbackQueryAsync(callback, cancellationToken: ct);
    /// <summary>Persian speakers get Persian descriptions and everyone else English, which is how Telegram itself picks between the two lists.</summary>
    public async Task SetCommandsAsync(IReadOnlyList<BotCommandText> commands, CancellationToken ct)
    {
        await Bot.SetMyCommandsAsync(commands.Select(c => new BotCommand { Command = c.Command, Description = c.En }), cancellationToken: ct);
        await Bot.SetMyCommandsAsync(commands.Select(c => new BotCommand { Command = c.Command, Description = c.Fa }), languageCode: "fa", cancellationToken: ct);
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
