using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
public class InputTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    /// <summary>Only the signature is inspected, so a header followed by padding is as good as a real photo here.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
    private static readonly byte[] Webp = [.. Encoding.ASCII.GetBytes("RIFF"), 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("WEBPVP8 "), 0, 0, 0, 0];

    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }
    private static async Task<Guid> MemberAsync(AppFactory factory, string username, string password, int credits = 1000)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user);
        if (credits != 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user.Id;
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task<Guid> ProjectAsync(AppFactory factory, Guid user, string title) =>
        await WithDbAsync(factory, async db => { var p = new ImageProject { UserId = user, Title = title }; db.Projects.Add(p); await db.SaveChangesAsync(); return p.Id; });
    /// <summary>A finished generation with a real file behind it, the shape an input has to have.</summary>
    private static async Task<Guid> CompletedAsync(AppFactory factory, Guid user, Guid project)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = new Generation { ProjectId = project, UserId = user, Prompt = "seeded", Status = RunStatus.Completed };
        g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(project, g.Id, Png, default);
        db.Generations.Add(g); await db.SaveChangesAsync();
        return g.Id;
    }
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }
        return await client.PostAsync("/api/uploads", form);
    }
    private static async Task<Guid> OneUploadAsync(HttpClient client, string name = "a.png", byte[]? bytes = null)
    {
        var response = await UploadAsync(client, (name, bytes ?? Png));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()).GetProperty("id").GetGuid();
    }
    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var error = body.StartsWith('{') ? JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null : null;
        Assert.True(error != null, $"{(int)response.StatusCode} {body}");
        return error!;
    }

    [Fact]
    public async Task Uploads_are_judged_by_their_bytes_not_their_names()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var forged = await UploadAsync(client, ("photo.png", Encoding.ASCII.GetBytes("<html>this is not an image at all</html>")));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("InvalidImage", await ErrorAsync(forged));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
        var real = await UploadAsync(client, ("notes.txt", Png), ("b.bin", Jpeg), ("c", Webp));
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        var rows = (await real.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(["image/png", "image/jpeg", "image/webp"], rows.Select(r => r.GetProperty("contentType").GetString()));
        Assert.Equal(Png.Length, rows[0].GetProperty("bytes").GetInt64());
        var stored = await WithDbAsync(factory, db => db.Uploads.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync());
        Assert.Equal(3, stored.Count);
        Assert.All(stored, u => Assert.Equal(sara, u.UserId));
        Assert.EndsWith(".png", stored[0].Path); Assert.EndsWith(".jpg", stored[1].Path); Assert.EndsWith(".webp", stored[2].Path);
        Assert.StartsWith("uploads/" + sara + "/", stored[0].Path);
        Assert.True(File.Exists(Path.Combine(factory.Root, "images", stored[0].Path)));
        // The bytes come back as what they are, whatever the file was called.
        var served = await client.GetAsync($"/api/uploads/{rows[1].GetProperty("id").GetGuid()}");
        Assert.Equal("image/jpeg", served.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_file_over_twenty_megabytes_is_refused_before_anything_is_written()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var huge = new byte[20 * 1024 * 1024 + 1];
        Png.AsSpan(0, 8).CopyTo(huge);
        var response = await UploadAsync(client, ("ok.png", Png), ("huge.png", huge));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("FileTooLarge", await ErrorAsync(response));
        // One bad file fails the whole request; the good one beside it is not kept half-way.
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "images", "uploads")) && Directory.EnumerateFiles(Path.Combine(factory.Root, "images", "uploads"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Five_files_in_one_request_or_none_at_all_are_refused()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var many = await UploadAsync(client, Enumerable.Range(0, 5).Select(i => ($"{i}.png", Png)).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal("TooManyFiles", await ErrorAsync(many));
        // What a browser sends for an empty FormData: the closing boundary and nothing else.
        var empty = new StringContent("--loomi--\r\n");
        empty.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=loomi");
        var none = await client.PostAsync("/api/uploads", empty);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("NoFiles", await ErrorAsync(none));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
    }

    [Fact]
    public async Task Another_users_upload_is_a_miss_and_the_owner_sees_everyones()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        var omid = await MemberAsync(factory, "omid", "omid-strong-pass");
        using var sara = await SignInAsync(factory, "sara", "sara-strong-pass");
        using var omidClient = await SignInAsync(factory, "omid", "omid-strong-pass");
        using var owner = await SignInAsync(factory, "owner", AppFactory.Key);
        var upload = await OneUploadAsync(sara);
        Assert.Equal(HttpStatusCode.OK, (await sara.GetAsync($"/api/uploads/{upload}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/uploads/{upload}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await omidClient.GetAsync($"/api/uploads/{upload}")).StatusCode);
        var project = await ProjectAsync(factory, omid, "omid's");
        var submitted = await omidClient.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "use it", inputs = new[] { new { uploadId = upload } } });
        Assert.Equal(HttpStatusCode.NotFound, submitted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await omidClient.DeleteAsync($"/api/uploads/{upload}")).StatusCode);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
    }

    [Fact]
    public async Task Inputs_are_recorded_in_the_order_given_and_shown_on_the_row()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "collage");
        var earlier = await CompletedAsync(factory, sara, project);
        var first = await OneUploadAsync(client, "first.png");
        var second = await OneUploadAsync(client, "second.png");
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "combine these", inputs = new object[] { new { uploadId = second }, new { generationId = earlier }, new { uploadId = first } } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var urls = dto.GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("url").GetString()).ToList();
        Assert.Equal([$"/api/uploads/{second}", $"/api/generations/{earlier}/image", $"/api/uploads/{first}"], urls);
        // Three inputs are a set, not a lineage: nothing is the parent.
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("parentGenerationId").ValueKind);
        var id = dto.GetProperty("id").GetGuid();
        var rows = await WithDbAsync(factory, db => db.GenerationInputs.AsNoTracking().Where(i => i.GenerationId == id).OrderBy(i => i.Order).ToListAsync());
        Assert.Equal([0, 1, 2], rows.Select(r => r.Order));
        Assert.Equal(second, rows[0].UploadId); Assert.Equal(earlier, rows[1].SourceGenerationId); Assert.Equal(first, rows[2].UploadId);
        // The list and the single read carry the same inputs, so a card can show what went in.
        var listed = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/generations")).EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == id);
        Assert.Equal(3, listed.GetProperty("inputs").GetArrayLength());
        Assert.Equal(3, (await client.GetFromJsonAsync<JsonElement>($"/api/generations/{id}")).GetProperty("inputs").GetArrayLength());
    }

    [Fact]
    public async Task A_single_generation_input_is_also_the_parent_and_an_edit_puts_its_parent_first()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "lineage");
        var earlier = await CompletedAsync(factory, sara, project);
        var alone = await (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "more of this", inputs = new[] { new { generationId = earlier } } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(earlier, alone.GetProperty("parentGenerationId").GetGuid());
        var upload = await OneUploadAsync(client);
        var edited = await client.PostAsJsonAsync($"/api/generations/{earlier}/edit", new { prompt = "add this", inputs = new[] { new { uploadId = upload } } });
        Assert.Equal(HttpStatusCode.Accepted, edited.StatusCode);
        var dto = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(earlier, dto.GetProperty("parentGenerationId").GetGuid());
        Assert.Equal([$"/api/generations/{earlier}/image", $"/api/uploads/{upload}"], dto.GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("url").GetString()));
        // Naming the parent again among the inputs does not double it.
        var branched = await (await client.PostAsJsonAsync($"/api/generations/{earlier}/branch", new { prompt = "again", inputs = new[] { new { generationId = earlier } } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, branched.GetProperty("inputs").GetArrayLength());
    }

    [Fact]
    public async Task Too_many_malformed_or_unfinished_inputs_are_refused()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "limits");
        var upload = await OneUploadAsync(client);
        var many = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "x", inputs = Enumerable.Repeat(new { uploadId = upload }, 5) });
        Assert.Equal(HttpStatusCode.Conflict, many.StatusCode);
        Assert.Equal("TooManyInputs", await ErrorAsync(many));
        var earlier = await CompletedAsync(factory, sara, project);
        var both = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "x", inputs = new[] { new { uploadId = upload, generationId = earlier } } });
        Assert.Equal(HttpStatusCode.Conflict, both.StatusCode);
        Assert.Equal("InvalidInput", await ErrorAsync(both));
        var neither = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "x", inputs = new[] { new { } } });
        Assert.Equal(HttpStatusCode.Conflict, neither.StatusCode);
        Assert.Equal("InvalidInput", await ErrorAsync(neither));
        var queued = await (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "plain" })).Content.ReadFromJsonAsync<JsonElement>();
        var unfinished = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "x", inputs = new[] { new { generationId = queued.GetProperty("id").GetGuid() } } });
        Assert.Equal(HttpStatusCode.Conflict, unfinished.StatusCode);
        Assert.Equal("InvalidInput", await ErrorAsync(unfinished));
        Assert.Equal(1, await WithDbAsync(factory, db => db.Generations.CountAsync(g => g.Prompt != "seeded")));
    }

    [Fact]
    public async Task Three_inputs_reach_the_site_as_three_files()
    {
        await using var launcher = new FixtureLauncher();
        Assert.Equal(3, await FilesSeenAsync(launcher, "multi"));
    }

    [Fact]
    public async Task A_site_that_takes_one_file_at_a_time_still_gets_all_three()
    {
        await using var launcher = new FixtureLauncher { Body = FixtureLauncher.BodyWith(false) };
        Assert.Equal(3, await FilesSeenAsync(launcher, "single"));
    }

    private static async Task<int> FilesSeenAsync(FixtureLauncher launcher, string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-inputs-" + name + "-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var paths = new List<string>();
            foreach (var i in Enumerable.Range(1, 3)) { var path = Path.Combine(root, $"in{i}.png"); await File.WriteAllBytesAsync(path, Png); paths.Add(path); }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
            await using var service = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 20 }), config, launcher);
            var result = await service.RunAsync(Operation.Generate, "compose", paths, null, (_, _) => Task.CompletedTask, default);
            Assert.True(result.Image.Length > 100);
            Assert.Equal(3, await launcher.Context.Pages[0].Locator("button[aria-label='Remove file']").CountAsync());
            return await launcher.Context.Pages[0].EvaluateAsync<int>("window.submission.files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Orphan_uploads_are_swept_after_a_day_and_used_or_fresh_ones_kept()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "sweep");
        var staleUnused = await OneUploadAsync(client, "stale.png");
        var staleUsed = await OneUploadAsync(client, "used.png");
        var fresh = await OneUploadAsync(client, "fresh.png");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "keep", inputs = new[] { new { uploadId = staleUsed } } })).StatusCode);
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        await WithDbAsync(factory, db => db.Uploads.Where(u => u.Id == staleUnused || u.Id == staleUsed).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, twoDaysAgo)));
        var paths = await WithDbAsync(factory, db => db.Uploads.AsNoTracking().ToDictionaryAsync(u => u.Id, u => Path.Combine(factory.Root, "images", u.Path)));
        Assert.All(paths.Values, p => Assert.True(File.Exists(p)));
        var storage = factory.Services.GetRequiredService<IImageStorage>();
        var swept = await WithDbAsync(factory, db => GenerationWorker.SweepUploadsAsync(db, storage, DateTime.UtcNow, default));
        Assert.Equal(1, swept);
        var left = await WithDbAsync(factory, db => db.Uploads.AsNoTracking().Select(u => u.Id).ToListAsync());
        Assert.Equal([staleUsed, fresh], left.OrderBy(id => id == staleUsed ? 0 : 1));
        Assert.False(File.Exists(paths[staleUnused]));
        Assert.True(File.Exists(paths[staleUsed]));
        Assert.True(File.Exists(paths[fresh]));
    }

    [Fact]
    public async Task An_unused_upload_can_be_deleted_and_a_used_one_cannot()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "delete");
        var spare = await OneUploadAsync(client, "spare.png");
        var used = await OneUploadAsync(client, "used.png");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "keep", inputs = new[] { new { uploadId = used } } })).StatusCode);
        var path = await WithDbAsync(factory, db => db.Uploads.AsNoTracking().Where(u => u.Id == spare).Select(u => Path.Combine(factory.Root, "images", u.Path)).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/uploads/{spare}")).StatusCode);
        Assert.False(File.Exists(path));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/uploads/{spare}")).StatusCode);
        var busy = await client.DeleteAsync($"/api/uploads/{used}");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("UploadInUse", await ErrorAsync(busy));
    }

    [Fact]
    public async Task The_dispatcher_hands_every_input_to_the_browser()
    {
        await using var launcher = new FixtureLauncher();
        using var factory = new DispatchFactory(launcher);
        using var client = await SignInAsync(factory, "owner", AppFactory.Key);
        var owner = await WithDbAsync(factory, db => db.Users.AsNoTracking().Where(u => u.NormalizedUsername == OwnerBootstrap.Username).Select(u => u.Id).SingleAsync());
        await WithDbAsync(factory, async db => { db.Accounts.Add(new ProviderAccount { Label = "one", ProfileDirectory = "one", DailyCap = 25 }); return await db.SaveChangesAsync(); });
        var project = await ProjectAsync(factory, owner, "dispatch");
        var earlier = await CompletedAsync(factory, owner, project);
        var upload = await OneUploadAsync(client);
        var queued = await (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "merge", inputs = new object[] { new { uploadId = upload }, new { generationId = earlier } } })).Content.ReadFromJsonAsync<JsonElement>();
        var id = queued.GetProperty("id").GetGuid();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Generation? settled = null;
        while (DateTime.UtcNow < deadline && settled is null)
        {
            var g = await WithDbAsync(factory, db => db.Generations.AsNoTracking().FirstAsync(x => x.Id == id));
            if (g.Status is RunStatus.Completed or RunStatus.Failed) settled = g; else await Task.Delay(500);
        }
        Assert.NotNull(settled);
        Assert.Equal(RunStatus.Completed, settled.Status);
        Assert.Equal(2, await launcher.Context.Pages[0].EvaluateAsync<int>("window.submission.files"));
        Assert.Equal("merge", await launcher.Context.Pages[0].EvaluateAsync<string>("window.submission.prompt"));
    }
}
