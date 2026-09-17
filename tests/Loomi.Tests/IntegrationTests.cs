using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Loomi.Tests;
public class AppFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "loomi-test-" + Guid.NewGuid());
    public const string Key = "integration-test-access-key-with-32-characters";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Security:AccessKey", Key);
        builder.UseSetting("Storage:Root", Root);
        builder.ConfigureServices(services => { services.RemoveAll<IHostedService>(); Unpooled(services, Root); });
    }
    /// <summary>Every factory gets its own unpooled connections. Test classes run side by side and SqliteConnection.ClearAllPools is process-wide, so one
    /// factory tidying up after itself was closing connections another was still using, which surfaced as a disposed sqlite3 handle in an unrelated test.</summary>
    public static void Unpooled(IServiceCollection services, string root)
    {
        foreach (var registered in services.Where(d => d.ServiceType.Name.Contains("DbContextOptions")).ToList()) services.Remove(registered);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(root, "loomi.db")};Foreign Keys=True;Default Timeout=30;Pooling=False"));
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(Root)) try { Directory.Delete(Root, true); } catch (IOException) { /* a file a browser still holds is a temp folder the machine will sweep */ } }
}
public class IntegrationTests
{
    private static async Task Authenticate(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/api/session/login", new { username = "owner", password = AppFactory.Key });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }
    [Fact]
    public async Task Protected_routes_and_csrf_are_enforced()
    {
        using var factory = new AppFactory(); using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/projects", "/api/auth/status", "/desktop/vnc.html", $"/api/generations/{Guid.NewGuid()}/image" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/session/login", new { username = "owner", password = AppFactory.Key })).StatusCode);
        await Authenticate(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/projects")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/accounts", null)).StatusCode);
    }
    [Fact]
    public async Task Queue_preserves_lineage_and_rejects_deletion_until_finished()
    {
        using var factory = new AppFactory(); using var client = factory.CreateClient(); await Authenticate(client);
        Guid projectId, parentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = new ImageProject { Title = "Lineage test" }; db.Projects.Add(project);
            var parent = new Generation { ProjectId = project.Id, Prompt = "Initial", Status = RunStatus.Completed, LocalImagePath = $"projects/{project.Id}/image.png", ConversationUrl = "https://chatgpt.com/c/test" };
            db.Generations.Add(parent); await db.SaveChangesAsync(); projectId = project.Id; parentId = parent.Id;
        }
        var response = await client.PostAsJsonAsync($"/api/generations/{parentId}/branch", new { prompt = "Another direction" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var g = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(parentId, g.GetProperty("parentGenerationId").GetGuid());
        Assert.Equal("Branch", g.GetProperty("operation").GetString());
        Assert.Equal("Queued", g.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/generations/{g.GetProperty("id").GetGuid()}/edit", new { prompt = "Cannot edit pending" })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Generations.Where(x => x.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RunStatus.Completed));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/generations/{parentId}")).StatusCode);
    }
    [Fact]
    public async Task Images_are_private_and_payloads_hide_filesystem_paths()
    {
        using var factory = new AppFactory(); using var client = factory.CreateClient(); await Authenticate(client);
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();
            var p = new ImageProject { Title = "Stored image" }; var g = new Generation { ProjectId = p.Id, Prompt = "Test", Status = RunStatus.Completed }; id = g.Id;
            g.LocalImagePath = await storage.SaveAsync(p.Id, g.Id, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII="), default);
            db.Projects.Add(p); db.Generations.Add(g); await db.SaveChangesAsync();
            Assert.Throws<InvalidOperationException>(() => storage.Resolve("../../profiles/default/Cookies"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(p.Id, Guid.NewGuid(), "<svg>untrusted</svg>"u8.ToArray(), default));
        }
        var payload = await client.GetStringAsync($"/api/generations/{id}"); Assert.DoesNotContain("localImagePath", payload); Assert.DoesNotContain(factory.Root, payload);
        var image = await client.GetAsync($"/api/generations/{id}/image"); Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", image.Headers.CacheControl?.ToString());
        await client.PostAsync("/api/session/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/generations/{id}/image")).StatusCode);
    }
}
