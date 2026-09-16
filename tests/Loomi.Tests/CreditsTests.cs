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
public class CreditsTests
{
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
    private static async Task<Guid> MemberAsync(AppFactory factory, string username, string password, int credits = 0)
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
    private static async Task<Guid> ProjectAsync(AppFactory factory, Guid owner, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = owner, Title = title };
        db.Projects.Add(project); await db.SaveChangesAsync();
        return project.Id;
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task A_balance_is_the_sum_of_the_ledger_and_never_a_stored_column()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass", 1500);
        Assert.Equal(1500, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        await WithDbAsync(factory, async db =>
        {
            db.CreditEntries.Add(new CreditEntry { UserId = mia, Amount = -200, Kind = CreditKind.Charge });
            db.CreditEntries.Add(new CreditEntry { UserId = mia, Amount = 50, Kind = CreditKind.Refund });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(1350, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
    }

    [Fact]
    public async Task An_exact_rule_beats_the_providers_default_price()
    {
        using var factory = new AppFactory();
        await WithDbAsync(factory, async db =>
        {
            db.PricingRules.Add(new PricingRule { Provider = Provider.ChatGPT, Operation = Operation.Edit, Cost = 3 });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(3, await WithDbAsync(factory, db => db.PriceAsync(Provider.ChatGPT, Operation.Edit, default)));
        // Seeded by bootstrap as the provider-wide default.
        Assert.Equal(10, await WithDbAsync(factory, db => db.PriceAsync(Provider.ChatGPT, Operation.Generate, default)));
    }

    [Fact]
    public async Task Queueing_charges_the_project_owner_and_records_what_it_cost()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass", 100);
        var project = await ProjectAsync(factory, mia, "A project of her own");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        var queued = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "a quiet harbour" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(90, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        var generation = await WithDbAsync(factory, db => db.Generations.AsNoTracking().FirstAsync(g => g.Id == id));
        Assert.Equal(10, generation.CreditCost);
        var charge = await WithDbAsync(factory, db => db.CreditEntries.AsNoTracking().SingleAsync(e => e.GenerationId == id));
        Assert.Equal(CreditKind.Charge, charge.Kind);
        Assert.Equal(-10, charge.Amount);
    }

    [Fact]
    public async Task Too_few_credits_is_refused_while_the_person_is_still_looking_at_it()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass", 5);
        var project = await ProjectAsync(factory, mia, "A project of her own");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        var refused = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "too expensive" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("InsufficientCredits", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Nothing was queued and nothing was taken.
        Assert.Equal(5, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
    }

    [Fact]
    public async Task Cancelling_returns_the_credits_exactly_once()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass", 100);
        var project = await ProjectAsync(factory, mia, "A project of her own");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        var queued = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "never mind" });
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(90, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/generations/{id}/cancel", null)).StatusCode);
        Assert.Equal(100, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        // Asking again returns nothing: the ledger holds one refund for a generation, no more.
        Assert.Equal(0, await WithDbAsync(factory, db => db.RefundAsync(id, default)));
        Assert.Equal(100, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        Assert.Equal(1, await WithDbAsync(factory, db => db.CreditEntries.CountAsync(e => e.GenerationId == id && e.Kind == CreditKind.Refund)));
    }

    [Fact]
    public async Task Work_that_finished_is_not_refunded()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass", 100);
        var project = await ProjectAsync(factory, mia, "A project of her own");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        var queued = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "it worked" });
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await WithDbAsync(factory, db => db.Generations.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Completed)));
        Assert.Equal(0, await WithDbAsync(factory, db => db.RefundAsync(id, default)));
        Assert.Equal(90, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
    }

    [Fact]
    public async Task The_owner_is_not_charged_for_the_accounts_they_pay_for()
    {
        using var factory = new AppFactory();
        using var client = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var created = await client.PostAsJsonAsync("/api/projects", new { title = "The owner's own project" });
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var queued = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "free for the owner" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.AsNoTracking().Where(g => g.Id == id).Select(g => g.CreditCost).FirstAsync()));
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
    }

    [Fact]
    public async Task Only_the_owner_hands_out_credits_and_the_ledger_records_who_did()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, "mia", "mia-strong-pass");
        using (var member = await SignInAsync(factory, "mia", "mia-strong-pass"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/users/{mia}/credits", new { amount = 9999 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/users/{mia}/credits")).StatusCode);
            // Their own ledger is theirs to read.
            Assert.Equal(0, (await member.GetFromJsonAsync<JsonElement>("/api/credits")).GetProperty("balance").GetInt32());
        }
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/users/{mia}/credits", new { amount = 1500, note = "welcome" })).StatusCode);
        Assert.Equal(1500, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
        var entry = await WithDbAsync(factory, db => db.CreditEntries.AsNoTracking().SingleAsync(e => e.UserId == mia));
        Assert.Equal(CreditKind.Grant, entry.Kind);
        Assert.NotNull(entry.ByUserId);
        // A correction may be negative; that is what Adjust is for.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/users/{mia}/credits", new { amount = -500, note = "correction" })).StatusCode);
        Assert.Equal(1000, await WithDbAsync(factory, db => db.BalanceAsync(mia, default)));
    }
}
