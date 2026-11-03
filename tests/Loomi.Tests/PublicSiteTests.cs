using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Loomi.Tests;
/// <summary>The intro page is what a stranger arriving from an advert sees, so its two routes answer with no cookie at all. They carry the owner's words and
/// the samples, and nothing of how a look is made: a template's prompt is the owner's work, and an original picture is never handed out here.</summary>
public class PublicSiteTests
{
    private const string SecretPrompt = "SECRET-PROMPT-warm-film-grain", SecretInstructions = "SECRET-INSTRUCTIONS-one-clear-photo";
    private const string MemberPassword = "tester-password-1";
    private static readonly Lazy<byte[]> Wide = new(() => Png(1800, 1200));

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(232, 168, 56));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
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
    private static async Task<HttpClient> MemberAsync(AppFactory factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var member = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
            member.PasswordHash = new PasswordService().Hash(member, MemberPassword);
            db.Users.Add(member);
            await db.SaveChangesAsync();
        }
        return await SignInAsync(factory, "tester", MemberPassword);
    }
    /// <summary>A browser that has never been here: no cookie store, so nothing the app handed out earlier can make a route look anonymous when it is not.</summary>
    private static HttpClient Stranger(AppFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    private static async Task<JsonElement> SiteAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/api/public/site");
    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    private static async Task<TemplateCategory> CategoryAsync(AppFactory factory, string name, int order)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var category = new TemplateCategory { Name = name, Order = order };
        db.TemplateCategories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }
    /// <summary>A template directly in the store; <paramref name="sample"/> null means it has none.</summary>
    private static async Task<Guid> TemplateAsync(AppFactory factory, TemplateCategory category, string title, int order = 0, bool active = true, byte[]? sample = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new PromptTemplate { CategoryId = category.Id, Title = title, Prompt = SecretPrompt, Instructions = SecretInstructions, Order = order, IsActive = active };
        if (sample is not null) template.SamplePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveTemplateAsync(template.Id, sample, default);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }
    private static async Task SetAsync(AppFactory factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GrowthSettings>().SetAsync(key, value, default);
    }
    private static async Task SwitchSignupAsync(AppFactory factory, bool on)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GrowthSettings>().SetOnAsync(GrowthSettings.SignupOpen, on, default);
    }
    private static object Row(string name = "Starter", string? detail = "Ten pictures", string price = "100,000 toman", bool highlight = false) => new { name, detail, price, highlight };
    private static async Task<string?> ErrorAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task The_site_shows_active_templates_with_samples_and_nothing_of_how_they_are_made()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var shown = await TemplateAsync(factory, category, "Golden hour", sample: Png(8, 8));
        await TemplateAsync(factory, category, "Retired look", active: false, sample: Png(8, 8));
        await TemplateAsync(factory, category, "Unfinished look");

        using var stranger = Stranger(factory);
        var response = await stranger.GetAsync("/api/public/site");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        foreach (var hidden in new[] { SecretPrompt, SecretInstructions, "Retired look", "Unfinished look", "prompt", "instructions", "note", "samplePath", "provider", "isActive", "categoryId" })
            Assert.DoesNotContain(hidden, raw, StringComparison.OrdinalIgnoreCase);
        var site = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(["botUsername", "contact", "giftImages", "prices", "showcase", "signupOpen"], Names(site));
        var item = Assert.Single(site.GetProperty("showcase").EnumerateArray());
        Assert.Equal(["category", "id", "sample", "title"], Names(item));
        Assert.Equal((shown, "Golden hour", "Portraits"), (item.GetProperty("id").GetGuid(), item.GetProperty("title").GetString(), item.GetProperty("category").GetString()));
        Assert.StartsWith($"/api/public/templates/{shown}/sample", item.GetProperty("sample").GetString());
    }

    [Fact]
    public async Task An_empty_workspace_answers_with_an_empty_site_rather_than_failing()
    {
        using var factory = new AppFactory();
        using var stranger = Stranger(factory);
        var site = await SiteAsync(stranger);
        Assert.False(site.GetProperty("signupOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("contact").ValueKind);
        Assert.Empty(site.GetProperty("prices").EnumerateArray());
        Assert.Empty(site.GetProperty("showcase").EnumerateArray());
    }

    [Fact]
    public async Task The_showcase_follows_category_and_template_order_and_stops_at_twenty_four()
    {
        using var factory = new AppFactory();
        var second = await CategoryAsync(factory, "Second", 2);
        var first = await CategoryAsync(factory, "First", 1);
        var sample = Png(4, 4);
        await TemplateAsync(factory, second, "s-late", order: 2, sample: sample);
        await TemplateAsync(factory, second, "s-early", order: 1, sample: sample);
        for (var i = 0; i < 30; i++) await TemplateAsync(factory, i < 22 ? first : second, $"t{i:00}", order: 10 + i, sample: sample);

        using var stranger = Stranger(factory);
        var showcase = (await SiteAsync(stranger)).GetProperty("showcase").EnumerateArray().ToList();
        Assert.Equal(24, showcase.Count);
        Assert.Equal("First", showcase[0].GetProperty("category").GetString());
        Assert.Equal("t00", showcase[0].GetProperty("title").GetString());
        Assert.Equal(("Second", "s-early"), (showcase[22].GetProperty("category").GetString(), showcase[22].GetProperty("title").GetString()));
        Assert.Equal("s-late", showcase[23].GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_sample_whose_file_is_gone_is_left_out_so_the_page_shows_no_broken_picture()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var id = await TemplateAsync(factory, category, "Lost file", sample: Png(4, 4));
        using (var scope = factory.Services.CreateScope())
        {
            var path = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Templates.FindAsync(id))!.SamplePath!;
            File.Delete(scope.ServiceProvider.GetRequiredService<IImageStorage>().Resolve(path));
        }
        using var stranger = Stranger(factory);
        Assert.Empty((await SiteAsync(stranger)).GetProperty("showcase").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/public/templates/{id}/sample")).StatusCode);
    }

    [Fact]
    public async Task Signup_counts_as_open_only_with_both_the_switch_on_and_a_bot()
    {
        using var factory = new AppFactory();
        using var stranger = Stranger(factory);
        var bot = factory.Services.GetRequiredService<BotSettings>();

        var site = await SiteAsync(stranger);
        Assert.False(site.GetProperty("signupOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("giftImages").ValueKind);

        await SwitchSignupAsync(factory, true);
        site = await SiteAsync(stranger);
        Assert.False(site.GetProperty("signupOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("giftImages").ValueKind);

        await bot.SetAsync("123456:token", "loomi_bot", default);
        site = await SiteAsync(stranger);
        Assert.True(site.GetProperty("signupOpen").GetBoolean());
        Assert.Equal("loomi_bot", site.GetProperty("botUsername").GetString());
        Assert.Equal(GrowthSettings.DefaultGiftImages, site.GetProperty("giftImages").GetInt32());

        await SetAsync(factory, GrowthSettings.SignupGiftImages, "3");
        Assert.Equal(3, (await SiteAsync(stranger)).GetProperty("giftImages").GetInt32());
        await SetAsync(factory, GrowthSettings.SignupGiftImages, "-2");
        Assert.Equal(0, (await SiteAsync(stranger)).GetProperty("giftImages").GetInt32());

        // The switch off with a bot in place: nobody is invited in, so neither the bot nor the gift is named.
        await SwitchSignupAsync(factory, false);
        site = await SiteAsync(stranger);
        Assert.False(site.GetProperty("signupOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("botUsername").ValueKind);
        Assert.Equal(JsonValueKind.Null, site.GetProperty("giftImages").ValueKind);
    }

    [Fact]
    public async Task The_site_may_be_kept_for_a_minute_and_a_sample_for_longer()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var id = await TemplateAsync(factory, category, "Golden hour", sample: Png(8, 8));
        using var stranger = Stranger(factory);

        var site = await stranger.GetAsync("/api/public/site");
        Assert.True(site.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromMinutes(1), site.Headers.CacheControl?.MaxAge);
        Assert.Empty(site.Headers.Where(h => h.Key == "Set-Cookie"));

        var sample = await stranger.GetAsync($"/api/public/templates/{id}/sample");
        Assert.True(sample.Headers.CacheControl?.Public);
        Assert.True(sample.Headers.CacheControl?.MaxAge > TimeSpan.FromMinutes(1));

        // A miss stays what every other API answer is, so a sample taken down does not linger as a remembered 404 either.
        var missing = await stranger.GetAsync($"/api/public/templates/{Guid.NewGuid()}/sample");
        Assert.True(missing.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task A_sample_is_served_to_a_stranger_only_for_an_active_template_that_has_one()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var active = await TemplateAsync(factory, category, "Active", sample: Wide.Value);
        var inactive = await TemplateAsync(factory, category, "Inactive", active: false, sample: Wide.Value);
        var empty = await TemplateAsync(factory, category, "No sample");
        using var stranger = Stranger(factory);

        var served = await stranger.GetAsync($"/api/public/templates/{active}/sample?w=640");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
        foreach (var id in new[] { inactive, empty, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/public/templates/{id}/sample?w=640")).StatusCode);
    }

    [Theory]
    [InlineData("", 640)]
    [InlineData("?w=256", 256)]
    [InlineData("?w=640", 640)]
    [InlineData("?w=1024", 1024)]
    [InlineData("?w=1600", 1024)]
    [InlineData("?w=99999", 640)]
    [InlineData("?w=big", 640)]
    public async Task The_original_is_never_served_only_the_fixed_widths(string query, int expected)
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var id = await TemplateAsync(factory, category, "Golden hour", sample: Wide.Value);
        using var stranger = Stranger(factory);

        var served = await stranger.GetAsync($"/api/public/templates/{id}/sample{query}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
        using var image = SKBitmap.Decode(await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(expected, image.Width);
    }

    [Fact]
    public async Task A_sample_that_cannot_be_made_small_is_a_miss_rather_than_the_original()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        // The signature of a PNG and nothing behind it: stored without complaint, and undecodable.
        var id = await TemplateAsync(factory, category, "Broken", sample: [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 1, 2, 3, 4]);
        using var stranger = Stranger(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/public/templates/{id}/sample")).StatusCode);
    }

    [Fact]
    public async Task Prices_and_contact_round_trip_and_reach_the_public_site()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);

        var empty = await owner.GetFromJsonAsync<JsonElement>("/api/site/settings");
        Assert.Empty(empty.GetProperty("prices").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("contact").ValueKind);

        var put = await owner.PutAsJsonAsync("/api/site/settings", new { prices = new[] { Row(" Starter "), Row("Plus", null, "200,000 toman", true) }, contact = " @loomi_support " });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        foreach (var site in new[] { await owner.GetFromJsonAsync<JsonElement>("/api/site/settings"), await SiteAsync(Stranger(factory)) })
        {
            Assert.Equal("@loomi_support", site.GetProperty("contact").GetString());
            var prices = site.GetProperty("prices").EnumerateArray().ToList();
            Assert.Equal(2, prices.Count);
            Assert.Equal(["detail", "highlight", "name", "price"], Names(prices[0]));
            Assert.Equal(("Starter", "Ten pictures", "100,000 toman", false), (prices[0].GetProperty("name").GetString(), prices[0].GetProperty("detail").GetString(), prices[0].GetProperty("price").GetString(), prices[0].GetProperty("highlight").GetBoolean()));
            Assert.Equal(("Plus", JsonValueKind.Null, true), (prices[1].GetProperty("name").GetString(), prices[1].GetProperty("detail").ValueKind, prices[1].GetProperty("highlight").GetBoolean()));
        }

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { prices = Array.Empty<object>(), contact = "" })).StatusCode);
        var cleared = await SiteAsync(Stranger(factory));
        Assert.Empty(cleared.GetProperty("prices").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("contact").ValueKind);
    }

    /// <summary>Six full rows of Persian are well inside the limits, and written as \uXXXX they would be six times too long for the row they are kept in.</summary>
    [Fact]
    public async Task Six_full_rows_of_persian_fit_in_the_setting_they_are_kept_in()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var rows = Enumerable.Range(0, 6).Select(_ => Row(new string('ن', 60), new string('ت', 200), new string('ق', 40))).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { prices = rows })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<GrowthSettings>().GetAsync(GrowthSettings.SitePrices, default);
        Assert.True(stored!.Length <= 4000, $"{stored.Length} characters");
        Assert.Equal(new string('ق', 40), (await owner.GetFromJsonAsync<JsonElement>("/api/site/settings")).GetProperty("prices")[5].GetProperty("price").GetString());
    }

    [Fact]
    public async Task Whatever_would_not_fit_is_refused_rather_than_cut_short()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        // Within every field's limit, and only quotes: each one is written with its escape, and the row of JSON is what has to fit.
        var quotes = Enumerable.Range(0, 6).Select(_ => Row(new string('"', 60), new string('"', 200), new string('"', 40))).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { prices = quotes })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var kept = await scope.ServiceProvider.GetRequiredService<GrowthSettings>().GetAsync(GrowthSettings.SitePrices, default);
        Assert.True(kept!.Length <= 4000, $"{kept.Length} characters");
        // Emoji are the one thing that still swells, each written as two \uXXXX: every field is within its limit, and the row could not hold them.
        var emoji = Enumerable.Range(0, 6).Select(_ => Row(string.Concat(Enumerable.Repeat("😀", 30)), string.Concat(Enumerable.Repeat("😀", 100)), string.Concat(Enumerable.Repeat("😀", 20)))).ToArray();
        var refused = await owner.PutAsJsonAsync("/api/site/settings", new { prices = emoji });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("InvalidPrices", await ErrorAsync(refused));
        Assert.Equal(kept, await scope.ServiceProvider.GetRequiredService<GrowthSettings>().GetAsync(GrowthSettings.SitePrices, default));
    }

    public static TheoryData<object[]> BadRows => new()
    {
        { Enumerable.Range(0, 7).Select(i => Row($"Row {i}")).ToArray() },
        { new[] { Row(new string('x', 61)) } },
        { new[] { Row(detail: new string('x', 201)) } },
        { new[] { Row(price: new string('x', 41)) } },
        { new[] { Row(name: "   ") } },
        { new[] { Row(price: "") } },
        { new[] { Row("One", highlight: true), Row("Two", highlight: true) } },
        { new[] { Row("Line\nbreak") } },
    };
    [Theory]
    [MemberData(nameof(BadRows))]
    public async Task A_bad_price_row_is_refused_and_nothing_changes(object[] rows)
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { prices = new[] { Row("Kept") }, contact = "@kept_contact" })).StatusCode);

        var refused = await owner.PutAsJsonAsync("/api/site/settings", new { prices = rows, contact = "@new_contact" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("InvalidPrices", await ErrorAsync(refused));

        var kept = await owner.GetFromJsonAsync<JsonElement>("/api/site/settings");
        Assert.Equal("@kept_contact", kept.GetProperty("contact").GetString());
        Assert.Equal("Kept", kept.GetProperty("prices")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("loomi_support")]
    [InlineData("@abc")]
    [InlineData("@has space")]
    [InlineData("@نام_فارسی")]
    [InlineData("http://example.com/buy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://t.me@evil.example/")]
    public async Task A_contact_that_is_neither_a_handle_nor_an_https_address_is_refused(string contact)
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { prices = new[] { Row("Kept") }, contact = "@kept_contact" })).StatusCode);

        var refused = await owner.PutAsJsonAsync("/api/site/settings", new { prices = Array.Empty<object>(), contact });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("InvalidContact", await ErrorAsync(refused));

        // Refused whole: the prices that came with it did not go in either.
        var kept = await owner.GetFromJsonAsync<JsonElement>("/api/site/settings");
        Assert.Equal("@kept_contact", kept.GetProperty("contact").GetString());
        Assert.Single(kept.GetProperty("prices").EnumerateArray());
    }

    [Theory]
    [InlineData("@loomi_support")]
    [InlineData("https://example.com/buy?plan=one")]
    [InlineData("HTTPS://example.com")]
    public async Task A_handle_or_an_https_address_is_accepted(string contact)
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { contact })).StatusCode);
        Assert.Equal(contact, (await owner.GetFromJsonAsync<JsonElement>("/api/site/settings")).GetProperty("contact").GetString());
    }

    [Fact]
    public async Task A_contact_over_a_hundred_characters_is_refused()
    {
        using var factory = new AppFactory();
        using var owner = await OwnerAsync(factory);
        var at = "https://example.com/" + new string('a', 80);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/site/settings", new { contact = at })).StatusCode);
        var refused = await owner.PutAsJsonAsync("/api/site/settings", new { contact = at + "a" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("InvalidContact", await ErrorAsync(refused));
    }

    [Fact]
    public async Task The_settings_are_the_owners_alone()
    {
        using var factory = new AppFactory();
        using var stranger = Stranger(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/site/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PutAsJsonAsync("/api/site/settings", new { contact = "@stranger_here" })).StatusCode);

        using var member = await MemberAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/site/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/site/settings", new { contact = "@member_here" })).StatusCode);

        using var owner = await OwnerAsync(factory);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var unforged = await owner.PutAsJsonAsync("/api/site/settings", new { contact = "@no_csrf_here" });
        Assert.Equal(HttpStatusCode.BadRequest, unforged.StatusCode);
        Assert.Equal("InvalidCsrf", await ErrorAsync(unforged));
        Assert.Equal(JsonValueKind.Null, (await SiteAsync(stranger)).GetProperty("contact").ValueKind);
    }

    [Fact]
    public async Task Nothing_else_became_anonymous()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var id = await TemplateAsync(factory, category, "Golden hour", sample: Png(8, 8));
        using var stranger = Stranger(factory);
        foreach (var path in new[] { "/api/projects", "/api/templates", "/api/templates/manage", $"/api/templates/{id}/sample", "/api/auth/status", "/api/pricing", "/api/users", "/api/uploads/" + Guid.NewGuid(), "/api/site/settings" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_stale_or_made_up_credential_does_not_shut_a_visitor_out_of_the_public_page()
    {
        using var factory = new AppFactory();
        var category = await CategoryAsync(factory, "Portraits", 1);
        var id = await TemplateAsync(factory, category, "Golden hour", sample: Png(8, 8));
        using var visitor = Stranger(factory);
        visitor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lm_not_a_real_token");
        visitor.DefaultRequestHeaders.Add("Cookie", "Loomi.Session=not-a-real-session");
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/api/public/site")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/public/templates/{id}/sample")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/projects")).StatusCode);
    }
}
