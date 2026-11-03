using Loomi.Data;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
/// <summary>The two routes a stranger's browser reaches before anyone has signed in, for the intro page. Each answer is the same for everybody, so it may be
/// kept by whatever sits in between. Its shape is written out field by field, and the query asks for only those fields: a template's prompt and
/// instructions are not even read, so a later change to the model cannot carry them out here.</summary>
[ApiController, AllowAnonymous, Route("api/public")]
public class PublicController(AppDbContext db, GrowthSettings settings, BotSettings bot, IImageStorage storage, ImagePreviews previews) : ControllerBase
{
    private const int ShowcaseSize = 24, DefaultWidth = 640;
    /// <summary>Narrower than the app's own list: a card and a large screen's hero need no more, and the widest copy is close to the original itself.</summary>
    private static readonly int[] Widths = [256, DefaultWidth, 1024];

    /// <summary>With sign-up closed nobody is being invited in, so neither the bot nor its gift is named.</summary>
    [HttpGet("site")] public async Task<IActionResult> Site(CancellationToken ct)
    {
        var open = bot.Configured && await settings.OnAsync(GrowthSettings.SignupOpen, ct);
        var gift = open ? Math.Max(0, await settings.IntAsync(GrowthSettings.SignupGiftImages, GrowthSettings.DefaultGiftImages, ct)) : (int?)null;
        var contact = await settings.GetAsync(GrowthSettings.SiteContact, ct);
        var prices = SiteContent.Decode(await settings.GetAsync(GrowthSettings.SitePrices, ct));
        var samples = await (from t in db.Templates.AsNoTracking()
                             join c in db.TemplateCategories.AsNoTracking() on t.CategoryId equals c.Id
                             where t.IsActive && t.SamplePath != null
                             orderby c.Order, c.Name, t.Order, t.Title
                             select new { t.Id, t.Title, Category = c.Name, t.SamplePath, t.UpdatedAt }).Take(ShowcaseSize).ToListAsync(ct);
        Response.Headers.CacheControl = "public, max-age=60";
        return Ok(new
        {
            botUsername = open ? bot.Username?.TrimStart('@') : null,
            signupOpen = open,
            giftImages = gift,
            contact = string.IsNullOrEmpty(contact) ? null : contact,
            prices = prices.Select(p => new { p.Name, p.Detail, p.Price, p.Highlight }),
            showcase = samples.Where(s => System.IO.File.Exists(storage.Resolve(s.SamplePath!)))
                .Select(s => new { s.Id, s.Title, s.Category, sample = $"/api/public/templates/{s.Id}/sample?v={s.UpdatedAt.Ticks}" })
        });
    }
    /// <summary>Always a WebP copy at one of the fixed widths: the original is the owner's full picture and never leaves here, so a sample that cannot be made
    /// small is a miss. The address carries the version, so a replaced sample shows at once; a day is how long one taken down may linger in a cache.</summary>
    [HttpGet("templates/{id:guid}/sample")] public async Task<IActionResult> Sample(Guid id, string? w, CancellationToken ct)
    {
        var path = await db.Templates.AsNoTracking().Where(t => t.Id == id && t.IsActive).Select(t => t.SamplePath).FirstOrDefaultAsync(ct);
        if (path is null || !System.IO.File.Exists(storage.Resolve(path))) return NotFound();
        if (await previews.GetAsync(path, WidthFor(w), ct) is not { } preview) return NotFound();
        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(preview, "image/webp");
    }
    private static int WidthFor(string? requested) => ImagePreviews.WidthFor(requested) is { } width ? Widths.MinBy(x => Math.Abs(x - width)) : DefaultWidth;
}
