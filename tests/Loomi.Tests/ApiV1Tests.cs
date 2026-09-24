using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>What the /api/v1 tests share: a workspace with one enabled ChatGPT account, people who each hold a token, and a way to finish a message
/// the way a run would. Nothing here runs a browser; the worker is not started, so a message stays queued until a test finishes it.</summary>
internal sealed class ApiV1World : IDisposable
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    public static readonly string PngBase64 = Convert.ToBase64String(Png);
    public AppFactory Factory { get; } = new();
    public Guid Account { get; private set; }
    public static async Task<ApiV1World> CreateAsync()
    {
        var world = new ApiV1World();
        world.Account = await world.DbAsync(async db => { var a = new ProviderAccount { Label = "GPT1", ProfileDirectory = "gpt1" }; db.Accounts.Add(a); await db.SaveChangesAsync(); return a.Id; });
        return world;
    }
    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    public async Task<(Guid User, HttpClient Client)> MemberAsync(string name, int credits = 1000)
    {
        var id = await DbAsync(async db =>
        {
            var user = new AppUser { Username = name, NormalizedUsername = name, Role = UserRole.Member };
            db.Users.Add(user);
            if (credits != 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
            await db.SaveChangesAsync();
            return user.Id;
        });
        return (id, await ClientForAsync(id));
    }
    public async Task<(Guid User, HttpClient Client)> OwnerAsync()
    {
        var id = await DbAsync(db => db.Users.Where(u => u.NormalizedUsername == OwnerBootstrap.Username).Select(u => u.Id).SingleAsync());
        return (id, await ClientForAsync(id));
    }
    /// <summary>A client that carries nothing but a token: no cookie jar and no CSRF header, the way an outside tool calls.</summary>
    private async Task<HttpClient> ClientForAsync(Guid user)
    {
        var (token, prefix, hash) = ApiTokens.Mint();
        await DbAsync(async db => { db.ApiTokens.Add(new ApiToken { UserId = user, Name = "test", Prefix = prefix, Hash = hash }); return await db.SaveChangesAsync(); });
        var client = Factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    /// <summary>What a finished run leaves: a picture or an answer in words, the conversation it lives in and the account holding it. Announced the
    /// way the worker announces it only when asked, so a test can tell the announcement from the re-check.</summary>
    public async Task FinishAsync(Guid message, bool picture = true, bool announce = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = await db.Generations.SingleAsync(x => x.Id == message);
        (g.Status, g.ConversationUrl, g.AccountId) = (picture ? RunStatus.Completed : RunStatus.Answered, "https://chatgpt.com/c/one", Account);
        if (picture) g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
        else g.ReplyText = "Which style would you like?";
        await db.SaveChangesAsync();
        if (announce) await scope.ServiceProvider.GetRequiredService<GenerationEvents>().PublishAsync(GenerationEvent.From(g), default);
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    public static async Task<string> ErrorAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("error").GetString()!;
    public static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();
    public void Dispose() => Factory.Dispose();
}

public class ApiV1Tests
{
    private static Task<HttpResponseMessage> StartAsync(HttpClient client, object body) => client.PostAsJsonAsync("/api/v1/chats", body);
    private static async Task<(Guid Chat, Guid Message)> StartedAsync(HttpClient client, string prompt, object[]? images = null)
    {
        var response = await StartAsync(client, new { prompt, images });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await ApiV1World.JsonAsync(response);
        return (ApiV1World.Id(body.GetProperty("chat")), ApiV1World.Id(body.GetProperty("message")));
    }

    [Fact]
    public async Task A_token_opens_v1_and_an_anonymous_caller_is_refused()
    {
        using var w = await ApiV1World.CreateAsync();
        using var anonymous = w.Factory.CreateClient(new() { HandleCookies = false });
        foreach (var path in new[] { "/api/v1/me", "/api/v1/chats", $"/api/v1/messages/{Guid.NewGuid()}/wait", "/api/v1/openapi.json" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/chats", new { prompt = "a cat" })).StatusCode);

        var (member, api) = await w.MemberAsync("sara", credits: 50);
        var me = await api.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal((member, "sara", "member", 50), (ApiV1World.Id(me), me.GetProperty("username").GetString(), me.GetProperty("role").GetString(), me.GetProperty("credits").GetInt32()));
        Assert.Equal((JsonValueKind.Null, 0, 4), (me.GetProperty("dailyQuota").ValueKind, me.GetProperty("dailyUsed").GetInt32(), me.GetProperty("limits").GetProperty("maxImages").GetInt32()));

        var (_, owner) = await w.OwnerAsync();
        me = await owner.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(("owner", JsonValueKind.Null), (me.GetProperty("role").GetString(), me.GetProperty("credits").ValueKind));

        // Every miss under v1 speaks in the same shape, a path nobody wrote included.
        var unknown = await api.GetAsync("/api/v1/nothing/here");
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), (unknown.StatusCode, await ApiV1World.ErrorAsync(unknown)));
    }

    [Fact]
    public async Task A_chat_started_with_a_prompt_queues_it_in_a_new_project_named_by_it()
    {
        using var w = await ApiV1World.CreateAsync();
        var (member, api) = await w.MemberAsync("sara");
        var response = await StartAsync(api, new { prompt = "a lighthouse at dusk" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiV1World.JsonAsync(response);
        var chat = body.GetProperty("chat"); var message = body.GetProperty("message");
        Assert.Equal($"/api/v1/chats/{ApiV1World.Id(chat)}", response.Headers.Location!.OriginalString);
        Assert.Equal(("a lighthouse at dusk", true, 1), (chat.GetProperty("title").GetString(), chat.GetProperty("busy").GetBoolean(), chat.GetProperty("messageCount").GetInt32()));
        Assert.Equal(("queued", "Queued", ApiV1World.Id(chat), "a lighthouse at dusk"), (message.GetProperty("status").GetString(), message.GetProperty("stage").GetString(), message.GetProperty("chatId").GetGuid(), message.GetProperty("prompt").GetString()));
        // Waiting work says where it stands; the dispatcher is not running here, so how far back is not known yet.
        Assert.Equal((JsonValueKind.Object, JsonValueKind.Null), (message.GetProperty("queue").ValueKind, message.GetProperty("imageUrl").ValueKind));
        Assert.EndsWith("Z", message.GetProperty("createdAt").GetString());

        var row = await w.DbAsync(db => db.Generations.Include(g => g.Project).SingleAsync(g => g.Id == ApiV1World.Id(message)));
        Assert.Equal((member, "a lighthouse at dusk", Operation.Generate), (row.Project.UserId, row.Project.Title, row.Operation));

        // Without a prompt a chat is only a name, and there is no message to speak of.
        var named = await ApiV1World.JsonAsync(await StartAsync(api, new { title = "Posters" }));
        Assert.False(named.TryGetProperty("message", out _));
        Assert.Equal(("Posters", 0, false), (named.GetProperty("chat").GetProperty("title").GetString(), named.GetProperty("chat").GetProperty("messageCount").GetInt32(), named.GetProperty("chat").GetProperty("busy").GetBoolean()));

        foreach (var (bad, code) in new (object, string)[] { (new { }, "InvalidTitle"), (new { prompt = "   " }, "InvalidPrompt"), (new { prompt = new string('x', 12001) }, "InvalidPrompt"),
                                                             (new { prompt = "a cat", provider = "Midjourney" }, "InvalidProvider"), (new { prompt = "a cat", provider = "1" }, "InvalidProvider"),
                                                             (new { title = new string('t', 161), prompt = "a cat" }, "InvalidTitle"), (new { prompt = 5 }, "InvalidRequest") })
        {
            var refused = await StartAsync(api, bad);
            Assert.Equal((HttpStatusCode.BadRequest, code), (refused.StatusCode, await ApiV1World.ErrorAsync(refused)));
        }
        Assert.Equal(2, await w.DbAsync(db => db.Projects.CountAsync(p => p.UserId == member)));
    }

    [Fact]
    public async Task A_follow_up_continues_the_same_conversation_without_sending_the_picture_again()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (chat, first) = await StartedAsync(api, "a red bicycle", [new { base64 = "data:image/png;base64," + ApiV1World.PngBase64 }]);
        Assert.Single(await w.DbAsync(db => db.GenerationInputs.Where(i => i.GenerationId == first).ToListAsync()));
        await w.FinishAsync(first);

        var sent = await api.PostAsJsonAsync($"/api/v1/chats/{chat}/messages", new { prompt = "make it blue" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var message = (await ApiV1World.JsonAsync(sent)).GetProperty("message");
        var next = ApiV1World.Id(message);
        Assert.Equal($"/api/v1/messages/{next}", sent.Headers.Location!.OriginalString);
        Assert.Equal(chat, message.GetProperty("chatId").GetGuid());
        var row = await w.DbAsync(db => db.Generations.Include(g => g.Inputs).SingleAsync(g => g.Id == next));
        Assert.Equal((Operation.Reply, first, w.Account, chat), (row.Operation, row.ParentGenerationId!.Value, row.AccountId!.Value, row.ProjectId));
        Assert.Empty(row.Inputs);

        var detail = await api.GetFromJsonAsync<JsonElement>($"/api/v1/chats/{chat}");
        Assert.Equal(new[] { first, next }, detail.GetProperty("messages").EnumerateArray().Select(ApiV1World.Id).ToArray());
        var done = detail.GetProperty("messages")[0];
        Assert.Equal(("completed", $"/api/v1/messages/{first}/image", JsonValueKind.Null), (done.GetProperty("status").GetString(), done.GetProperty("imageUrl").GetString(), done.GetProperty("queue").ValueKind));
        Assert.Equal($"/api/v1/messages/{first}/image", detail.GetProperty("chat").GetProperty("latestImageUrl").GetString());
        var image = await api.GetAsync($"/api/v1/messages/{first}/image");
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ApiV1World.Png, await image.Content.ReadAsByteArrayAsync());
        var none = await api.GetAsync($"/api/v1/messages/{next}/image");
        Assert.Equal((HttpStatusCode.NotFound, "NoImage"), (none.StatusCode, await ApiV1World.ErrorAsync(none)));
    }

    [Fact]
    public async Task A_chat_takes_one_message_at_a_time_and_is_not_deleted_mid_message()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (chat, first) = await StartedAsync(api, "a red bicycle");
        var busy = await api.PostAsJsonAsync($"/api/v1/chats/{chat}/messages", new { prompt = "too soon", images = new[] { new { base64 = ApiV1World.PngBase64 } } });
        Assert.Equal((HttpStatusCode.Conflict, "ProjectBusy"), (busy.StatusCode, await ApiV1World.ErrorAsync(busy)));
        // The picture that came with the refused message is not left behind for the sweeper.
        Assert.Equal(0, await w.DbAsync(db => db.Uploads.CountAsync()));
        var delete = await api.DeleteAsync($"/api/v1/chats/{chat}");
        Assert.Equal((HttpStatusCode.Conflict, "ProjectBusy"), (delete.StatusCode, await ApiV1World.ErrorAsync(delete)));

        await w.FinishAsync(first, picture: false);
        Assert.Equal(HttpStatusCode.Accepted, (await api.PostAsJsonAsync($"/api/v1/chats/{chat}/messages", new { prompt = "flat, please" })).StatusCode);
        var missing = await api.PostAsJsonAsync($"/api/v1/chats/{chat}/messages", new { images = Array.Empty<object>() });
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidPrompt"), (missing.StatusCode, await ApiV1World.ErrorAsync(missing)));
        var (other, _) = await StartedAsync(api, "a quiet harbour");
        await w.FinishAsync(await w.DbAsync(db => db.Generations.Where(g => g.ProjectId == other).Select(g => g.Id).SingleAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/chats/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/chats/{other}")).StatusCode);
    }

    [Fact]
    public async Task Base64_pictures_become_the_callers_uploads_and_bad_ones_leave_nothing_behind()
    {
        using var w = await ApiV1World.CreateAsync();
        var (member, api) = await w.MemberAsync("sara");
        var (stranger, _) = await w.MemberAsync("omid");
        // The bytes decide what a file is; the media type a data: URL claims is not believed.
        var (_, message) = await StartedAsync(api, "a sofa in this fabric", [new { base64 = ApiV1World.PngBase64 }, new { base64 = "data:image/jpeg;base64," + ApiV1World.PngBase64 }]);
        var uploads = await w.DbAsync(db => db.Uploads.AsNoTracking().ToListAsync());
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, u => Assert.Equal((member, "image/png", (long)ApiV1World.Png.Length), (u.UserId, u.ContentType, u.Bytes)));
        Assert.Equal(uploads.Select(u => u.Id).Order(), (await w.DbAsync(db => db.GenerationInputs.Where(i => i.GenerationId == message).Select(i => i.UploadId!.Value).ToListAsync())).Order());

        var theirs = await w.DbAsync(async db => { var u = new Upload { UserId = stranger, Path = $"uploads/{stranger}/x.png", ContentType = "image/png" }; db.Uploads.Add(u); await db.SaveChangesAsync(); return u.Id; });
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is a text file, not a picture"));
        foreach (var (images, status, code) in new (object[], HttpStatusCode, string)[]
        {
            ([new { base64 = text }], HttpStatusCode.BadRequest, "InvalidImage"),
            ([new { base64 = "!!! not base64 !!!" }], HttpStatusCode.BadRequest, "InvalidBase64"),
            ([new { base64 = "data:image/png," + ApiV1World.PngBase64 }], HttpStatusCode.BadRequest, "InvalidBase64"),
            ([new { base64 = ApiV1World.PngBase64, uploadId = uploads[0].Id }], HttpStatusCode.BadRequest, "InvalidImageInput"),
            ([new { }], HttpStatusCode.BadRequest, "InvalidImageInput"),
            ([.. Enumerable.Repeat(new { base64 = ApiV1World.PngBase64 }, 5)], HttpStatusCode.Conflict, "TooManyInputs"),
            // Somebody else's upload is a miss, and the good picture sent beside it is taken back with the refusal.
            ([new { base64 = ApiV1World.PngBase64 }, new { uploadId = theirs }], HttpStatusCode.NotFound, "NotFound"),
        })
        {
            var refused = await StartAsync(api, new { prompt = "again", images });
            Assert.Equal((status, code), (refused.StatusCode, await ApiV1World.ErrorAsync(refused)));
        }
        Assert.Equal((3, 1), await w.DbAsync(async db => (await db.Uploads.CountAsync(), await db.Projects.CountAsync())));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(w.Factory.Root, "images", "uploads", member.ToString())).Length);
    }

    [Fact]
    public async Task Another_users_chat_and_message_are_a_miss_everywhere()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, sara) = await w.MemberAsync("sara");
        var (_, omid) = await w.MemberAsync("omid");
        var (chat, message) = await StartedAsync(sara, "a lighthouse at dusk");
        foreach (var response in new[]
        {
            await omid.GetAsync($"/api/v1/chats/{chat}"), await omid.GetAsync($"/api/v1/messages/{message}"), await omid.GetAsync($"/api/v1/messages/{message}/wait?timeout=1"),
            await omid.GetAsync($"/api/v1/messages/{message}/events"), await omid.GetAsync($"/api/v1/messages/{message}/image"), await omid.PostAsync($"/api/v1/messages/{message}/cancel", null),
            await omid.DeleteAsync($"/api/v1/chats/{chat}"), await omid.PostAsJsonAsync($"/api/v1/chats/{chat}/messages", new { prompt = "hijack" }),
        })
            Assert.Equal((HttpStatusCode.NotFound, "NotFound"), (response.StatusCode, await ApiV1World.ErrorAsync(response)));
        Assert.Empty((await omid.GetFromJsonAsync<JsonElement>("/api/v1/chats")).GetProperty("chats").EnumerateArray());
        // Nothing the stranger tried reached the work itself.
        var still = await sara.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message}");
        Assert.Equal("queued", still.GetProperty("status").GetString());
        Assert.Equal(1, (await sara.GetFromJsonAsync<JsonElement>($"/api/v1/chats/{chat}")).GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Chats_are_listed_by_latest_activity_a_page_at_a_time()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        foreach (var title in new[] { "one", "two", "three" }) Assert.Equal(HttpStatusCode.Created, (await StartAsync(api, new { title })).StatusCode);
        var first = await api.GetFromJsonAsync<JsonElement>("/api/v1/chats?limit=2");
        Assert.Equal(new[] { "three", "two" }, first.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
        var before = first.GetProperty("nextBefore").GetString()!;
        var second = await api.GetFromJsonAsync<JsonElement>($"/api/v1/chats?limit=2&before={Uri.EscapeDataString(before)}");
        Assert.Equal(new[] { "one" }, second.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
        var bad = await api.GetAsync("/api/v1/chats?before=yesterday");
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidRequest"), (bad.StatusCode, await ApiV1World.ErrorAsync(bad)));
    }

    [Fact]
    public async Task Cancel_stops_a_queued_message_and_leaves_a_finished_one_as_it_ended()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (chat, message) = await StartedAsync(api, "a lighthouse at dusk");
        var cancelled = await api.PostAsync($"/api/v1/messages/{message}/cancel", null);
        Assert.Equal((HttpStatusCode.OK, "cancelled"), (cancelled.StatusCode, (await ApiV1World.JsonAsync(cancelled)).GetProperty("status").GetString()));
        Assert.False((await api.GetFromJsonAsync<JsonElement>($"/api/v1/chats/{chat}")).GetProperty("chat").GetProperty("busy").GetBoolean());
        Assert.Equal("cancelled", (await ApiV1World.JsonAsync(await api.PostAsync($"/api/v1/messages/{message}/cancel", null))).GetProperty("status").GetString());

        var (_, done) = await StartedAsync(api, "a quiet harbour");
        await w.FinishAsync(done);
        Assert.Equal("completed", (await ApiV1World.JsonAsync(await api.PostAsync($"/api/v1/messages/{done}/cancel", null))).GetProperty("status").GetString());
    }

    private const string Secret = "SECRET-TEMPLATE-PROMPT: warm film grain, golden hour, 85mm portrait of the couple";
    [Fact]
    public async Task Templates_are_chosen_and_used_through_v1_and_their_prompt_is_in_no_answer()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, owner) = await w.OwnerAsync();
        var category = await ApiV1World.JsonAsync(await owner.PostAsJsonAsync("/api/templates/categories", new { name = "Couples", order = 1 }));
        var created = await owner.PostAsJsonAsync("/api/templates", new { categoryId = ApiV1World.Id(category), title = "Golden hour couple", instructions = "One clear photo of both of you.", prompt = Secret, provider = "ChatGPT", minInputs = 1, maxInputs = 2, sendSample = false, isActive = true, order = 1 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var template = ApiV1World.Id(await ApiV1World.JsonAsync(created));
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(ApiV1World.Png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "sample.png");
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/templates/{template}/sample", form)).StatusCode);
        }

        var (_, api) = await w.MemberAsync("sara");
        var raw = await api.GetStringAsync("/api/v1/templates");
        Assert.DoesNotContain("SECRET", raw);
        var listed = Assert.Single(JsonDocument.Parse(raw).RootElement.GetProperty("templates").EnumerateArray());
        // The price is the seeded one for ChatGPT; a member sees it, the owner would see null.
        Assert.Equal((template, "Golden hour couple", "Couples", 1, 2, 10), (ApiV1World.Id(listed), listed.GetProperty("title").GetString(), listed.GetProperty("category").GetString(),
                     listed.GetProperty("minImages").GetInt32(), listed.GetProperty("maxImages").GetInt32(), listed.GetProperty("price").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(listed.GetProperty("sampleUrl").GetString())).StatusCode);

        var tooFew = await api.PostAsJsonAsync($"/api/v1/templates/{template}/use", new { note = "smile" });
        Assert.Equal((HttpStatusCode.Conflict, "TemplateInputs"), (tooFew.StatusCode, await ApiV1World.ErrorAsync(tooFew)));
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsJsonAsync($"/api/v1/templates/{Guid.NewGuid()}/use", new { images = new[] { new { base64 = ApiV1World.PngBase64 } } })).StatusCode);
        var used = await api.PostAsJsonAsync($"/api/v1/templates/{template}/use", new { note = "make us smile", images = new[] { new { base64 = ApiV1World.PngBase64 } } });
        Assert.Equal(HttpStatusCode.Accepted, used.StatusCode);
        var usedRaw = await used.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", usedRaw);
        var body = JsonDocument.Parse(usedRaw).RootElement;
        var message = ApiV1World.Id(body.GetProperty("message"));
        Assert.Equal(("Golden hour couple", "Golden hour couple", "make us smile"), (body.GetProperty("chat").GetProperty("title").GetString(),
                     body.GetProperty("message").GetProperty("templateTitle").GetString(), body.GetProperty("message").GetProperty("prompt").GetString()));
        // The prompt did go with the work; it is only never handed back.
        Assert.Equal(Secret, await w.DbAsync(db => db.Generations.Where(g => g.Id == message).Select(g => g.HiddenPrompt).SingleAsync()));

        await w.FinishAsync(message);
        var chat = ApiV1World.Id(body.GetProperty("chat"));
        foreach (var path in new[] { $"/api/v1/messages/{message}", $"/api/v1/messages/{message}/wait?timeout=1", $"/api/v1/chats/{chat}", "/api/v1/chats", $"/api/v1/messages/{message}/events" })
            Assert.DoesNotContain("SECRET", await api.GetStringAsync(path));
        Assert.DoesNotContain("SECRET", await (await api.PostAsync($"/api/v1/messages/{message}/cancel", null)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_v1_reference_is_open_to_members_and_describes_v1_alone()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var response = await api.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await ApiV1World.JsonAsync(response);
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(paths, p => Assert.StartsWith("/api/v1/", p));
        foreach (var path in new[] { "/api/v1/me", "/api/v1/chats", "/api/v1/chats/{id}/messages", "/api/v1/messages/{id}/wait", "/api/v1/messages/{id}/events", "/api/v1/templates/{id}/use" })
            Assert.Contains(path, paths);
        Assert.DoesNotContain("/api/v1/openapi.json", paths);
        var schemas = document.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("MessageDto", out _));
        // The owner's side is not described by the back door: its request shapes are not reachable from v1.
        Assert.False(schemas.TryGetProperty("TemplateRequest", out _));
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }
}
