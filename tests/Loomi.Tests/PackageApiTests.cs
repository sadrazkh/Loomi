using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Loomi.Tests;
/// <summary>The owner hands packages out, stretches and stops them over HTTP; a member reads only their own. Everything about them reaches the page
/// beside the regular balance, never mixed into it.</summary>
public class PackageApiTests
{
    private static async Task<JsonElement> GrantAsync(HttpClient owner, Guid user, object body)
    {
        var response = await owner.PostAsJsonAsync($"/api/users/{user}/packages", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiV1World.JsonAsync(response);
    }
    private static async Task<JsonElement> PostAsync(HttpClient owner, string path, object? body = null)
    {
        var response = body is null ? await owner.PostAsync(path, null) : await owner.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiV1World.JsonAsync(response);
    }
    private static async Task<string> InvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await ApiV1World.ErrorAsync(response);
    }
    private static DateTime At(JsonElement package, string name)
    {
        var text = package.GetProperty(name).GetString()!;
        Assert.EndsWith("Z", text);
        return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }
    private static List<string> Names(JsonElement list) => list.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList();
    private static Task<Guid> AddAsync(ApiV1World world, Guid user, string name, DateTime starts, DateTime ends, PackageKind kind = PackageKind.Daily, int amount = 3, DateTime? created = null) =>
        world.DbAsync(async db =>
        {
            var package = new CreditPackage { UserId = user, Kind = kind, Amount = amount, Name = name, StartsAt = starts, EndsAt = ends, Note = "owner only", CreatedAt = created ?? DateTime.UtcNow };
            db.CreditPackages.Add(package);
            await db.SaveChangesAsync();
            return package.Id;
        });

    [Fact]
    public async Task Only_the_owner_reads_or_hands_out_packages_and_the_page_session_needs_its_csrf_token()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, member) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var id = (await GrantAsync(owner, sara, new { kind = "Daily", amount = 3, days = 30 })).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/users/{sara}/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Unlimited", amount = 0, days = 365 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/users/{sara}/packages/{id}/extend", new { days = 365 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/users/{sara}/packages/{id}/end", null)).StatusCode);
        Assert.Equal(1, await world.DbAsync(db => db.CreditPackages.CountAsync()));

        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var forged = await owner.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Daily", amount = 3, days = 30 });
        Assert.Equal("InvalidCsrf", await InvalidAsync(forged));
        Assert.Equal(1, await world.DbAsync(db => db.CreditPackages.CountAsync()));
    }

    [Fact]
    public async Task A_granted_package_comes_back_whole_and_a_blank_name_reads_in_persian()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var ownerId = await world.DbAsync(db => db.Users.Where(u => u.NormalizedUsername == OwnerBootstrap.Username).Select(u => u.Id).SingleAsync());

        var before = DateTime.UtcNow;
        var credits = await GrantAsync(owner, sara, new { kind = "Credits", amount = 500, days = 30, name = "  ", note = " paid in cash " });
        var after = DateTime.UtcNow;
        Assert.Equal("Credits", credits.GetProperty("kind").GetString());
        Assert.Equal("۵۰۰ اعتبار", credits.GetProperty("name").GetString());
        Assert.Equal(500, credits.GetProperty("amount").GetInt32());
        Assert.Equal("Active", credits.GetProperty("state").GetString());
        Assert.Equal(0, credits.GetProperty("usedToday").GetInt32());
        Assert.Equal(500, credits.GetProperty("remaining").GetInt32());
        Assert.False(credits.GetProperty("endedEarly").GetBoolean());
        Assert.Equal("paid in cash", credits.GetProperty("note").GetString());
        var starts = At(credits, "startsAt");
        Assert.InRange(starts, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(TimeSpan.FromDays(30), At(credits, "endsAt") - starts);
        At(credits, "createdAt");
        Assert.Equal(ownerId, await world.DbAsync(db => db.CreditPackages.Where(p => p.Id == credits.GetProperty("id").GetGuid()).Select(p => p.ByUserId).SingleAsync()));

        var unlimited = await GrantAsync(owner, sara, new { kind = "Unlimited", amount = 0, days = 7 });
        Assert.Equal(("نامحدود", JsonValueKind.Null), (unlimited.GetProperty("name").GetString(), unlimited.GetProperty("remaining").ValueKind));
        var capped = await GrantAsync(owner, sara, new { kind = "unlimited", amount = 30, days = 7 });
        Assert.Equal(("نامحدود، روزی تا ۳۰", 30), (capped.GetProperty("name").GetString(), capped.GetProperty("remaining").GetInt32()));
        var daily = await GrantAsync(owner, sara, new { kind = "Daily", amount = 5, days = 7 });
        Assert.Equal(("روزی ۵ تصویر", 5), (daily.GetProperty("name").GetString(), daily.GetProperty("remaining").GetInt32()));
        Assert.Equal(JsonValueKind.Null, daily.GetProperty("note").ValueKind);
        var named = await GrantAsync(owner, sara, new { kind = "Daily", amount = 5, days = 7, name = "  Gold  " });
        Assert.Equal("Gold", named.GetProperty("name").GetString());

        var listed = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{sara}/packages");
        Assert.Equal(["Gold", "روزی ۵ تصویر", "نامحدود، روزی تا ۳۰", "نامحدود", "۵۰۰ اعتبار"], Names(listed));
        Assert.Equal("paid in cash", listed[4].GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_package_that_cannot_be_given_is_refused_with_one_code()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var ownerId = await world.DbAsync(db => db.Users.Where(u => u.NormalizedUsername == OwnerBootstrap.Username).Select(u => u.Id).SingleAsync());
        object[] bad =
        [
            new { kind = "Credits", amount = 0, days = 30 }, new { kind = "Credits", amount = 1_000_001, days = 30 },
            new { kind = "Unlimited", amount = -1, days = 30 }, new { kind = "Unlimited", amount = 10_001, days = 30 },
            new { kind = "Daily", amount = 0, days = 30 }, new { kind = "Daily", amount = 10_001, days = 30 },
            new { kind = "Daily", amount = 5, days = 0 }, new { kind = "Daily", amount = 5, days = 3651 }, new { kind = "Daily", amount = 5 },
            new { kind = "Weekly", amount = 5, days = 30 }, new { kind = "1", amount = 5, days = 30 }, new { amount = 5, days = 30 },
            new { kind = "Daily", amount = 5, days = 30, name = new string('x', 81) },
            new { kind = "Daily", amount = 5, days = 30, note = new string('x', 401) },
        ];
        foreach (var body in bad) Assert.Equal("InvalidPackage", await InvalidAsync(await owner.PostAsJsonAsync($"/api/users/{sara}/packages", body)));
        // The owner is never charged, so a package for them would be one that does nothing.
        Assert.Equal("InvalidPackage", await InvalidAsync(await owner.PostAsJsonAsync($"/api/users/{ownerId}/packages", new { kind = "Daily", amount = 5, days = 30 })));
        Assert.Equal(0, await world.DbAsync(db => db.CreditPackages.CountAsync()));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/packages", new { kind = "Daily", amount = 5, days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/users/{Guid.NewGuid()}/packages")).StatusCode);

        // The edges themselves are allowed.
        await GrantAsync(owner, sara, new { kind = "Credits", amount = 1_000_000, days = 3650, name = new string('x', 80), note = new string('x', 400) });
        await GrantAsync(owner, sara, new { kind = "Unlimited", amount = 10_000, days = 1 });
        var daily = await GrantAsync(owner, sara, new { kind = "Daily", amount = 1, days = 1 });
        var id = daily.GetProperty("id").GetGuid();

        Assert.Equal("InvalidPackage", await InvalidAsync(await owner.PostAsJsonAsync($"/api/users/{sara}/packages/{id}/extend", new { days = 0 })));
        Assert.Equal("InvalidPackage", await InvalidAsync(await owner.PostAsJsonAsync($"/api/users/{sara}/packages/{id}/extend", new { days = 3651 })));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/users/{sara}/packages/{Guid.NewGuid()}/extend", new { days = 5 })).StatusCode);
        // A package is reached only through the person it belongs to.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/users/{ownerId}/packages/{id}/extend", new { days = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/users/{ownerId}/packages/{id}/end", null)).StatusCode);
    }

    [Fact]
    public async Task Starting_after_the_current_one_lines_the_new_package_up_behind_its_own_kind()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var first = await GrantAsync(owner, sara, new { kind = "Daily", amount = 3, days = 30 });
        var second = await GrantAsync(owner, sara, new { kind = "Daily", amount = 5, days = 10, startAfterCurrent = true });
        Assert.Equal(At(first, "endsAt"), At(second, "startsAt"));
        Assert.Equal(TimeSpan.FromDays(10), At(second, "endsAt") - At(second, "startsAt"));
        Assert.Equal("Scheduled", second.GetProperty("state").GetString());
        var third = await GrantAsync(owner, sara, new { kind = "Daily", amount = 1, days = 5, startAfterCurrent = true });
        Assert.Equal(At(second, "endsAt"), At(third, "startsAt"));

        // Nothing of its own kind is running, so it starts at once.
        var before = DateTime.UtcNow;
        var credits = await GrantAsync(owner, sara, new { kind = "Credits", amount = 100, days = 30, startAfterCurrent = true });
        Assert.InRange(At(credits, "startsAt"), before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("Active", credits.GetProperty("state").GetString());
        // Without asking, it starts now beside the running one.
        var beside = await GrantAsync(owner, sara, new { kind = "Daily", amount = 2, days = 3 });
        Assert.Equal("Active", beside.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Ending_stops_a_package_now_and_extending_brings_it_back()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var id = (await GrantAsync(owner, sara, new { kind = "Daily", amount = 2, days = 30 })).GetProperty("id").GetGuid();

        var ended = await PostAsync(owner, $"/api/users/{sara}/packages/{id}/end");
        Assert.Equal(("Ended", true), (ended.GetProperty("state").GetString(), ended.GetProperty("endedEarly").GetBoolean()));
        Assert.True(At(ended, "endsAt") <= DateTime.UtcNow);
        // Ending it again changes nothing.
        Assert.Equal(At(ended, "endsAt"), At(await PostAsync(owner, $"/api/users/{sara}/packages/{id}/end"), "endsAt"));

        await world.DbAsync(db => db.CreditPackages.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ReminderSentAt, DateTime.UtcNow).SetProperty(p => p.EndNoticeSentAt, DateTime.UtcNow)));
        var before = DateTime.UtcNow;
        var revived = await PostAsync(owner, $"/api/users/{sara}/packages/{id}/extend", new { days = 10 });
        Assert.Equal(("Active", false), (revived.GetProperty("state").GetString(), revived.GetProperty("endedEarly").GetBoolean()));
        Assert.InRange(At(revived, "endsAt"), before.AddDays(10).AddSeconds(-1), DateTime.UtcNow.AddDays(10).AddSeconds(1));
        var row = await world.DbAsync(db => db.CreditPackages.AsNoTracking().SingleAsync(p => p.Id == id));
        Assert.Equal((null, null, null), (row.EndedEarlyAt, row.ReminderSentAt, row.EndNoticeSentAt));

        var longer = await PostAsync(owner, $"/api/users/{sara}/packages/{id}/extend", new { days = 5 });
        Assert.Equal(At(revived, "endsAt").AddDays(5), At(longer, "endsAt"));

        var scheduled = await GrantAsync(owner, sara, new { kind = "Daily", amount = 9, days = 10, startAfterCurrent = true });
        Assert.Equal("Scheduled", scheduled.GetProperty("state").GetString());
        var stopped = await PostAsync(owner, $"/api/users/{sara}/packages/{scheduled.GetProperty("id").GetGuid()}/end");
        Assert.Equal("Ended", stopped.GetProperty("state").GetString());
        Assert.True(At(stopped, "startsAt") <= At(stopped, "endsAt"));
    }

    [Fact]
    public async Task A_member_reads_their_own_packages_running_first_and_never_the_owners_note()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, member) = await world.MemberAsync("sara");
        var (omid, _) = await world.MemberAsync("omid");
        var now = DateTime.UtcNow;
        await AddAsync(world, sara, "later", now.AddDays(-1), now.AddDays(5), created: now.AddMinutes(-5));
        await AddAsync(world, sara, "sooner", now.AddDays(-1), now.AddDays(2), created: now.AddMinutes(-4));
        await AddAsync(world, sara, "next", now.AddDays(3), now.AddDays(33), created: now.AddMinutes(-3));
        await AddAsync(world, sara, "just ended", now.AddDays(-30), now.AddDays(-2), created: now.AddMinutes(-2));
        await AddAsync(world, sara, "long gone", now.AddDays(-90), now.AddDays(-40), created: now.AddMinutes(-1));
        await AddAsync(world, omid, "not hers", now.AddDays(-1), now.AddDays(5));

        var mine = await member.GetFromJsonAsync<JsonElement>("/api/packages");
        Assert.Equal(["sooner", "later", "next", "just ended"], Names(mine));
        Assert.Equal(["Active", "Active", "Scheduled", "Ended"], mine.EnumerateArray().Select(p => p.GetProperty("state").GetString()!));
        Assert.All(mine.EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("note").ValueKind));

        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var all = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{sara}/packages");
        Assert.Equal(["long gone", "just ended", "next", "sooner", "later"], Names(all));
        Assert.All(all.EnumerateArray(), p => Assert.Equal("owner only", p.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task The_user_list_shows_each_persons_running_and_coming_packages_beside_the_regular_balance()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara", credits: 100);
        var now = DateTime.UtcNow;
        await AddAsync(world, sara, "later", now.AddDays(-1), now.AddDays(5));
        var sooner = await AddAsync(world, sara, "sooner", now.AddDays(-1), now.AddDays(2), PackageKind.Credits, 50);
        await AddAsync(world, sara, "next", now.AddDays(3), now.AddDays(33));
        await AddAsync(world, sara, "ended", now.AddDays(-30), now.AddDays(-2));
        await world.DbAsync(db => { db.CreditEntries.Add(new CreditEntry { UserId = sara, Amount = -20, Kind = CreditKind.Charge, PackageId = sooner }); return db.SaveChangesAsync(); });

        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var users = await owner.GetFromJsonAsync<JsonElement>("/api/users");
        var row = users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == sara);
        Assert.Equal(100, row.GetProperty("credits").GetInt32());
        Assert.Equal(["sooner", "later", "next"], Names(row.GetProperty("packages")));
        Assert.Equal(30, row.GetProperty("packages")[0].GetProperty("remaining").GetInt32());
        Assert.Equal(0, users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() != sara).GetProperty("packages").GetArrayLength());

        var patched = await owner.PatchAsJsonAsync($"/api/users/{sara}", new { dailyQuota = 5 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = await ApiV1World.JsonAsync(patched);
        Assert.Equal(["sooner", "later", "next"], Names(updated.GetProperty("packages")));
        Assert.Equal(100, updated.GetProperty("credits").GetInt32());

        var created = await owner.PostAsJsonAsync("/api/users", new { username = "neda", password = "neda-strong-pass" });
        Assert.Equal(0, (await ApiV1World.JsonAsync(created)).GetProperty("packages").GetArrayLength());

        // The usage report agrees: what is left is the regular balance, while what was spent counts the package's credits too.
        var usage = (await owner.GetFromJsonAsync<JsonElement>("/api/usage")).GetProperty("users").EnumerateArray().Single(u => u.GetProperty("userId").GetGuid() == sara);
        Assert.Equal((100, 20), (usage.GetProperty("creditsLeft").GetInt32(), usage.GetProperty("creditsSpent").GetInt32()));
    }

    [Fact]
    public async Task A_request_paid_by_a_package_names_it_in_the_history_the_ledger_and_me()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, member) = await world.MemberAsync("sara", credits: 0);
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var gold = (await GrantAsync(owner, sara, new { kind = "Credits", amount = 10, days = 30, name = "Gold", note = "cash" })).GetProperty("id").GetGuid();
        await GrantAsync(owner, sara, new { kind = "Daily", amount = 1, days = 10, name = "One a day" });

        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v1/chats", new { prompt = "first" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v1/chats", new { prompt = "second" })).StatusCode);
        var refused = await member.PostAsJsonAsync("/api/v1/chats", new { prompt = "third" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("PackageLimitReached", await ApiV1World.ErrorAsync(refused));

        var requests = await owner.GetFromJsonAsync<JsonElement>($"/api/usage/users/{sara}/requests");
        Assert.Equal(["Gold", "One a day"], requests.EnumerateArray().Select(r => r.GetProperty("packageName").GetString()!));

        var ledger = await member.GetFromJsonAsync<JsonElement>("/api/credits");
        Assert.Equal(0, ledger.GetProperty("balance").GetInt32());
        var charge = ledger.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal((-10, gold, "Gold"), (charge.GetProperty("amount").GetInt32(), charge.GetProperty("packageId").GetGuid(), charge.GetProperty("packageName").GetString()));
        var owners = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{sara}/credits");
        Assert.Equal("Gold", owners.GetProperty("entries")[0].GetProperty("packageName").GetString());

        var me = await member.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(0, me.GetProperty("credits").GetInt32());
        var packages = me.GetProperty("packages");
        Assert.Equal(["One a day", "Gold"], Names(packages));
        Assert.Equal((1, 0), (packages[0].GetProperty("usedToday").GetInt32(), packages[0].GetProperty("remaining").GetInt32()));
        Assert.Equal((0, 0), (packages[1].GetProperty("usedToday").GetInt32(), packages[1].GetProperty("remaining").GetInt32()));
        Assert.All(packages.EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("note").ValueKind));
        // The session's credits are the regular balance, as before.
        Assert.Equal(0, (await member.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("credits").GetInt32());
    }

    [Fact]
    public async Task Daily_usage_counts_each_local_day_oldest_first_with_quiet_days_as_zero()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, member) = await world.MemberAsync("sara");
        DateTime Local(int daysAgo, int hour) => DateTime.Today.AddDays(-daysAgo).AddHours(hour).ToUniversalTime();
        await world.DbAsync(async db =>
        {
            var project = new ImageProject { UserId = sara, Title = "p" };
            db.Projects.Add(project);
            foreach (var (status, at) in new[] { (RunStatus.Completed, Local(0, 0)), (RunStatus.Failed, Local(0, 0)), (RunStatus.Answered, Local(0, 0)),
                                                 (RunStatus.Completed, Local(2, 23)), (RunStatus.Completed, Local(3, 12)) })
                db.Generations.Add(new Generation { ProjectId = project.Id, UserId = sara, Prompt = "x", Status = status, CreatedAt = at });
            return await db.SaveChangesAsync();
        });
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        var days = await owner.GetFromJsonAsync<JsonElement>("/api/usage/daily?days=3");
        string Day(int daysAgo) => DateTime.Today.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal([Day(2), Day(1), Day(0)], days.EnumerateArray().Select(d => d.GetProperty("date").GetString()!));
        Assert.Equal([(1, 1, 0), (0, 0, 0), (3, 1, 1)],
            days.EnumerateArray().Select(d => (d.GetProperty("requests").GetInt32(), d.GetProperty("images").GetInt32(), d.GetProperty("failed").GetInt32())));

        var fortnight = await owner.GetFromJsonAsync<JsonElement>("/api/usage/daily");
        Assert.Equal(14, fortnight.GetArrayLength());
        Assert.Equal(Day(0), fortnight[13].GetProperty("date").GetString());
        Assert.Equal(90, (await owner.GetFromJsonAsync<JsonElement>("/api/usage/daily?days=90")).GetArrayLength());
        foreach (var bad in new[] { "0", "91", "-3", "abc" })
            Assert.Equal("InvalidPeriod", await InvalidAsync(await owner.GetAsync($"/api/usage/daily?days={bad}")));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/usage/daily")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_person_takes_their_packages_with_them()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var owner = await UserAdminTests.OwnerSessionAsync(world.Factory);
        await GrantAsync(owner, sara, new { kind = "Daily", amount = 3, days = 30 });
        await GrantAsync(owner, sara, new { kind = "Credits", amount = 30, days = 30 });
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/users/{sara}")).StatusCode);
        Assert.Equal(0, await world.DbAsync(db => db.CreditPackages.CountAsync()));
    }
}
