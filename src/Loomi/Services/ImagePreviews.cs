using System.Globalization;
using SkiaSharp;
namespace Loomi.Services;
/// <summary>Smaller copies of stored pictures, for the places that show them small. A finished picture is ChatGPT's PNG of a couple of megabytes, and the
/// web showed every one whole, a 28 px thumbnail included; on the connections Loomi's people use, that kept a picture seconds from appearing. A copy is
/// WebP, made the first time it is asked for and kept beside the original from then on, so it costs the decoding once.</summary>
public class ImagePreviews(IImageStorage storage, ILogger<ImagePreviews> logger)
{
    /// <summary>The only widths made, so a picture has at most this many copies however the width is asked for.</summary>
    public static readonly int[] Widths = [256, 640, 1024, 1600];
    public const int Quality = 80;
    /// <summary>Decoding a large PNG takes a core and tens of megabytes for a moment, and a gallery opened for the first time asks for dozens at once.</summary>
    private readonly SemaphoreSlim gate = new(Math.Max(1, Environment.ProcessorCount));

    /// <summary>The width a request earns: the nearest of <see cref="Widths"/>, or null, meaning the original, for anything but a plain number up to the largest.</summary>
    public static int? WidthFor(string? requested) =>
        int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0 && w <= Widths[^1] ? Widths.MinBy(x => Math.Abs(x - w)) : null;

    /// <summary>The full path of a WebP copy of the stored image no wider than <paramref name="width"/>, or null when the original should go instead:
    /// a file that does not decode, or a copy that could not be written.</summary>
    public async Task<string?> GetAsync(string original, int width, CancellationToken ct)
    {
        var target = storage.Resolve(ImageStorage.PreviewOf(original, width));
        if (File.Exists(target)) return target;
        await gate.WaitAsync(ct);
        try { return File.Exists(target) || Make(storage.Resolve(original), target, width) ? target : null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A preview could not be made ({Type})", ex.GetType().Name);
            return null;
        }
        finally { gate.Release(); }
    }

    private bool Make(string source, string target, int width)
    {
        using var bitmap = SKBitmap.Decode(source);
        if (bitmap is null) { logger.LogWarning("A preview could not be made ({Type})", "Undecodable"); return false; }
        // Never wider than the original: a picture already narrow enough is only re-encoded, which alone takes a 1024 px PNG to a fraction of its size.
        var w = Math.Min(width, bitmap.Width);
        var h = Math.Max(1, (int)Math.Round(bitmap.Height * (double)w / bitmap.Width));
        // Mipmaps for a big step down, where a cubic filter samples too few of the pixels it replaces and the result shimmers; Mitchell for a small one,
        // where mipmaps would soften it.
        var sampling = bitmap.Width >= w * 2 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var resized = w == bitmap.Width ? null : bitmap.Resize(bitmap.Info.WithSize(w, h), sampling);
        using var data = (w == bitmap.Width ? bitmap : resized)?.Encode(SKEncodedImageFormat.Webp, Quality);
        if (data is null) { logger.LogWarning("A preview could not be made ({Type})", "Unencodable"); return false; }
        // Written aside and moved into place, so a request arriving meanwhile never reads half a file. Two requests making the same copy at once both
        // write one; the second finds the first's in place and keeps it.
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
