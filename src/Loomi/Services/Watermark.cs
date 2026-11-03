using Loomi.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
namespace Loomi.Services;
/// <summary>The small Loomi mark on what a gift paid for, and the one place that decides which file of a generation is handed out. The stored picture stays as
/// ChatGPT made it, because the worker compares its digest with what comes back and sends it back in; the mark goes on a copy beside it, marked.png, made the
/// first time it is asked for. A copy that cannot be made costs only the mark: the picture goes out unmarked.
/// <para>The mark is Assets/watermark.png, because the server has no fonts to draw it with. It was rendered once with Pillow on the development machine: the
/// weave from components/LoomiMark.vue in plain white with a gap wherever one thread crosses another, the word Loomi in Vazirmatn ExtraBold from the client's
/// @fontsource package, and a soft black edge of two Gaussian blurs so that it reads on a light picture too. Its background is transparent.</para></summary>
public class Watermark(IImageStorage storage, ILogger<Watermark> logger)
{
    public const string Resource = "Loomi.Assets.watermark.png";
    /// <summary>The file's width against the picture's. It keeps room for its soft edge around the letters, so the mark itself takes about 17% of the picture.</summary>
    private const float Share = 0.19f;
    /// <summary>From the corner to the file's edge, against the picture's width; the room for the soft edge adds to it.</summary>
    private const float Margin = 0.012f;
    /// <summary>About 70%: there to be seen, not to spoil the picture.</summary>
    private const byte Opacity = 179;
    private static readonly Lazy<SKImage?> Mark = new(Load);
    /// <summary>The same reason as <see cref="ImagePreviews"/>: decoding a large PNG takes a core and tens of megabytes for a moment.</summary>
    private readonly SemaphoreSlim gate = new(Math.Max(1, Environment.ProcessorCount));
    private static SKImage? Load()
    {
        using var stream = typeof(Watermark).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) return null;
        using var bitmap = SKBitmap.Decode(stream);
        return bitmap is null ? null : SKImage.FromBitmap(bitmap);
    }
    private static string MarkedOf(string stored) => stored[..(stored.LastIndexOf('/') + 1)] + "marked.png";

    /// <summary>The stored path of what people get for this picture: the marked copy when a gift paid for it, the original otherwise. A smaller copy made from
    /// the answer is of the same file, and is named after it, so a preview of the unmarked picture can never stand in for a marked one.</summary>
    public async Task<string> DeliveredAsync(AppDbContext db, Guid? package, string stored, CancellationToken ct) =>
        package is { } id && await db.CreditPackages.AsNoTracking().AnyAsync(p => p.Id == id && p.IsGift, ct) ? await MarkedAsync(stored, ct) ?? stored : stored;

    /// <summary>The stored path of the marked copy, made if it is not there yet, or null when it cannot be made.</summary>
    public async Task<string?> MarkedAsync(string stored, CancellationToken ct)
    {
        var relative = MarkedOf(stored);
        var target = storage.Resolve(relative);
        if (File.Exists(target)) return relative;
        await gate.WaitAsync(ct);
        // Never the exception's text, which can carry a path; the type says enough.
        try { return File.Exists(target) || Make(storage.Resolve(stored), target) ? relative : null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A picture could not be marked ({Type})", ex.GetType().Name);
            return null;
        }
        finally { gate.Release(); }
    }
    private bool Make(string source, string target)
    {
        using var picture = SKBitmap.Decode(source);
        if (picture is null || Mark.Value is not { } mark) { logger.LogWarning("A picture could not be marked ({Type})", picture is null ? "Undecodable" : "NoMark"); return false; }
        using var surface = SKSurface.Create(new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.DrawBitmap(picture, 0, 0);
        var width = picture.Width * Share;
        var height = width * mark.Height / mark.Width;
        var margin = picture.Width * Margin;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Opacity) };
        // Mipmaps, since the file is usually drawn several times smaller than it is and a cubic filter would shimmer on letters this thin.
        surface.Canvas.DrawImage(mark, SKRect.Create(picture.Width - width - margin, picture.Height - height - margin, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null) { logger.LogWarning("A picture could not be marked ({Type})", "Unencodable"); return false; }
        // Written aside and moved into place, like a preview, so a request arriving meanwhile never reads half a file.
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, data.AsSpan());
            File.Move(temp, target);
        }
        catch (IOException) when (File.Exists(target)) { }
        finally { File.Delete(temp); }
        return true;
    }
}
