using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>The web's side of one project, one conversation: inside a project there is only "the next message", and a new chat, including one
/// branched from a picture, is a new project begun by its first message.</summary>
public class ChatWebTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private static async Task<string> CsrfAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString()!;
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username = "owner", string password = AppFactory.Key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        return client;
    }
    private static async Task<Guid> AccountAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = new ProviderAccount { Label = "GPT1", ProfileDirectory = "gpt1" };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        return account.Id;
    }
    /// <summary>What a finished run leaves behind: a picture, the conversation it lives in, and the account holding that conversation.</summary>
    private static async Task FinishAsync(AppFactory factory, Guid generation, Guid account)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = await db.Generations.SingleAsync(x => x.Id == generation);
        (g.Status, g.ConversationUrl, g.AccountId) = (RunStatus.Completed, "https://chatgpt.com/c/one", account);
        g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
        await db.SaveChangesAsync();
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static Task<HttpResponseMessage> MessageAsync(HttpClient client, Guid project, string prompt) => client.PostAsJsonAsync($"/api/projects/{project}/messages", new { prompt });
    private static async Task<(Guid Project, Guid Picture)> PictureAsync(AppFactory factory, HttpClient client, Guid account)
    {
        var started = await JsonAsync(await client.PostAsJsonAsync("/api/projects/start", new { prompt = "a red bicycle" }), HttpStatusCode.Created);
        var picture = started.GetProperty("generation").GetProperty("id").GetGuid();
        await FinishAsync(factory, picture, account);
        return (started.GetProperty("project").GetProperty("id").GetGuid(), picture);
    }

    [Fact]
    public async Task A_message_continues_the_chat_and_waits_for_the_answer_before_the_next()
    {
        using var factory = new AppFactory();
        var account = await AccountAsync(factory);
        using var client = await SignInAsync(factory);
        var project = (await JsonAsync(await client.PostAsJsonAsync("/api/projects", new { title = "a chat" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var first = await JsonAsync(await MessageAsync(client, project, "a red bicycle"), HttpStatusCode.Accepted);
        Assert.Equal("Generate", first.GetProperty("operation").GetString());
        // The composer is told to wait, but a second tab or a quick double send still meets a stable, explainable refusal.
        Assert.Equal("ProjectBusy", (await JsonAsync(await MessageAsync(client, project, "too soon"), HttpStatusCode.Conflict)).GetProperty("error").GetString());

        await FinishAsync(factory, first.GetProperty("id").GetGuid(), account);
        var next = await JsonAsync(await MessageAsync(client, project, "make it blue"), HttpStatusCode.Accepted);
        Assert.Equal(("Reply", first.GetProperty("id").GetGuid()), (next.GetProperty("operation").GetString(), next.GetProperty("parentGenerationId").GetGuid()));
        Assert.Empty(next.GetProperty("inputs").EnumerateArray());
        Assert.Equal(project, next.GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task A_new_chat_is_a_project_named_by_its_first_message_in_the_shape_the_list_uses()
    {
        using var factory = new AppFactory();
        await AccountAsync(factory);
        using var client = await SignInAsync(factory);
        var started = await JsonAsync(await client.PostAsJsonAsync("/api/projects/start", new { prompt = "  a lighthouse\nat dusk " }), HttpStatusCode.Created);
        var project = started.GetProperty("project");
        Assert.Equal("a lighthouse at dusk", project.GetProperty("title").GetString());
        Assert.Equal(project.GetProperty("id").GetGuid(), started.GetProperty("generation").GetProperty("projectId").GetGuid());
        Assert.Equal("Generate", started.GetProperty("generation").GetProperty("operation").GetString());
        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray().Single();
        Assert.Equal(listed.EnumerateObject().Select(p => p.Name), project.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, project.GetProperty("generationCount").GetInt32());

        var named = await JsonAsync(await client.PostAsJsonAsync("/api/projects/start", new { title = "Posters", prompt = "a poster" }), HttpStatusCode.Created);
        Assert.Equal("Posters", named.GetProperty("project").GetProperty("title").GetString());
        // A message the server refuses leaves no empty chat behind.
        Assert.Equal("InvalidInput", (await JsonAsync(await client.PostAsJsonAsync("/api/projects/start", new { prompt = "with nothing attached", inputs = new[] { new { } } }), HttpStatusCode.Conflict)).GetProperty("error").GetString());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/projects")).GetArrayLength());
    }

    [Fact]
    public async Task Branching_a_picture_starts_a_new_chat_whose_first_message_carries_it()
    {
        using var factory = new AppFactory();
        var account = await AccountAsync(factory);
        using var client = await SignInAsync(factory);
        var (source, picture) = await PictureAsync(factory, client, account);

        var branched = await JsonAsync(await client.PostAsJsonAsync("/api/projects/start", new { title = "a red bicycle ↳ at night", prompt = "at night", inputs = new[] { new { generationId = picture } } }), HttpStatusCode.Created);
        var project = branched.GetProperty("project").GetProperty("id").GetGuid();
        var first = branched.GetProperty("generation");
        Assert.NotEqual(source, project);
        Assert.Equal("a red bicycle ↳ at night", branched.GetProperty("project").GetProperty("title").GetString());
        Assert.Equal("Generate", first.GetProperty("operation").GetString());
        Assert.Equal(picture, Assert.Single(first.GetProperty("inputs").EnumerateArray()).GetProperty("generationId").GetGuid());
        // No lineage across chats: a parent in another project would stop that project from ever being deleted.
        Assert.Equal(JsonValueKind.Null, first.GetProperty("parentGenerationId").ValueKind);
        // The chat it came from is left exactly as it was, and can still be deleted.
        Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/projects/{source}/generations")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/projects/{source}")).StatusCode);
    }

    [Fact]
    public async Task Another_users_chat_and_picture_are_a_miss()
    {
        using var factory = new AppFactory();
        var account = await AccountAsync(factory);
        using var owner = await SignInAsync(factory);
        var (project, picture) = await PictureAsync(factory, owner, account);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = new AppUser { Username = "ann", NormalizedUsername = "ann", Role = UserRole.Member };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, "ann-password");
            db.Users.Add(user); db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = 1000, Kind = CreditKind.Grant });
            await db.SaveChangesAsync();
        }
        using var ann = await SignInAsync(factory, "ann", "ann-password");

        Assert.Equal(HttpStatusCode.NotFound, (await MessageAsync(ann, project, "make it blue")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsJsonAsync("/api/projects/start", new { prompt = "mine now", inputs = new[] { new { generationId = picture } } })).StatusCode);
        Assert.Equal(0, (await ann.GetFromJsonAsync<JsonElement>("/api/projects")).GetArrayLength());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/generations")).EnumerateArray());
    }
}
