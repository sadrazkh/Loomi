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
/// <summary>What the owner reads about everybody's use: counted from the requests themselves, per period, with the door each request came through.</summary>
public class UsageTests
{
    private static DateTime Today(int minutes) => Quota.Midnight().AddMinutes(minutes);
    private static DateTime DaysAgo(int days) => Quota.Midnight().AddDays(-days).AddHours(1);
    private static async Task<Guid> SeedAsync(ApiV1World world, Guid user, params Generation[] rows) => await world.DbAsync(async db =>
    {
        var project = new ImageProject { UserId = user, Title = "p" };
        db.Projects.Add(project);
        foreach (var g in rows) { g.ProjectId = project.Id; g.UserId = user; g.Prompt = g.Prompt.Length == 0 ? "a prompt" : g.Prompt; db.Generations.Add(g); }
        await db.SaveChangesAsync();
        return project.Id;
    });
    private static Generation Row(RunStatus status, Operation operation, Channel channel, DateTime at, string? template = null) =>
        new() { Status = status, Operation = operation, Channel = channel, CreatedAt = at, TemplateTitle = template };
    private static async Task<T> ReportAsync<T>(ApiV1World world, Func<UsageReport, Task<T>> read)
    {
        using var scope = world.Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<UsageReport>());
    }

    [Fact]
    public async Task A_request_records_the_door_it_came_through()
    {
        using var world = await ApiV1World.CreateAsync();
        var (_, api) = await world.MemberAsync("sara");
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/chats", new { prompt = "from a script" })).StatusCode);
        var web = await UserAdminTests.OwnerSessionAsync(world.Factory);
        Assert.Equal(HttpStatusCode.Created, (await web.PostAsJsonAsync("/api/projects/start", new { prompt = "from the page" })).StatusCode);
        var owner = await world.DbAsync(db => db.Users.SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username));
        using (var scope = world.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<GenerationService>().StartAsync(Viewer.For(owner), null, "from the bot", null, null, default);
        var channels = await world.DbAsync(db => db.Generations.ToDictionaryAsync(g => g.Prompt, g => g.Channel));
        Assert.Equal(Channel.Api, channels["from a script"]);
        Assert.Equal(Channel.Web, channels["from the page"]);
        Assert.Equal(Channel.Bot, channels["from the bot"]);
    }

    [Fact]
    public async Task Requests_made_before_the_door_was_recorded_are_read_from_the_bots_own_records()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        var migrations = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(migrations[migrations.FindIndex(m => m.EndsWith("_GenerationChannel")) - 1]);
        var user = Guid.NewGuid(); var project = Guid.NewGuid(); var fromBot = Guid.NewGuid(); var fromWeb = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Users (Id, Username, NormalizedUsername, PasswordHash, Role, DailyQuota, IsDisabled, CreatedAt) VALUES ({0}, 'sara', 'sara', '', 1, 0, 0, '2026-09-01 00:00:00')", user);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Projects (Id, UserId, Title, CreatedAt, UpdatedAt, Status, BrowserProfileReference) VALUES ({0}, {1}, 'p', '2026-09-01 00:00:00', '2026-09-01 00:00:00', 'Ready', 'default')", project, user);
        foreach (var id in new[] { fromBot, fromWeb })
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Generations (Id, ProjectId, UserId, Provider, Prompt, CreditCost, CreatedAt, Operation, Status) VALUES ({0}, {1}, {2}, 0, 'x', 0, '2026-09-01 00:00:00', 0, 7)", id, project, user);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO BotRequests (GenerationId, ChatId, CreatedAt) VALUES ({0}, 5150, '2026-09-01 00:00:00')", fromBot);
        await migrator.MigrateAsync();
        var channels = await db.Generations.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Channel);
        Assert.Equal(Channel.Bot, channels[fromBot]);
        Assert.Equal(Channel.Web, channels[fromWeb]);
    }

    [Fact]
    public async Task Counts_follow_outcome_kind_and_door_within_the_period()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        await SeedAsync(world, sara,
            Row(RunStatus.Completed, Operation.Generate, Channel.Web, Today(10), "Golden"),
            Row(RunStatus.Completed, Operation.Reply, Channel.Bot, Today(20), "Golden"),
            Row(RunStatus.Answered, Operation.Reply, Channel.Bot, Today(30), "Neon"),
            Row(RunStatus.Failed, Operation.Generate, Channel.Api, Today(40)),
            Row(RunStatus.Cancelled, Operation.Edit, Channel.Web, Today(50)),
            Row(RunStatus.Queued, Operation.Branch, Channel.Web, Today(60)),
            Row(RunStatus.Completed, Operation.Generate, Channel.Web, DaysAgo(1), "Neon"));

        var today = (await ReportAsync(world, r => r.UserAsync(sara, UsagePeriod.Today, default)))!;
        Assert.Equal((6, 2, 1, 1, 1), (today.Requests, today.Images, today.Answers, today.Failed, today.Cancelled));
        Assert.Equal((3, 3), (today.NewChats, today.FollowUps));
        Assert.Equal((3, 2, 1), (today.Web, today.Bot, today.Api));
        Assert.Equal([new TemplateCount("Golden", 2), new TemplateCount("Neon", 1)], today.Templates);
        Assert.Equal(Today(60), today.LastActive);

        var week = (await ReportAsync(world, r => r.UserAsync(sara, UsagePeriod.Week, default)))!;
        Assert.Equal((7, 3), (week.Requests, week.Images));
        Assert.Equal([new TemplateCount("Golden", 2), new TemplateCount("Neon", 2)], week.Templates);
    }

    [Fact]
    public async Task Credits_spent_are_net_of_refunds_and_an_owner_has_no_balance()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara", credits: 0);
        await world.DbAsync(async db =>
        {
            db.CreditEntries.AddRange(
                new CreditEntry { UserId = sara, Amount = 1000, Kind = CreditKind.Grant, CreatedAt = DaysAgo(5) },
                new CreditEntry { UserId = sara, Amount = -10, Kind = CreditKind.Charge, CreatedAt = Today(1) },
                new CreditEntry { UserId = sara, Amount = -10, Kind = CreditKind.Charge, CreatedAt = Today(2) },
                new CreditEntry { UserId = sara, Amount = 10, Kind = CreditKind.Refund, CreatedAt = Today(3) },
                new CreditEntry { UserId = sara, Amount = -10, Kind = CreditKind.Charge, CreatedAt = DaysAgo(2) });
            return await db.SaveChangesAsync();
        });
        var today = (await ReportAsync(world, r => r.UserAsync(sara, UsagePeriod.Today, default)))!;
        var week = (await ReportAsync(world, r => r.UserAsync(sara, UsagePeriod.Week, default)))!;
        Assert.Equal((10, 20), (today.CreditsSpent, week.CreditsSpent));
        Assert.Equal(980, week.CreditsLeft);
        var (owner, _) = await world.OwnerAsync();
        Assert.Null((await ReportAsync(world, r => r.UserAsync(owner, UsagePeriod.All, default)))!.CreditsLeft);
    }

    [Fact]
    public async Task Everyone_is_listed_busiest_first_and_the_totals_add_up()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var (reza, _) = await world.MemberAsync("reza");
        await world.MemberAsync("idle");
        await SeedAsync(world, sara, Row(RunStatus.Completed, Operation.Generate, Channel.Web, Today(1)));
        await SeedAsync(world, reza, Row(RunStatus.Completed, Operation.Generate, Channel.Bot, Today(2)), Row(RunStatus.Failed, Operation.Reply, Channel.Bot, Today(3)));
        var summary = await ReportAsync(world, r => r.SummaryAsync(UsagePeriod.Today, default));
        Assert.Equal(["reza", "sara"], summary.Users.Take(2).Select(u => u.Username));
        Assert.Contains(summary.Users, u => u.Username == "idle" && u.Requests == 0 && u.LastActive == null);
        Assert.Equal((2, 3, 2, 1), (summary.ActiveUsers, summary.Requests, summary.Images, summary.Failed));
        Assert.Equal(Quota.Midnight(), summary.Since);
        Assert.Null((await ReportAsync(world, r => r.SummaryAsync(UsagePeriod.All, default))).Since);
    }

    [Fact]
    public async Task Requests_come_newest_first_a_page_at_a_time()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        await SeedAsync(world, sara,
            new Generation { Prompt = "first", Status = RunStatus.Completed, CreatedAt = Today(1), LocalImagePath = "projects/x/y/image.png" },
            new Generation { Prompt = "second", Status = RunStatus.Answered, CreatedAt = Today(2), ReplyText = "words" },
            new Generation { Prompt = "third", Status = RunStatus.Failed, CreatedAt = Today(3), ErrorMessage = "AutomationFailed" });
        var page = await ReportAsync(world, r => r.RequestsAsync(sara, 0, 2, default));
        Assert.Equal(["third", "second"], page.Select(i => i.Prompt));
        var rest = await ReportAsync(world, r => r.RequestsAsync(sara, 2, 2, default));
        var first = Assert.Single(rest);
        Assert.True(first.HasImage);
        Assert.Equal("p", first.ProjectTitle);
    }

    [Fact]
    public async Task Only_the_owner_reads_usage()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, member) = await world.MemberAsync("sara");
        var (_, owner) = await world.OwnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/usage/users/{sara}/requests")).StatusCode);
        var summary = await owner.GetFromJsonAsync<JsonElement>("/api/usage?period=week");
        Assert.Equal("Week", summary.GetProperty("period").GetString());
        Assert.Contains(summary.GetProperty("users").EnumerateArray(), u => u.GetProperty("username").GetString() == "sara");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/usage?period=forever")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/usage/users/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/usage/users/{sara}?period=all")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/usage/users/{sara}/requests?take=500")).StatusCode);
    }

    [Fact]
    public async Task The_nightly_report_can_be_switched_off_and_on()
    {
        using var world = await ApiV1World.CreateAsync();
        var (_, owner) = await world.OwnerAsync();
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/usage/daily-report")).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/usage/daily-report", new { enabled = false })).StatusCode);
        var now = await owner.GetFromJsonAsync<JsonElement>("/api/usage/daily-report");
        Assert.False(now.GetProperty("enabled").GetBoolean());
        Assert.Equal(23, now.GetProperty("hour").GetInt32());
    }
}
