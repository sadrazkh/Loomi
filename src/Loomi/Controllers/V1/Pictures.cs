using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers.V1;
/// <summary>The pictures a request brings, turned into the references GenerationService takes. An upload id is passed on as it is, and the service
/// decides whether it is the caller's. Bytes sent inline are judged exactly as POST /api/uploads judges a file — size, then what the bytes are — every
/// one before any is written, and are then kept as the caller's own uploads.</summary>
internal sealed class Pictures(AppDbContext db, IImageStorage storage, Guid owner)
{
    /// <summary>Four pictures at the file ceiling, as base64, and room for the rest of the request. Kestrel's default would refuse two.</summary>
    public const long MaxBodyBytes = 4 * UploadsController.MaxFileBytes / 3 * 4 + 1024 * 1024;
    /// <summary>The base64 length of a file at the ceiling, and a little slack for line breaks some encoders add.</summary>
    private const long MaxBase64Chars = (UploadsController.MaxFileBytes + 2) / 3 * 4 + 64 * 1024;
    private readonly List<Upload> written = [];

    /// <summary>Runs the work with the request's pictures in hand. Pictures this request saved go again when the work is refused, rather than
    /// waiting a day for the sweeper.</summary>
    public static async Task<IActionResult> WithAsync(AppDbContext db, IImageStorage storage, Guid owner, List<ImageInput>? images, int max, Func<List<InputRef>, Task<IActionResult>> work, CancellationToken ct)
    {
        var pictures = new Pictures(db, storage, owner);
        try
        {
            var (refs, error) = await pictures.ReadAsync(images, max, ct);
            return error is not null ? new BadRequestObjectResult(new ErrorDto(error)) : await work(refs);
        }
        catch { await pictures.DiscardAsync(); throw; }
    }

    private async Task<(List<InputRef> Refs, string? Error)> ReadAsync(List<ImageInput>? images, int max, CancellationToken ct)
    {
        images ??= [];
        // Counted before anything is decoded, and refused the way the service refuses it, so the code means one thing whichever way it arrives.
        if (images.Count > max) throw new InvalidOperationException("TooManyInputs");
        var refs = new InputRef[images.Count];
        var pending = new List<(int Index, byte[] Bytes)>();
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (image is null || (image.UploadId is null) == (image.Base64 is null)) return ([], "InvalidImageInput");
            if (image.UploadId is { } upload) { refs[i] = new InputRef(upload, null); continue; }
            var (bytes, error) = Decode(image.Base64!);
            if (error is not null) return ([], error);
            pending.Add((i, bytes!));
        }
        foreach (var (index, bytes) in pending)
        {
            var upload = new Upload { UserId = owner, Bytes = bytes.Length };
            upload.Path = await storage.SaveUploadAsync(owner, upload.Id, bytes, ct);
            upload.ContentType = ImageStorage.ContentTypeFor(upload.Path);
            written.Add(upload);
            refs[index] = new InputRef(upload.Id, null);
        }
        if (written.Count > 0) { db.Uploads.AddRange(written); await db.SaveChangesAsync(ct); }
        return (refs.ToList(), null);
    }

    /// <summary>Plain base64 or a data: URL. The media type a data: URL claims is not believed: the bytes say what the file is.</summary>
    private static (byte[]? Bytes, string? Error) Decode(string text)
    {
        var payload = text.AsSpan().Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma < 0 || !payload[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return (null, "InvalidBase64");
            payload = payload[(comma + 1)..];
        }
        if (payload.Length > MaxBase64Chars) return (null, "FileTooLarge");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload.ToString()); }
        catch (FormatException) { return (null, "InvalidBase64"); }
        if (bytes.Length > UploadsController.MaxFileBytes) return (null, "FileTooLarge");
        return ImageStorage.ExtensionFor(bytes) is null ? (null, "InvalidImage") : (bytes, null);
    }

    private async Task DiscardAsync()
    {
        if (written.Count == 0) return;
        db.ChangeTracker.Clear();
        var ids = written.Select(u => u.Id).ToList();
        await db.Uploads.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(CancellationToken.None);
        foreach (var upload in written) storage.Delete(upload.Path);
    }
}
