using System.Net;
using System.Net.Http.Headers;
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
/// <summary>The owner curates templates: a sample to choose by and a prompt only the owner reads. A member picks one, sends their photos and perhaps a
/// note, and gets a new project named after it. The prompt is the owner's work, so no path a member can reach may carry it.</summary>
public class TemplateTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const string Secret = "SECRET-TEMPLATE-PROMPT: warm film grain, golden hour, 85mm portrait of the couple";
    private const string MemberPassword = "tester-password-1";

    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        async Task<string> Csrf() => (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
        return client;
    }
    private static async Task<Guid> MemberAsync(AppFactory factory, int credits)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
        member.PasswordHash = new PasswordService().Hash(member, MemberPassword);
        db.Users.Add(member);
        if (credits > 0) db.CreditEntries.Add(new CreditEntry { UserId = member.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return member.Id;
    }
    private static async Task<Guid> UploadAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "me.png");
        var response = await client.PostAsync("/api/uploads", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    /// <summary>A category and a template with a sample, made through the owner's own endpoints.</summary>
    private static async Task<Guid> TemplateAsync(HttpClient owner, bool sendSample = false, int min = 1, int max = 2, bool active = true, bool sample = true)
    {
        var category = await JsonAsync(await owner.PostAsJsonAsync("/api/templates/categories", new { name = "Couples", order = 1 }));
        var created = await owner.PostAsJsonAsync("/api/templates", new
        {
            categoryId = category.GetProperty("id").GetGuid(), title = "Golden hour couple", instructions = "One clear photo of both of you.", prompt = Secret,
            provider = "ChatGPT", minInputs = min, maxInputs = max, sendSample, isActive = active, order = 1
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        if (sample)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "sample.png");
            var uploaded = await owner.PostAsync($"/api/templates/{id}/sample", form);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            Assert.Equal("ready", (await JsonAsync(uploaded)).GetProperty("sampleState").GetString());
        }
        return id;
    }

    [Fact]
    public void The_ai_gets_the_template_prompt_then_the_persons_note()
    {
        Assert.Equal("hidden\n\nnote", TemplateService.PromptFor(new Generation { HiddenPrompt = "hidden", Prompt = "note" }));
        Assert.Equal("hidden", TemplateService.PromptFor(new Generation { HiddenPrompt = "hidden", Prompt = "" }));
        Assert.Equal("plain", TemplateService.PromptFor(new Generation { Prompt = "plain" }));
    }

    [Fact]
    public async Task A_member_uses_a_template_and_the_prompt_never_reaches_them()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var template = await TemplateAsync(owner);
        await MemberAsync(factory, credits: 100);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);

        var listing = await tester.GetStringAsync("/api/templates");
        Assert.DoesNotContain("SECRET-TEMPLATE-PROMPT", listing);
        var shown = JsonDocument.Parse(listing).RootElement[0].GetProperty("templates")[0];
        Assert.Equal(("Golden hour couple", 10), (shown.GetProperty("title").GetString(), shown.GetProperty("price").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.GetAsync("/api/templates/manage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tester.GetAsync($"/api/templates/{template}/sample")).StatusCode);

        var used = await tester.PostAsJsonAsync($"/api/templates/{template}/use", new { note = "blue background please", inputs = new[] { new { uploadId = await UploadAsync(tester) } } });
        Assert.Equal(HttpStatusCode.Accepted, used.StatusCode);
        var body = await used.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-TEMPLATE-PROMPT", body);
        var result = JsonDocument.Parse(body).RootElement;
        var generation = result.GetProperty("generation");
        Assert.Equal(("blue background please", "Golden hour couple"), (generation.GetProperty("prompt").GetString(), generation.GetProperty("templateTitle").GetString()));
        var project = result.GetProperty("projectId").GetGuid();
        Assert.DoesNotContain("SECRET-TEMPLATE-PROMPT", await tester.GetStringAsync($"/api/projects/{project}/generations"));
        Assert.DoesNotContain("SECRET-TEMPLATE-PROMPT", await tester.GetStringAsync("/api/projects"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Generations.AsNoTracking().Include(g => g.Project).SingleAsync(g => g.Id == generation.GetProperty("id").GetGuid());
        Assert.Equal(Secret + "\n\nblue background please", TemplateService.PromptFor(row));
        Assert.Equal(("Golden hour couple", template, 10), (row.Project.Title, row.TemplateId!.Value, row.CreditCost));
    }

    [Fact]
    public async Task Too_few_or_too_many_photos_are_refused_and_leave_no_empty_project()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var template = await TemplateAsync(owner, min: 1, max: 1);
        await MemberAsync(factory, credits: 100);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);
        var none = await tester.PostAsJsonAsync($"/api/templates/{template}/use", new { note = (string?)null, inputs = Array.Empty<object>() });
        var two = await tester.PostAsJsonAsync($"/api/templates/{template}/use", new { inputs = new[] { new { uploadId = await UploadAsync(tester) }, new { uploadId = await UploadAsync(tester) } } });
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (none.StatusCode, two.StatusCode));
        Assert.Equal("TemplateInputs", (await JsonAsync(none)).GetProperty("error").GetString());
        Assert.Equal(0, (await tester.GetFromJsonAsync<JsonElement>("/api/projects")).GetArrayLength());
    }

    [Fact]
    public async Task Without_enough_credits_nothing_is_made_and_no_project_is_left()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var template = await TemplateAsync(owner);
        await MemberAsync(factory, credits: 0);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);
        var refused = await tester.PostAsJsonAsync($"/api/templates/{template}/use", new { inputs = new[] { new { uploadId = await UploadAsync(tester) } } });
        Assert.Equal("InsufficientCredits", (await JsonAsync(refused)).GetProperty("error").GetString());
        Assert.Equal(0, (await tester.GetFromJsonAsync<JsonElement>("/api/projects")).GetArrayLength());
    }

    [Fact]
    public async Task The_sample_goes_along_as_a_reference_only_when_the_template_says_so()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var withSample = await TemplateAsync(owner, sendSample: true);
        var without = await TemplateAsync(owner, sendSample: false);
        await MemberAsync(factory, credits: 100);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);
        async Task<int> InputsAsync(Guid template) =>
            (await JsonAsync(await tester.PostAsJsonAsync($"/api/templates/{template}/use", new { inputs = new[] { new { uploadId = await UploadAsync(tester) } } })))
                .GetProperty("generation").GetProperty("inputs").GetArrayLength();
        Assert.Equal((2, 1), (await InputsAsync(withSample), await InputsAsync(without)));
    }

    [Fact]
    public async Task Only_active_templates_with_a_sample_are_offered()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var inactive = await TemplateAsync(owner, active: false);
        await TemplateAsync(owner, sample: false);
        await MemberAsync(factory, credits: 100);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);
        Assert.Equal(0, (await tester.GetFromJsonAsync<JsonElement>("/api/templates")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await tester.GetAsync($"/api/templates/{inactive}/sample")).StatusCode);
        var refused = await tester.PostAsJsonAsync($"/api/templates/{inactive}/use", new { inputs = new[] { new { uploadId = await UploadAsync(tester) } } });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonElement>("/api/templates/manage")).GetProperty("templates").GetArrayLength());
    }

    [Fact]
    public async Task A_sample_drawn_from_the_prompt_becomes_the_templates_own_once_it_is_finished()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var template = await TemplateAsync(owner, sample: false);
        var drawn = await JsonAsync(await owner.PostAsJsonAsync($"/api/templates/{template}/sample/generate", new { inputs = Array.Empty<object>() }));
        async Task<JsonElement> StateAsync() => (await owner.GetFromJsonAsync<JsonElement>("/api/templates/manage")).GetProperty("templates")[0];
        Assert.Equal("generating", (await StateAsync()).GetProperty("sampleState").GetString());

        // The worker finishing the drawing, as it would.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var g = await db.Generations.SingleAsync(x => x.Id == drawn.GetProperty("generationId").GetGuid());
            Assert.Equal(Secret, g.Prompt);
            g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
            g.Status = RunStatus.Completed;
            await db.SaveChangesAsync();
        }
        var ready = await StateAsync();
        Assert.Equal("ready", ready.GetProperty("sampleState").GetString());
        // Copied, not borrowed: deleting the project it was drawn in leaves the sample in place.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{drawn.GetProperty("projectId").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(ready.GetProperty("sampleUrl").GetString())).StatusCode);
    }

    [Fact]
    public async Task A_drawing_that_did_not_produce_a_picture_says_so()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var template = await TemplateAsync(owner, sample: false);
        var drawn = await JsonAsync(await owner.PostAsJsonAsync($"/api/templates/{template}/sample/generate", new { inputs = Array.Empty<object>() }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Generations.SingleAsync(x => x.Id == drawn.GetProperty("generationId").GetGuid())).Status = RunStatus.Answered;
            await db.SaveChangesAsync();
        }
        Assert.Equal("failed", (await owner.GetFromJsonAsync<JsonElement>("/api/templates/manage")).GetProperty("templates")[0].GetProperty("sampleState").GetString());
    }

    [Fact]
    public async Task A_template_the_rules_do_not_allow_is_refused()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var category = (await JsonAsync(await owner.PostAsJsonAsync("/api/templates/categories", new { name = "Products", order = 0 }))).GetProperty("id").GetGuid();
        object Body(string title = "Shoe", string prompt = "studio product shot", int min = 1, int max = 1, bool sendSample = false, Guid? cat = null) =>
            new { categoryId = cat ?? category, title, instructions = (string?)null, prompt, provider = "ChatGPT", minInputs = min, maxInputs = max, sendSample, isActive = true, order = 0 };
        foreach (var bad in new[] { Body(title: ""), Body(prompt: ""), Body(min: 2, max: 1), Body(max: 5), Body(max: 4, sendSample: true), Body(cat: Guid.NewGuid()) })
        {
            var refused = await owner.PostAsJsonAsync("/api/templates", bad);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("InvalidTemplate", (await JsonAsync(refused)).GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/templates", Body(max: 3, sendSample: true))).StatusCode);
        var busy = await owner.DeleteAsync($"/api/templates/categories/{category}");
        Assert.Equal("CategoryNotEmpty", (await JsonAsync(busy)).GetProperty("error").GetString());
    }
}
