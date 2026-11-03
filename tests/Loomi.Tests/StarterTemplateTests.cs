using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Loomi.Tests;
/// <summary>The starter pack: what it says, adding it to a workspace, and the run that makes its samples. Nothing here reaches a real account: the queue
/// is watched, and a test plays the part of the worker that would have drawn each picture.</summary>
public class StarterTemplateTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const string MemberPassword = "tester-password-1";
    private const string Starter = "/api/templates/starter", Status = "/api/templates/samples/status", Start = "/api/templates/samples/start", Stop = "/api/templates/samples/stop";

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
    private static Task<HttpClient> OwnerAsync(AppFactory factory) => SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
    private static async Task<Guid> MemberAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
        member.PasswordHash = new PasswordService().Hash(member, MemberPassword);
        db.Users.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }
    private static async Task<JsonElement> PostAsync(HttpClient client, string path)
    {
        var response = await client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task WithDbAsync(AppFactory factory, Func<AppDbContext, Task> write)
    {
        using var scope = factory.Services.CreateScope();
        await write(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    /// <summary>A new one each time it matters: the worker holds nothing in memory, which is what lets a restart carry on.</summary>
    private static StarterSampleWorker Worker(AppFactory factory) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<InstanceLease>(), NullLogger<StarterSampleWorker>.Instance);
    /// <summary>Templates in a category of their own, with no sample, in the order they are named.</summary>
    private static async Task<List<Guid>> TemplatesAsync(AppFactory factory, int count, bool active = true, Provider provider = Provider.ChatGPT) => await WithDbAsync(factory, async db =>
    {
        var category = new TemplateCategory { Name = "Batch " + Guid.NewGuid() };
        db.TemplateCategories.Add(category);
        var made = Enumerable.Range(0, count).Select(i => new PromptTemplate { CategoryId = category.Id, Title = $"T{i}", Prompt = $"prompt {i}", Order = i, IsActive = active, Provider = provider }).ToList();
        db.Templates.AddRange(made);
        await db.SaveChangesAsync();
        return made.Select(t => t.Id).ToList();
    });
    private static Task<List<Generation>> DrawingsAsync(AppFactory factory) => WithDbAsync(factory, db => db.Generations.AsNoTracking().OrderBy(g => g.CreatedAt).ToListAsync());
    /// <summary>The worker finishing a drawing, as it would: a picture on disk for one that completed.</summary>
    private static async Task FinishAsync(AppFactory factory, Guid drawing, RunStatus status = RunStatus.Completed, string? error = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = await db.Generations.SingleAsync(x => x.Id == drawing);
        if (status == RunStatus.Completed) g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
        (g.Status, g.ErrorMessage) = (status, error);
        await db.SaveChangesAsync();
    }
    private static async Task<Guid> QueuedForAsync(AppFactory factory, Guid user, RunStatus status = RunStatus.Queued) => await WithDbAsync(factory, async db =>
    {
        var project = new ImageProject { UserId = user, Title = "Someone's work" };
        var g = new Generation { ProjectId = project.Id, UserId = user, Prompt = "their picture", Status = status };
        db.Projects.Add(project); db.Generations.Add(g);
        await db.SaveChangesAsync();
        return g.Id;
    });
    private static bool On(JsonElement status) => status.GetProperty("on").GetBoolean();
    private static int Number(JsonElement status, string name) => status.GetProperty(name).GetInt32();

    // ---- What the pack says ----

    private static readonly string[] Categories = ["پرتره و پروفایل", "دونفره و خانوادگی", "سبک‌های هنری", "عکس محصول", "بهبود و بازسازی عکس", "مناسبت‌ها"];
    private static bool IsProduct(StarterTemplate s) => s.Category == "عکس محصول";

    [Fact]
    public void The_pack_is_within_every_limit_the_app_sets()
    {
        var pack = StarterPack.All;
        Assert.InRange(pack.Count, 24, 30);
        Assert.Equal(pack.Count, pack.Select(s => s.Key).Distinct().Count());
        Assert.Equal(Categories, pack.Select(s => s.Category).Distinct());
        Assert.All(Categories, c => Assert.Contains(pack, s => s.Category == c));
        foreach (var s in pack)
        {
            Assert.Matches("^[a-z0-9-]{1,64}$", s.Key);
            Assert.InRange(s.Title.Length, 1, 40);
            Assert.InRange(s.Instructions.Length, 1, 120);
            Assert.InRange(s.Prompt.Length, 1, 8000);
            Assert.True(0 <= s.MinInputs && s.MinInputs <= s.MaxInputs && s.MaxInputs <= 4, s.Key);
            Assert.InRange(s.Category.Length, 1, 64);
        }
        foreach (var category in pack.GroupBy(s => s.Category)) Assert.Equal(category.Count(), category.Select(s => s.Order).Distinct().Count());
    }

    [Fact]
    public void Every_prompt_works_with_or_without_a_photo_and_never_asks_back()
    {
        foreach (var s in StarterPack.All)
        {
            Assert.InRange(s.Prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 60, 150);
            Assert.Contains("If no photo is attached", s.Prompt);
            Assert.Contains("Create the image right away and do not ask questions", s.Prompt);
            Assert.EndsWith("Follow any extra request below.", s.Prompt);
            Assert.DoesNotMatch(@"[^\x00-\x7F]", s.Prompt);
            if (IsProduct(s)) Assert.Contains("never invent", s.Prompt);
            else { Assert.Contains("recognisably the same", s.Prompt); Assert.Contains("beautify", s.Prompt); }
            // A person's note lands straight after the closing sentence, so it reads as the extra request that sentence asks for.
            Assert.EndsWith("below.\n\nبا پس‌زمینه‌ی آبی", TemplateService.PromptFor(new Generation { HiddenPrompt = s.Prompt, Prompt = "با پس‌زمینه‌ی آبی" }));
        }
    }

    [Fact]
    public void A_sample_with_no_photo_shows_grown_ups_and_invents_no_brand()
    {
        foreach (var s in StarterPack.All)
        {
            var clause = s.Prompt[s.Prompt.IndexOf("If no photo is attached", StringComparison.Ordinal)..];
            if (s.Key == "product-food") Assert.Contains("dish", clause);
            else if (IsProduct(s)) Assert.Contains("blank, unbranded label", clause);
            else Assert.Contains("adult", clause);
        }
        // Only a person's own photo can bring a child into the family portrait.
        var family = StarterPack.All.Single(s => s.Key == "family-warm-studio").Prompt;
        Assert.Contains("children only if the attached photos show them", family);
        Assert.Contains("adults only", family);
    }

    /// <summary>Names of studios, franchises, artists and brands the looks are described instead of named by, in English and in the Persian people would write them.</summary>
    private static readonly string[] Banned =
    [
        "ghibli", "miyazaki", "pixar", "disney", "dreamworks", "marvel", "illumination", "lego", "funko", "simpsons", "wes anderson", "hollywood", "bollywood", "warhol", "lichtenstein", "picasso", "van gogh", "rembrandt",
        "da vinci", "vermeer", "monet", "banksy", "rockwell", "instagram", "linkedin", "facebook", "tiktok", "snapchat", "pinterest", "vogue", "chanel", "dior", "gucci", "rolex", "nike", "adidas", "coca-cola", "starbucks", "iphone",
        "samsung", "canon", "nikon", "sony", "leica", "hasselblad", "kodak", "fujifilm", "portra", "photoshop", "lightroom", "midjourney", "chatgpt", "openai", "dall-e", "gemini", "hafez", "rumi", "khayyam",
        "جیبلی", "میازاکی", "پیکسار", "دیزنی", "دریم‌ورکس", "مارول", "ون‌گوگ", "پیکاسو", "اینستاگرام", "لینکدین", "حافظ",
    ];
    private static readonly string[] Suggestive = ["sexy", "sensual", "seductive", "lingerie", "nude", "naked", "bikini", "cleavage", "erotic", "bedroom", "lying in bed"];
    private static readonly string[] Unwelcome = ["quran", "koran", "bible", "mosque", "church", "prayer", "flag of", "president", "protest", "election", "toddler", "teenager", "schoolgirl", "schoolboy", "little girl", "little boy"];

    [Fact]
    public void No_studio_brand_or_artist_is_named_and_nothing_is_suggestive_political_or_religious()
    {
        foreach (var s in StarterPack.All)
        foreach (var word in Banned.Concat(Suggestive).Concat(Unwelcome))
            Assert.False(Regex.IsMatch($"{s.Title}\n{s.Instructions}\n{s.Prompt}", $@"(?<![\p{{L}}]){Regex.Escape(word)}(?![\p{{L}}])", RegexOptions.IgnoreCase), $"{s.Key} says {word}");
    }

    [Fact]
    public void The_persian_copy_is_written_the_persian_way()
    {
        foreach (var s in StarterPack.All)
        {
            var copy = $"{s.Title}\n{s.Instructions}\n{s.Category}";
            Assert.DoesNotContain('ي', copy);   // Arabic yeh, where Persian writes ی
            Assert.DoesNotContain('ك', copy);   // Arabic kaf, where Persian writes ک
            Assert.DoesNotContain('·', copy);
            Assert.DoesNotMatch("[A-Za-z]", copy);
            // The half-space is what keeps these one word; a plain space or none at all is a typing slip.
            Assert.DoesNotMatch("(^|\\s)(می|نمی) ", copy);
            Assert.DoesNotMatch("(پس زمینه|پسزمینه|سیاه و سفید|رو به رو)", copy);
            Assert.DoesNotMatch("[\\s‌]‌|‌\\s", copy);
        }
    }

    [Fact]
    public void The_passport_style_template_is_plain_and_says_it_is_no_official_document()
    {
        var passport = StarterPack.All.Single(s => s.Key == "fix-passport");
        Assert.Contains("plain, neutral", passport.Prompt);
        Assert.Contains("not an official document", passport.Prompt);
        Assert.Contains("مدرک رسمی نیست", passport.Instructions);
    }

    // ---- Adding it ----

    [Fact]
    public async Task Adding_twice_adds_once()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var count = StarterPack.All.Count;

        var first = await PostAsync(owner, Starter);
        Assert.Equal((Categories.Length, count, 0), (Number(first, "categories"), Number(first, "added"), Number(first, "existing")));
        var second = await PostAsync(owner, Starter);
        Assert.Equal((0, 0, count), (Number(second, "categories"), Number(second, "added"), Number(second, "existing")));

        var (templates, categories) = await WithDbAsync(factory, async db => (await db.Templates.AsNoTracking().ToListAsync(), await db.TemplateCategories.AsNoTracking().OrderBy(c => c.Order).ToListAsync()));
        Assert.Equal(count, templates.Count);
        Assert.Equal(Categories, categories.Select(c => c.Name));
        Assert.All(templates, t =>
        {
            var source = StarterPack.All.Single(s => s.Key == t.StarterKey);
            Assert.Equal((source.Title, source.Instructions, source.Prompt, source.MinInputs, source.MaxInputs, source.Order, categories.Single(c => c.Name == source.Category).Id), (t.Title, t.Instructions, t.Prompt, t.MinInputs, t.MaxInputs, t.Order, t.CategoryId));
            Assert.True(t.IsActive);
            Assert.Null(t.SamplePath);
            Assert.Null(t.SampleGenerationId);
            Assert.False(t.SendSample);
        });
    }

    [Fact]
    public async Task An_edited_template_is_left_alone_and_only_what_is_missing_comes_back()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await PostAsync(owner, Starter);
        var (edited, deleted) = await WithDbAsync(factory, async db => (await db.Templates.AsNoTracking().FirstAsync(t => t.StarterKey == "portrait-headshot"), await db.Templates.AsNoTracking().FirstAsync(t => t.StarterKey == "art-oil")));
        var put = await owner.PutAsJsonAsync($"/api/templates/{edited.Id}", new
        {
            categoryId = edited.CategoryId, title = "My own title", instructions = "My own words", prompt = "my own prompt", provider = "Gemini", minInputs = 0, maxInputs = 2, sendSample = false, isActive = false, order = 99
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/templates/{deleted.Id}")).StatusCode);

        var again = await PostAsync(owner, Starter);
        Assert.Equal((0, 1, StarterPack.All.Count - 1), (Number(again, "categories"), Number(again, "added"), Number(again, "existing")));

        var kept = await WithDbAsync(factory, db => db.Templates.AsNoTracking().FirstAsync(t => t.StarterKey == "portrait-headshot"));
        Assert.Equal(("My own title", "My own words", "my own prompt", Provider.Gemini, false, 99), (kept.Title, kept.Instructions, kept.Prompt, kept.Provider, kept.IsActive, kept.Order));
        Assert.Equal(StarterPack.All.Count, await WithDbAsync(factory, db => db.Templates.CountAsync()));
    }

    [Fact]
    public async Task A_category_of_that_name_is_reused_and_one_the_owner_renamed_is_not_made_again()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var mine = (await (await owner.PostAsJsonAsync("/api/templates/categories", new { name = "مناسبت‌ها", order = 50 })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await PostAsync(owner, Starter);
        Assert.Equal(Categories.Length - 1, Number(first, "categories"));
        Assert.Equal(StarterPack.All.Count(s => s.Category == "مناسبت‌ها"), await WithDbAsync(factory, db => db.Templates.CountAsync(t => t.CategoryId == mine)));
        Assert.True(await WithDbAsync(factory, async db => (await db.TemplateCategories.AsNoTracking().ToListAsync()).Where(c => c.Id != mine).All(c => c.Order > 50)));

        // Everything is in, so pressing it again has no reason to bring the old name back beside the owner's new one.
        await WithDbAsync(factory, async db => { (await db.TemplateCategories.SingleAsync(c => c.Name == "عکس محصول")).Name = "محصولات من"; await db.SaveChangesAsync(); });
        var again = await PostAsync(owner, Starter);
        Assert.Equal(0, Number(again, "categories"));
        Assert.Equal(Categories.Length, await WithDbAsync(factory, db => db.TemplateCategories.CountAsync()));
    }

    [Fact]
    public async Task Only_the_owner_reaches_the_pack_or_the_samples()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await MemberAsync(factory);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsync(Starter, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.GetAsync(Status)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsync(Start, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsync(Stop, null)).StatusCode);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Templates.CountAsync()));
    }

    [Fact]
    public async Task A_template_with_no_sample_stays_out_of_everything_people_see()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await PostAsync(owner, Starter);
        await MemberAsync(factory);
        using var tester = await SignInAsync(factory, "tester", MemberPassword);

        Assert.Equal(0, (await tester.GetFromJsonAsync<JsonElement>("/api/templates")).GetArrayLength());
        Assert.Equal(0, (await tester.GetFromJsonAsync<JsonElement>("/api/v1/templates")).GetProperty("templates").GetArrayLength());
        var any = await WithDbAsync(factory, db => db.Templates.AsNoTracking().FirstAsync(t => t.StarterKey == "occasion-yalda"));
        Assert.Equal(HttpStatusCode.NotFound, (await tester.PostAsJsonAsync($"/api/templates/{any.Id}/use", new { inputs = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tester.GetAsync($"/api/templates/{any.Id}/sample")).StatusCode);
        // The owner still sees all of them, waiting for a sample.
        Assert.All((await owner.GetFromJsonAsync<JsonElement>("/api/templates/manage")).GetProperty("templates").EnumerateArray(), t => Assert.Equal("none", t.GetProperty("sampleState").GetString()));

        // A half-finished run: the one sample that exists is the one template shown, and its prompt does not go with it.
        await TemplateServiceSampleAsync(factory, any.Id);
        var listing = await tester.GetStringAsync("/api/templates");
        var categories = JsonDocument.Parse(listing).RootElement;
        Assert.Equal(1, categories.GetArrayLength());
        Assert.Equal(1, categories[0].GetProperty("templates").GetArrayLength());
        Assert.DoesNotContain("korsi", listing);
    }
    private static async Task TemplateServiceSampleAsync(AppFactory factory, Guid template)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await scope.ServiceProvider.GetRequiredService<TemplateService>().SetSampleAsync(await db.Templates.SingleAsync(t => t.Id == template), Png, default);
        await db.SaveChangesAsync();
    }

    // ---- Making the samples ----

    [Fact]
    public async Task Nothing_happens_while_the_run_is_off()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 2);
        await Worker(factory).StepAsync(default);
        Assert.Empty(await DrawingsAsync(factory));
        var status = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(On(status));
        Assert.Equal(2, Number(status, "withoutSample"));
    }

    [Fact]
    public async Task One_sample_at_a_time_for_the_owner_uncharged_until_none_is_left_and_then_it_turns_itself_off()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 2);
        var started = await PostAsync(owner, Start);
        Assert.True(On(started));
        Assert.Equal((2, 0), (Number(started, "total"), Number(started, "done")));
        var worker = Worker(factory);

        await worker.StepAsync(default);
        await worker.StepAsync(default);
        var first = Assert.Single(await DrawingsAsync(factory));
        Assert.Equal("prompt 0", first.Prompt);
        Assert.Equal(first.Id, (await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SampleGenerationId);
        Assert.Equal(0, first.CreditCost);
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
        Assert.Equal("T0", (await owner.GetFromJsonAsync<JsonElement>(Status)).GetProperty("drawing").GetString());

        await FinishAsync(factory, first.Id);
        await worker.StepAsync(default);
        Assert.Equal(2, (await DrawingsAsync(factory)).Count);
        var midway = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.True(On(midway));
        Assert.Equal((2, 1), (Number(midway, "total"), Number(midway, "done")));
        Assert.NotNull((await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SamplePath);

        await FinishAsync(factory, (await DrawingsAsync(factory)).Single(g => g.Id != first.Id).Id);
        await worker.StepAsync(default);
        var done = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(On(done));
        Assert.Equal(0, Number(done, "withoutSample"));
        Assert.Equal(2, (await DrawingsAsync(factory)).Count);
        Assert.Equal("off", await WithDbAsync(factory, db => db.Settings.AsNoTracking().Where(s => s.Key == StarterService.RunKey).Select(s => s.Value).SingleAsync()));
    }

    [Fact]
    public async Task A_members_work_goes_first_whether_it_waits_or_is_being_drawn()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        var member = await MemberAsync(factory);
        var queued = await QueuedForAsync(factory, member);
        await PostAsync(owner, Start);
        var worker = Worker(factory);

        await worker.StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));
        Assert.Equal("people", (await owner.GetFromJsonAsync<JsonElement>(Status)).GetProperty("waiting").GetString());

        await WithDbAsync(factory, async db => { (await db.Generations.SingleAsync(g => g.Id == queued)).Status = RunStatus.GeneratingImage; await db.SaveChangesAsync(); });
        await worker.StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));

        await FinishAsync(factory, queued);
        await worker.StepAsync(default);
        Assert.Equal(2, (await DrawingsAsync(factory)).Count);
    }

    [Fact]
    public async Task The_owners_own_chat_in_the_queue_does_not_hold_the_samples_back()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        var me = await WithDbAsync(factory, db => db.Users.Where(u => u.Role == UserRole.Owner).Select(u => u.Id).SingleAsync());
        await QueuedForAsync(factory, me);
        await PostAsync(owner, Start);
        await Worker(factory).StepAsync(default);
        Assert.Equal(2, (await DrawingsAsync(factory)).Count);
    }

    [Fact]
    public async Task A_full_queue_makes_the_run_wait_and_leaves_nothing_behind()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 1);
        var me = await WithDbAsync(factory, db => db.Users.Where(u => u.Role == UserRole.Owner).Select(u => u.Id).SingleAsync());
        var mine = new List<Guid>();
        for (var i = 0; i < 25; i++) mine.Add(await QueuedForAsync(factory, me));
        await PostAsync(owner, Start);
        var projects = await WithDbAsync(factory, db => db.Projects.CountAsync());

        await Worker(factory).StepAsync(default);
        Assert.Equal(25, (await DrawingsAsync(factory)).Count);
        Assert.Equal(projects, await WithDbAsync(factory, db => db.Projects.CountAsync()));
        Assert.True(On(await owner.GetFromJsonAsync<JsonElement>(Status)));

        // Waiting is not failing: as soon as there is room, the same template goes.
        await FinishAsync(factory, mine[0]);
        await Worker(factory).StepAsync(default);
        Assert.NotNull((await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SampleGenerationId);
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.Answered)]
    public async Task A_failed_template_is_passed_over_for_this_run_and_keeps_saying_it_failed(RunStatus outcome)
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 2);
        await PostAsync(owner, Start);
        var worker = Worker(factory);

        await worker.StepAsync(default);
        var failed = Assert.Single(await DrawingsAsync(factory));
        await FinishAsync(factory, failed.Id, outcome, outcome == RunStatus.Failed ? "AutomationFailed" : null);
        await worker.StepAsync(default);
        var drawings = await DrawingsAsync(factory);
        Assert.Equal(2, drawings.Count);
        Assert.Equal(ids[1], (await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.SampleGenerationId == drawings[1].Id))).Id);
        Assert.Equal(1, Number(await owner.GetFromJsonAsync<JsonElement>(Status), "failed"));

        await FinishAsync(factory, drawings[1].Id);
        await worker.StepAsync(default);
        await worker.StepAsync(default);
        Assert.Equal(2, (await DrawingsAsync(factory)).Count);

        var status = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(On(status));
        Assert.Equal((1, 1), (Number(status, "withoutSample"), Number(status, "failed")));
        var state = (await owner.GetFromJsonAsync<JsonElement>("/api/templates/manage")).GetProperty("templates").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == ids[0]);
        Assert.Equal("failed", state.GetProperty("sampleState").GetString());

        // The owner pressing start again is a new run, and a new chance.
        await PostAsync(owner, Start);
        await worker.StepAsync(default);
        var retried = (await DrawingsAsync(factory)).Last();
        Assert.Equal(retried.Id, (await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SampleGenerationId);
    }

    [Fact]
    public async Task A_template_the_queue_refuses_outright_is_passed_over_and_leaves_no_project()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var geminiOnly = await TemplatesAsync(factory, 1, provider: Provider.Gemini);
        var ids = await TemplatesAsync(factory, 1);
        // A ChatGPT account and no Gemini one: the Gemini template can only ever be told NoAccount, and must not stand in the way of the rest.
        await WithDbAsync(factory, async db => { db.Accounts.Add(new ProviderAccount { Provider = Provider.ChatGPT, Kind = AccountKind.Browser, Label = "A", ProfileDirectory = "a" }); await db.SaveChangesAsync(); });
        await WithDbAsync(factory, async db =>
        {
            (await db.Templates.SingleAsync(t => t.Id == geminiOnly[0])).Order = -1;
            await db.SaveChangesAsync();
        });
        await PostAsync(owner, Start);
        var worker = Worker(factory);

        await worker.StepAsync(default);
        Assert.Empty(await DrawingsAsync(factory));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Projects.CountAsync()));
        Assert.Equal(1, Number(await owner.GetFromJsonAsync<JsonElement>(Status), "failed"));

        await worker.StepAsync(default);
        var drawing = Assert.Single(await DrawingsAsync(factory));
        Assert.Equal(drawing.Id, (await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SampleGenerationId);
        await FinishAsync(factory, drawing.Id);
        await worker.StepAsync(default);
        Assert.False(On(await owner.GetFromJsonAsync<JsonElement>(Status)));
    }

    [Fact]
    public async Task Stop_halts_it_and_the_sample_being_made_still_arrives()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 2);
        await PostAsync(owner, Start);
        var worker = Worker(factory);
        await worker.StepAsync(default);
        var stopped = await PostAsync(owner, Stop);
        Assert.False(On(stopped));

        var drawing = Assert.Single(await DrawingsAsync(factory));
        await FinishAsync(factory, drawing.Id);
        await worker.StepAsync(default);
        await worker.StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));
        var status = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(On(status));
        Assert.Equal(1, Number(status, "withoutSample"));
        Assert.NotNull((await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SamplePath);
    }

    [Fact]
    public async Task A_restart_carries_on_and_a_drawing_it_cut_short_is_tried_again()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 2);
        await PostAsync(owner, Start);
        await Worker(factory).StepAsync(default);
        var cut = Assert.Single(await DrawingsAsync(factory));
        // What the dispatcher's sweep writes on a row the old process left mid-run. The restart did this, not the template.
        await FinishAsync(factory, cut.Id, RunStatus.Failed, "Interrupted");

        await Worker(factory).StepAsync(default);
        var drawings = await DrawingsAsync(factory);
        Assert.Equal(2, drawings.Count);
        Assert.Equal(drawings[1].Id, (await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[0]))).SampleGenerationId);
        Assert.Equal(0, Number(await owner.GetFromJsonAsync<JsonElement>(Status), "failed"));

        await FinishAsync(factory, drawings[1].Id);
        await Worker(factory).StepAsync(default);
        Assert.Equal(drawings[1].Id, (await DrawingsAsync(factory))[1].Id);
        Assert.Equal(3, (await DrawingsAsync(factory)).Count);
        Assert.NotNull((await WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == ids[1]))).SampleGenerationId);
    }

    [Fact]
    public async Task A_sample_being_drawn_by_hand_counts_as_the_one_at_a_time()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var ids = await TemplatesAsync(factory, 2);
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync($"/api/templates/{ids[1]}/sample/generate", new { inputs = Array.Empty<object>() })).StatusCode);
        await PostAsync(owner, Start);
        await Worker(factory).StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));
    }

    [Fact]
    public async Task A_run_covers_only_what_the_owner_was_told_about()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1, active: false);
        await TemplatesAsync(factory, 1);
        var started = await PostAsync(owner, Start);
        Assert.Equal(1, Number(started, "total"));
        var worker = Worker(factory);
        await worker.StepAsync(default);
        var drawing = Assert.Single(await DrawingsAsync(factory));

        // Added once the run was under way: it would spend an image the warning never counted.
        await TemplatesAsync(factory, 1);
        await FinishAsync(factory, drawing.Id);
        await worker.StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));
        var ended = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.False(On(ended));
        Assert.Equal(1, Number(ended, "withoutSample"));
    }

    [Fact]
    public async Task Starting_with_nothing_to_make_stays_off_and_starting_twice_keeps_the_same_run()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        Assert.False(On(await PostAsync(owner, Start)));
        await TemplatesAsync(factory, 2);
        await PostAsync(owner, Start);
        var worker = Worker(factory);
        await worker.StepAsync(default);
        await FinishAsync(factory, Assert.Single(await DrawingsAsync(factory)).Id);
        await worker.StepAsync(default);
        var again = await PostAsync(owner, Start);
        Assert.Equal((2, 1), (Number(again, "total"), Number(again, "done")));
    }

    [Fact]
    public async Task The_last_images_of_the_day_are_kept_for_people_and_the_run_carries_on_when_they_return()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        var me = await WithDbAsync(factory, db => db.Users.Where(u => u.Role == UserRole.Owner).Select(u => u.Id).SingleAsync());
        // A day of ten with two kept back, and eight already drawn today.
        var account = new ProviderAccount { Provider = Provider.ChatGPT, Kind = AccountKind.Browser, Label = "A", ProfileDirectory = "a", DailyCap = StarterService.ReservedShare * 2 };
        await WithDbAsync(factory, async db => { db.Accounts.Add(account); await db.SaveChangesAsync(); });
        for (var i = 0; i < account.DailyCap - 2; i++)
        {
            var drawn = await QueuedForAsync(factory, me);
            await WithDbAsync(factory, async db => { var g = await db.Generations.SingleAsync(x => x.Id == drawn); g.AccountId = account.Id; g.Status = RunStatus.Completed; await db.SaveChangesAsync(); });
        }
        var before = (await DrawingsAsync(factory)).Count;
        var started = await PostAsync(owner, Start);
        Assert.Equal(2, Number(started, "imagesLeftToday"));
        var worker = Worker(factory);

        await worker.StepAsync(default);
        Assert.Equal(before, (await DrawingsAsync(factory)).Count);
        var waiting = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.True(On(waiting));
        Assert.Equal("capacity", waiting.GetProperty("waiting").GetString());

        // A new day would leave the whole of it again; so does a higher cap, which is what the test can reach.
        await WithDbAsync(factory, async db => { (await db.Accounts.SingleAsync()).DailyCap = 100; await db.SaveChangesAsync(); });
        await worker.StepAsync(default);
        Assert.Equal(before + 1, (await DrawingsAsync(factory)).Count);
    }

    [Fact]
    public async Task An_account_with_a_small_cap_of_its_own_is_not_kept_back_for_ever()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        await WithDbAsync(factory, async db => { db.Accounts.Add(new ProviderAccount { Provider = Provider.ChatGPT, Kind = AccountKind.Browser, Label = "A", ProfileDirectory = "a", DailyCap = StarterService.ReservedShare - 1 }); await db.SaveChangesAsync(); });
        await PostAsync(owner, Start);
        await Worker(factory).StepAsync(default);
        Assert.Single(await DrawingsAsync(factory));
    }

    [Fact]
    public async Task With_no_account_connected_yet_the_sample_waits_in_the_queue()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 2);
        var started = await PostAsync(owner, Start);
        Assert.Equal(JsonValueKind.Null, started.GetProperty("imagesLeftToday").ValueKind);
        var worker = Worker(factory);
        await worker.StepAsync(default);
        await worker.StepAsync(default);
        Assert.Equal(RunStatus.Queued, Assert.Single(await DrawingsAsync(factory)).Status);
    }

    [Fact]
    public async Task A_run_whose_owner_no_longer_is_one_ends_rather_than_spending_on_their_word()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        await PostAsync(owner, Start);
        var me = await WithDbAsync(factory, db => db.Users.Where(u => u.Role == UserRole.Owner).Select(u => u.Id).SingleAsync());
        await WithDbAsync(factory, async db => { (await db.Users.SingleAsync(u => u.Id == me)).IsDisabled = true; await db.SaveChangesAsync(); });
        await Worker(factory).StepAsync(default);
        Assert.Empty(await DrawingsAsync(factory));
        Assert.Equal("off", await WithDbAsync(factory, db => db.Settings.AsNoTracking().Where(s => s.Key == StarterService.RunKey).Select(s => s.Value).SingleAsync()));
    }

    [Fact]
    public async Task A_setting_nobody_can_read_counts_as_off()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        await TemplatesAsync(factory, 1);
        await WithDbAsync(factory, async db => { db.Settings.Add(new AppSetting { Key = StarterService.RunKey, Value = "not a run" }); await db.SaveChangesAsync(); });
        await Worker(factory).StepAsync(default);
        Assert.Empty(await DrawingsAsync(factory));
        Assert.False(On(await owner.GetFromJsonAsync<JsonElement>(Status)));
    }

    [Fact]
    public async Task The_status_says_how_much_of_the_pack_is_in()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var before = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.Equal((StarterPack.All.Count, 0, 0), (Number(before, "packTotal"), Number(before, "packAdded"), Number(before, "withoutSample")));
        await PostAsync(owner, Starter);
        var after = await owner.GetFromJsonAsync<JsonElement>(Status);
        Assert.Equal((StarterPack.All.Count, StarterPack.All.Count, StarterPack.All.Count), (Number(after, "packTotal"), Number(after, "packAdded"), Number(after, "withoutSample")));
    }
}
