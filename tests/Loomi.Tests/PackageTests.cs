using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>Who pays for a request once a member holds packages: an Unlimited one, then a Daily one, then a Credits one, then the regular balance, each
/// soonest-ending first. A package only counts while it runs, a Credits one must cover the whole price, and a refund goes back where the charge came from.</summary>
public class PackageTests
{
    /// <summary>What the owner's bootstrap prices a ChatGPT request at.</summary>
    private const int Price = 10;
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static Task<AppUser> MemberAsync(AppFactory factory, int credits = 0, int quota = 0) => WithDbAsync(factory, async db =>
    {
        var user = new AppUser { Username = "sara", NormalizedUsername = "sara", Role = UserRole.Member, DailyQuota = quota };
        db.Users.Add(user);
        if (credits != 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user;
    });
    /// <summary>Running now for <paramref name="days"/> more days unless a start and end are given.</summary>
    private static Task<Guid> GrantAsync(AppFactory factory, Guid user, PackageKind kind, int amount, double days = 10, DateTime? starts = null, DateTime? ends = null) => WithDbAsync(factory, async db =>
    {
        var package = new CreditPackage { UserId = user, Kind = kind, Amount = amount, Name = $"{kind} {amount}", StartsAt = starts ?? DateTime.UtcNow.AddDays(-1), EndsAt = ends ?? DateTime.UtcNow.AddDays(days) };
        db.CreditPackages.Add(package);
        await db.SaveChangesAsync();
        return package.Id;
    });
    private static async Task<Generation> SubmitAsync(AppFactory factory, AppUser user)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GenerationService>().StartAsync(Viewer.For(user), null, "a lighthouse", null, null, default);
    }
    private static async Task<string> RefusedAsync(AppFactory factory, AppUser user) => (await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(factory, user))).Message;
    private static Task<Generation> ReadAsync(AppFactory factory, Guid id) => WithDbAsync(factory, db => db.Generations.AsNoTracking().SingleAsync(g => g.Id == id));
    private static Task<int> SetStatusAsync(AppFactory factory, Guid id, RunStatus status) =>
        WithDbAsync(factory, db => db.Generations.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, status)));
    private static Task<int> BalanceAsync(AppFactory factory, Guid user) => WithDbAsync(factory, db => db.BalanceAsync(user, default));
    /// <summary>A Credits package's own money: what it was given plus every charge and refund made against it.</summary>
    private static Task<int> LeftInAsync(AppFactory factory, Guid package) => WithDbAsync(factory, async db =>
        await db.CreditPackages.Where(p => p.Id == package).Select(p => p.Amount).SingleAsync() + await db.CreditEntries.Where(e => e.PackageId == package).SumAsync(e => e.Amount));
    /// <summary>A request of the package's, made at <paramref name="at"/> and already finished.</summary>
    private static Task<int> UsedAsync(AppFactory factory, Guid user, Guid package, DateTime at, RunStatus status = RunStatus.Completed) => WithDbAsync(factory, async db =>
    {
        var project = new ImageProject { UserId = user, Title = "earlier" };
        db.Projects.Add(project);
        db.Generations.Add(new Generation { ProjectId = project.Id, UserId = user, Prompt = "earlier", Status = status, CreatedAt = at, PackageId = package });
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Unlimited_pays_first_then_daily_then_package_credits_then_the_balance()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, credits: Price);
        var unlimited = await GrantAsync(factory, sara.Id, PackageKind.Unlimited, 1);
        var daily = await GrantAsync(factory, sara.Id, PackageKind.Daily, 1);
        var credits = await GrantAsync(factory, sara.Id, PackageKind.Credits, Price);

        var first = await SubmitAsync(factory, sara);
        Assert.Equal((unlimited, 0), (first.PackageId!.Value, first.CreditCost));
        var second = await SubmitAsync(factory, sara);
        Assert.Equal((daily, 0), (second.PackageId!.Value, second.CreditCost));
        // Free use leaves nothing in the ledger.
        Assert.Equal(1, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));

        var third = await SubmitAsync(factory, sara);
        Assert.Equal((credits, Price), (third.PackageId!.Value, third.CreditCost));
        var charge = await WithDbAsync(factory, db => db.CreditEntries.AsNoTracking().SingleAsync(e => e.GenerationId == third.Id));
        Assert.Equal((CreditKind.Charge, -Price, (Guid?)credits), (charge.Kind, charge.Amount, charge.PackageId));
        Assert.Equal(0, await LeftInAsync(factory, credits));
        Assert.Equal(Price, await BalanceAsync(factory, sara.Id));

        var fourth = await SubmitAsync(factory, sara);
        Assert.Equal((null, Price), (fourth.PackageId, fourth.CreditCost));
        Assert.Null(await WithDbAsync(factory, db => db.CreditEntries.Where(e => e.GenerationId == fourth.Id).Select(e => e.PackageId).SingleAsync()));
        Assert.Equal(0, await BalanceAsync(factory, sara.Id));

        // Everything is spent while a package with a daily allowance is running: the allowance is what ran out, and tomorrow brings it back.
        Assert.Equal("PackageLimitReached", await RefusedAsync(factory, sara));
    }

    [Fact]
    public async Task Of_two_packages_of_a_kind_the_one_ending_sooner_pays()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        await GrantAsync(factory, sara.Id, PackageKind.Daily, 1, days: 20);
        var sooner = await GrantAsync(factory, sara.Id, PackageKind.Daily, 1, days: 2);
        await GrantAsync(factory, sara.Id, PackageKind.Credits, 100, days: 20);
        var soonerCredits = await GrantAsync(factory, sara.Id, PackageKind.Credits, 100, days: 3);
        Assert.Equal(sooner, (await SubmitAsync(factory, sara)).PackageId);
        Assert.NotEqual(sooner, (await SubmitAsync(factory, sara)).PackageId);
        Assert.Equal(soonerCredits, (await SubmitAsync(factory, sara)).PackageId);
    }

    [Fact]
    public async Task Unlimited_without_a_cap_never_runs_out_and_charges_nothing()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var unlimited = await GrantAsync(factory, sara.Id, PackageKind.Unlimited, 0);
        for (var i = 0; i < 6; i++)
        {
            var g = await SubmitAsync(factory, sara);
            Assert.Equal((unlimited, 0), (g.PackageId!.Value, g.CreditCost));
        }
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
    }

    [Fact]
    public async Task Unlimited_with_a_cap_stops_at_the_cap_and_then_the_balance_pays()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, credits: Price);
        var unlimited = await GrantAsync(factory, sara.Id, PackageKind.Unlimited, 2);
        Assert.Equal(unlimited, (await SubmitAsync(factory, sara)).PackageId);
        Assert.Equal(unlimited, (await SubmitAsync(factory, sara)).PackageId);
        Assert.Null((await SubmitAsync(factory, sara)).PackageId);
        Assert.Equal(0, await BalanceAsync(factory, sara.Id));
        Assert.Equal("PackageLimitReached", await RefusedAsync(factory, sara));
    }

    [Fact]
    public async Task Yesterdays_use_does_not_count_against_todays_allowance()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var daily = await GrantAsync(factory, sara.Id, PackageKind.Daily, 2);
        await UsedAsync(factory, sara.Id, daily, Quota.Midnight().AddMinutes(-5));
        await UsedAsync(factory, sara.Id, daily, Quota.Midnight().AddHours(-20));
        Assert.Equal(daily, (await SubmitAsync(factory, sara)).PackageId);
        Assert.Equal(daily, (await SubmitAsync(factory, sara)).PackageId);
        Assert.Equal("PackageLimitReached", await RefusedAsync(factory, sara));
    }

    [Theory]
    [InlineData(PackageKind.Unlimited, RunStatus.Failed)]
    [InlineData(PackageKind.Daily, RunStatus.Failed)]
    [InlineData(PackageKind.Daily, RunStatus.Cancelled)]
    [InlineData(PackageKind.Daily, RunStatus.Answered)]
    public async Task A_run_that_delivered_no_picture_gives_its_slot_back(PackageKind kind, RunStatus outcome)
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var package = await GrantAsync(factory, sara.Id, kind, 1);
        var first = await SubmitAsync(factory, sara);
        Assert.Equal(package, first.PackageId);
        Assert.Equal("PackageLimitReached", await RefusedAsync(factory, sara));
        await SetStatusAsync(factory, first.Id, outcome);
        Assert.Equal(package, (await SubmitAsync(factory, sara)).PackageId);
    }

    [Fact]
    public async Task A_free_request_takes_nothing_from_any_package()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        await WithDbAsync(factory, db => db.PricingRules.ExecuteUpdateAsync(s => s.SetProperty(r => r.Cost, 0)));
        await GrantAsync(factory, sara.Id, PackageKind.Daily, 1);
        await GrantAsync(factory, sara.Id, PackageKind.Credits, 100);
        for (var i = 0; i < 3; i++)
        {
            var g = await SubmitAsync(factory, sara);
            Assert.Equal((null, 0), (g.PackageId, g.CreditCost));
        }
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
    }

    [Fact]
    public async Task The_owner_is_never_charged_and_uses_no_package()
    {
        using var factory = new AppFactory();
        var owner = await WithDbAsync(factory, db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username));
        await GrantAsync(factory, owner.Id, PackageKind.Daily, 1);
        await GrantAsync(factory, owner.Id, PackageKind.Credits, 100);
        for (var i = 0; i < 3; i++)
        {
            var g = await SubmitAsync(factory, owner);
            Assert.Equal((null, 0), (g.PackageId, g.CreditCost));
        }
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
    }

    [Fact]
    public async Task A_credits_package_must_cover_the_whole_price_and_is_never_split_with_the_balance()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, credits: Price / 2);
        var credits = await GrantAsync(factory, sara.Id, PackageKind.Credits, Price / 2);
        // Half and half would cover it, but each source pays whole or not at all; with no daily allowance running, it is plain credits that are short.
        Assert.Equal("InsufficientCredits", await RefusedAsync(factory, sara));
        Assert.Equal(Price / 2, await LeftInAsync(factory, credits));
        Assert.Equal(Price / 2, await BalanceAsync(factory, sara.Id));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
    }

    [Fact]
    public async Task The_daily_quota_is_still_a_hard_cap_whatever_the_packages_say()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, quota: 1);
        await GrantAsync(factory, sara.Id, PackageKind.Unlimited, 0);
        await SubmitAsync(factory, sara);
        Assert.Equal("QuotaExceeded", await RefusedAsync(factory, sara));
    }

    [Fact]
    public async Task Packages_that_have_ended_or_not_yet_begun_are_never_used()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var now = DateTime.UtcNow;
        await GrantAsync(factory, sara.Id, PackageKind.Daily, 5, starts: now.AddDays(-10), ends: now.AddMinutes(-1));
        await GrantAsync(factory, sara.Id, PackageKind.Unlimited, 0, starts: now.AddDays(1), ends: now.AddDays(30));
        await GrantAsync(factory, sara.Id, PackageKind.Credits, 100, starts: now.AddDays(-10), ends: now.AddMinutes(-1));
        await GrantAsync(factory, sara.Id, PackageKind.Credits, 100, starts: now.AddDays(1), ends: now.AddDays(30));
        // Nothing with a daily allowance is running, so this is the plain shortage it always was.
        Assert.Equal("InsufficientCredits", await RefusedAsync(factory, sara));
        await WithDbAsync(factory, db => { db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = Price, Kind = CreditKind.Grant }); return db.SaveChangesAsync(); });
        var paid = await SubmitAsync(factory, sara);
        Assert.Equal((null, Price), (paid.PackageId, paid.CreditCost));
    }

    [Fact]
    public async Task A_refund_goes_back_to_the_credits_package_that_paid()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var credits = await GrantAsync(factory, sara.Id, PackageKind.Credits, Price);
        var g = await SubmitAsync(factory, sara);
        Assert.Equal(0, await LeftInAsync(factory, credits));
        await SetStatusAsync(factory, g.Id, RunStatus.Failed);
        Assert.Equal(Price, await WithDbAsync(factory, db => db.RefundAsync(g.Id, default)));
        var refund = await WithDbAsync(factory, db => db.CreditEntries.AsNoTracking().SingleAsync(e => e.GenerationId == g.Id && e.Kind == CreditKind.Refund));
        Assert.Equal(credits, refund.PackageId);
        Assert.Equal(Price, await LeftInAsync(factory, credits));
        Assert.Equal(0, await BalanceAsync(factory, sara.Id));
    }

    [Fact]
    public async Task A_refund_after_the_paying_package_ended_goes_to_the_regular_balance()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        var credits = await GrantAsync(factory, sara.Id, PackageKind.Credits, Price);
        var g = await SubmitAsync(factory, sara);
        await WithDbAsync(factory, db => db.CreditPackages.Where(p => p.Id == credits).ExecuteUpdateAsync(s => s.SetProperty(p => p.EndsAt, DateTime.UtcNow.AddMinutes(-1))));
        await SetStatusAsync(factory, g.Id, RunStatus.Cancelled);
        Assert.Equal(Price, await WithDbAsync(factory, db => db.RefundAsync(g.Id, default)));
        Assert.Null(await WithDbAsync(factory, db => db.CreditEntries.Where(e => e.GenerationId == g.Id && e.Kind == CreditKind.Refund).Select(e => e.PackageId).SingleAsync()));
        // Nobody loses credits to an expiry they did not cause.
        Assert.Equal(Price, await BalanceAsync(factory, sara.Id));
    }

    [Fact]
    public async Task Free_package_use_has_nothing_to_refund()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory);
        await GrantAsync(factory, sara.Id, PackageKind.Daily, 1);
        var g = await SubmitAsync(factory, sara);
        await SetStatusAsync(factory, g.Id, RunStatus.Failed);
        Assert.Equal(0, await WithDbAsync(factory, db => db.RefundAsync(g.Id, default)));
        Assert.Equal(0, await WithDbAsync(factory, db => db.CreditEntries.CountAsync()));
    }

    [Fact]
    public async Task The_regular_balance_leaves_package_money_out()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, credits: 50);
        var credits = await GrantAsync(factory, sara.Id, PackageKind.Credits, 100);
        await WithDbAsync(factory, db =>
        {
            db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = -30, Kind = CreditKind.Charge, PackageId = credits });
            db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = 10, Kind = CreditKind.Refund, PackageId = credits });
            db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = -5, Kind = CreditKind.Charge });
            return db.SaveChangesAsync();
        });
        Assert.Equal(45, await BalanceAsync(factory, sara.Id));
        Assert.Equal(80, await LeftInAsync(factory, credits));
    }
}
