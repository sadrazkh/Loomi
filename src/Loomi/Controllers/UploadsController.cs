using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers;
/// <summary>Reference images, held until a generation uses them or the sweeper drops them. A file is what its bytes say, never what it was named.</summary>
[ApiController, Authorize, Route("api/uploads")]
public class UploadsController(AppDbContext db, IImageStorage storage, IOptions<BrowserOptions> browser) : ControllerBase
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    /// <summary>Four files at the ceiling plus multipart framing; anything larger is refused at the edge instead of buffered.</summary>
    public const long MaxRequestBytes = 4 * MaxFileBytes + 1024 * 1024;
    private Viewer Me => Viewer.From(User);
    [HttpPost, RequestSizeLimit(MaxRequestBytes), RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest(new { error = "NoFiles" });
        var files = (await Request.ReadFormAsync(ct)).Files;
        if (files.Count == 0) return BadRequest(new { error = "NoFiles" });
        if (files.Count > browser.Value.MaxInputs) return BadRequest(new { error = "TooManyFiles" });
        // Every file is judged before any is written, so one bad file in the batch leaves nothing behind.
        var contents = new List<byte[]>();
        foreach (var file in files)
        {
            if (file.Length > MaxFileBytes) return BadRequest(new { error = "FileTooLarge" });
            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (ImageStorage.ExtensionFor(bytes) == null) return BadRequest(new { error = "InvalidImage" });
            contents.Add(bytes);
        }
        var uploads = new List<Upload>();
        foreach (var bytes in contents)
        {
            var upload = new Upload { UserId = Me.Id, Bytes = bytes.Length };
            upload.Path = await storage.SaveUploadAsync(Me.Id, upload.Id, bytes, ct);
            upload.ContentType = ImageStorage.ContentTypeFor(upload.Path);
            uploads.Add(upload);
        }
        db.Uploads.AddRange(uploads); await db.SaveChangesAsync(ct);
        return Created("/api/uploads", uploads.Select(u => new { u.Id, u.Bytes, u.ContentType }));
    }
    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var upload = await db.Uploads.AsNoTracking().OwnedBy(Me).FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();
        var path = storage.Resolve(upload.Path);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, upload.ContentType);
    }
    /// <summary>Only while nothing refers to it: a file a generation was made from is that generation's history.</summary>
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var upload = await db.Uploads.OwnedBy(Me).FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();
        if (await db.GenerationInputs.AnyAsync(i => i.UploadId == id, ct)) throw new InvalidOperationException("UploadInUse");
        db.Uploads.Remove(upload); await db.SaveChangesAsync(ct);
        storage.Delete(upload.Path);
        return NoContent();
    }
}
