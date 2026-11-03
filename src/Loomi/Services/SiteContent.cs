using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Loomi.Services;
/// <summary>One price on the intro page, in the owner's words: the page never works out or rounds a price, it shows what was written.</summary>
public record SitePrice(string? Name, string? Detail, string? Price, bool Highlight);
/// <summary>What the owner writes for the intro page and how it is kept. A setting's row holds at most <see cref="MaxStored"/> characters, so what is
/// measured in the end is the JSON itself; anything that would not fit is refused rather than cut short. Null from a check means refused.</summary>
public static partial class SiteContent
{
    public const int MaxRows = 6, MaxName = 60, MaxDetail = 200, MaxPrice = 40, MaxContact = 100, MaxStored = 4000;
    /// <summary>The default encoder writes every Persian letter as \uXXXX, which takes rows well inside their limits to several times what the setting can hold.</summary>
    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The rows as they are kept, trimmed and with an empty detail as none; null when any of them breaks a limit or more than one is highlighted.</summary>
    public static string? Encode(IReadOnlyList<SitePrice?>? rows)
    {
        if (rows is null) return "[]";
        if (rows.Count > MaxRows || rows.Count(r => r?.Highlight == true) > 1) return null;
        var clean = new List<SitePrice>(rows.Count);
        foreach (var row in rows)
        {
            var name = row?.Name?.Trim(); var price = row?.Price?.Trim(); var detail = string.IsNullOrWhiteSpace(row?.Detail) ? null : row.Detail.Trim();
            if (name is not { Length: > 0 and <= MaxName } || price is not { Length: > 0 and <= MaxPrice } || detail?.Length > MaxDetail) return null;
            if ((name + price + detail).Any(char.IsControl)) return null;
            clean.Add(new SitePrice(name, detail, price, row!.Highlight));
        }
        var json = JsonSerializer.Serialize(clean, Stored);
        return json.Length <= MaxStored ? json : null;
    }
    /// <summary>A row that does not read is no prices at all: the section is simply left out rather than the public page failing.</summary>
    public static List<SitePrice> Decode(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return [];
        try { return JsonSerializer.Deserialize<List<SitePrice>>(stored, Stored) ?? []; }
        catch (JsonException) { return []; }
    }
    /// <summary>The contact as it is kept: "" for none, a Telegram handle, or an https address that shows where it goes. Null for anything else. The page
    /// turns it into a link, so a scheme of any other kind must never get this far, and an address with a name before an @ can read as a site it is not.</summary>
    public static string? Contact(string? value)
    {
        var contact = value?.Trim() ?? "";
        if (contact.Length == 0) return "";
        if (contact.Length > MaxContact) return null;
        if (contact[0] == '@') return TelegramHandle().IsMatch(contact) ? contact : null;
        return !contact.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            && Uri.TryCreate(contact, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps && address.Host.Length > 0 && address.UserInfo.Length == 0 ? contact : null;
    }
    /// <summary>Telegram's own rule for a public username: five to thirty-two letters, digits and underscores, starting with a letter.</summary>
    [GeneratedRegex("^@[A-Za-z][A-Za-z0-9_]{4,31}$")] private static partial Regex TelegramHandle();
}
