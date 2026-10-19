using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Loomi.Tests;
/// <summary>The web asks for each picture at the size it shows it: a WebP copy made once, kept beside the original and served from then on. A finished
/// picture is a PNG of a few megabytes, which on a slow connection kept a 28 px thumbnail waiting seconds. The plain address still gives the original.</summary>
public class ImagePreviewTests
{
    /// <summary>A gradient under fine grain, so the PNG weighs what a real picture does rather than the few bytes a flat colour packs into.</summary>
    private static byte[] Png(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = new byte[info.BytesSize];
        var random = new Random(7);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)random.Next(96, 160), 255);
            }
        using var image = SKImage.FromPixelCopy(info, pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    private static readonly Lazy<byte[]> Large = new(() => Png(2048, 2048));
    private static async Task<(Guid Project, Guid Generation)> PictureAsync(ApiV1World w, Guid user, byte[] bytes)
    {
        using var scope = w.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = user, Title = "Lighthouse" };
        var g = new Generation { ProjectId = project.Id, UserId = user, Prompt = "a lighthouse at dusk", Status = RunStatus.Completed };
        g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(project.Id, g.Id, bytes, default);
        db.Projects.Add(project); db.Generations.Add(g); await db.SaveChangesAsync();
        return (project.Id, g.Id);
    }
    private static string Folder(ApiV1World w, Guid project, Guid generation) => Path.Combine(w.Factory.Root, "images", "projects", project.ToString(), generation.ToString());
    /// <summary>Everything in a generation's folder but its picture: the copies, and any half-written file one left behind.</summary>
    private static string[] Previews(string folder) => Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).StartsWith("image.")).ToArray();
    private static bool IsWebp(byte[] bytes) => bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
    private static (int Width, int Height) SizeOf(byte[] bytes) { var info = SKBitmap.DecodeBounds(bytes); return (info.Width, info.Height); }

    [Fact]
    public async Task A_picture_asked_for_at_a_width_comes_back_as_a_much_smaller_webp_that_wide()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, api) = await w.MemberAsync("sara");
        var (_, id) = await PictureAsync(w, sara, Large.Value);

        var response = await api.GetAsync($"/api/generations/{id}/image?w=1024");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        // A generation's picture never changes, so the copy is kept for good; the original's address stays uncached as before.
        var cache = response.Headers.CacheControl!;
        Assert.Equal((true, TimeSpan.FromDays(365), false), (cache.Private, cache.MaxAge, cache.NoStore));
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");
        var preview = await response.Content.ReadAsByteArrayAsync();
        Assert.True(IsWebp(preview));
        Assert.True(preview.Length * 4 < Large.Value.Length, $"{preview.Length:N0} bytes against the original's {Large.Value.Length:N0}");
        Assert.Equal((1024, 1024), SizeOf(preview));
    }

    [Fact]
    public async Task A_copy_is_made_once_beside_the_original_and_goes_when_the_chat_does()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, api) = await w.MemberAsync("sara");
        var (project, id) = await PictureAsync(w, sara, Large.Value);
        var folder = Folder(w, project, id);

        var first = await api.GetByteArrayAsync($"/api/generations/{id}/image?w=1024");
        var kept = Path.Combine(folder, "preview-1024.webp");
        Assert.Equal(first, await File.ReadAllBytesAsync(kept));
        var made = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(kept, made);

        // A width near one of the fixed few is that one, so asking again in a slightly different way is served from the same file, untouched.
        Assert.Equal(first, await api.GetByteArrayAsync($"/api/generations/{id}/image?w=1000"));
        Assert.Equal(made, File.GetLastWriteTimeUtc(kept));
        Assert.Equal([kept], Previews(folder));

        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/projects/{project}")).StatusCode);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Without_a_width_or_with_one_out_of_range_the_original_comes_back_untouched()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, api) = await w.MemberAsync("sara");
        var original = Png(120, 90);
        var (project, id) = await PictureAsync(w, sara, original);
        foreach (var query in new[] { "", "?w=", "?w=abc", "?w=0", "?w=-640", "?w=+640", "?w=5000", "?w=99999999999", "?w=1e3", "?width=640" })
        {
            var response = await api.GetAsync($"/api/generations/{id}/image{query}");
            Assert.Equal((HttpStatusCode.OK, "image/png"), (response.StatusCode, response.Content.Headers.ContentType?.MediaType));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Empty(Previews(Folder(w, project, id)));
    }

    [Fact]
    public async Task A_member_cannot_reach_another_members_copy_and_the_owner_can()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, _) = await w.MemberAsync("sara");
        var (_, omid) = await w.MemberAsync("omid");
        var (project, id) = await PictureAsync(w, sara, Png(300, 200));

        // A miss, as for the original, so ids cannot be probed; and nothing is made for the asking.
        Assert.Equal(HttpStatusCode.NotFound, (await omid.GetAsync($"/api/generations/{id}/image?w=256")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await omid.GetAsync($"/api/generations/{id}/image")).StatusCode);
        Assert.Empty(Previews(Folder(w, project, id)));

        // The owner's page, signed in the way the browser is, sees everyone's work.
        using var owner = await UserAdminTests.OwnerSessionAsync(w.Factory);
        var response = await owner.GetAsync($"/api/generations/{id}/image?w=256");
        Assert.Equal((HttpStatusCode.OK, "image/webp"), (response.StatusCode, response.Content.Headers.ContentType?.MediaType));
    }

    [Fact]
    public async Task A_small_picture_is_never_made_larger_and_keeps_its_shape()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, api) = await w.MemberAsync("sara");
        var (_, id) = await PictureAsync(w, sara, Png(300, 200));

        var whole = await api.GetByteArrayAsync($"/api/generations/{id}/image?w=640");
        Assert.True(IsWebp(whole));
        Assert.Equal((300, 200), SizeOf(whole));
        Assert.Equal((256, 171), SizeOf(await api.GetByteArrayAsync($"/api/generations/{id}/image?w=256")));
    }

    [Fact]
    public async Task A_file_that_does_not_decode_is_served_as_it_is()
    {
        using var w = await ApiV1World.CreateAsync();
        var (sara, api) = await w.MemberAsync("sara");
        // The PNG signature passes the storage's check; nothing after it is a picture.
        byte[] broken = [137, 80, 78, 71, 13, 10, 26, 10, .. "not really a picture at all"u8];
        var (project, id) = await PictureAsync(w, sara, broken);

        var response = await api.GetAsync($"/api/generations/{id}/image?w=256");
        Assert.Equal((HttpStatusCode.OK, "image/png"), (response.StatusCode, response.Content.Headers.ContentType?.MediaType));
        Assert.Equal(broken, await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(Previews(Folder(w, project, id)));
    }

    [Fact]
    public async Task A_template_sample_comes_at_the_card_width_and_a_replaced_one_takes_its_copies_with_it()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, owner) = await w.OwnerAsync();
        var (_, member) = await w.MemberAsync("sara");
        var category = (await (await owner.PostAsJsonAsync("/api/templates/categories", new { name = "Portraits", order = 1 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await owner.PostAsJsonAsync("/api/templates", new { categoryId = category, title = "Film portrait", instructions = (string?)null, prompt = "85mm film portrait", provider = "ChatGPT", minInputs = 1, maxInputs = 1, sendSample = false, isActive = true, order = 1 });
        var template = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        async Task<string> UploadAsync(byte[] bytes)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "sample.png");
            var uploaded = await owner.PostAsync($"/api/templates/{template}/sample", form);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            return (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sampleUrl").GetString()!;
        }
        var folder = Path.Combine(w.Factory.Root, "images", "templates", template.ToString());

        var url = await UploadAsync(Large.Value);
        var card = await member.GetAsync($"{url}&w=640");
        Assert.Equal((HttpStatusCode.OK, "image/webp"), (card.StatusCode, card.Content.Headers.ContentType?.MediaType));
        Assert.Equal((640, 640), SizeOf(await card.Content.ReadAsByteArrayAsync()));
        Assert.Equal(Large.Value, await member.GetByteArrayAsync(url));
        var copy = Assert.Single(Directory.GetFiles(folder, "*.webp"));

        // The next sample is a new file with copies of its own; the old one's go with it rather than lingering.
        url = await UploadAsync(Png(400, 300));
        Assert.Equal((400, 300), SizeOf(await member.GetByteArrayAsync($"{url}&w=640")));
        Assert.DoesNotContain(copy, Directory.GetFiles(folder));
        Assert.Single(Directory.GetFiles(folder, "*.webp"));
    }
}
