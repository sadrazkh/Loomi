using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Loomi.Services.QueueEstimates;

namespace Loomi.Tests;
/// <summary>Several testers share one account's queue. It takes turns between people, and each person is told only how many requests are ahead of
/// theirs and roughly when their turn comes, never whose they are.</summary>
public class QueueTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
    private static readonly DateTime T = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private sealed record Row(string Name, Guid User, DateTime Created);

    [Fact]
    public void One_persons_pile_of_requests_does_not_hold_back_someone_who_asked_later()
    {
        Row[] queued = [new("a1", A, T), new("a2", A, T.AddSeconds(1)), new("a3", A, T.AddSeconds(2)), new("b1", B, T.AddSeconds(3)), new("c1", C, T.AddSeconds(4)), new("b2", B, T.AddSeconds(5))];
        Assert.Equal(["a1", "b1", "c1", "a2", "b2", "a3"], FairOrder(queued, r => r.User, r => r.Created, new Dictionary<Guid, int>()).Select(r => r.Name));
    }

    [Fact]
    public void Work_already_running_counts_as_that_persons_turn()
    {
        Row[] queued = [new("a2", A, T), new("a3", A, T.AddSeconds(1)), new("b1", B, T.AddSeconds(2))];
        Assert.Equal(["b1", "a2", "a3"], FairOrder(queued, r => r.User, r => r.Created, new Dictionary<Guid, int> { [A] = 1 }).Select(r => r.Name));
    }

    [Fact]
    public void With_one_idle_account_each_request_waits_one_run_longer_than_the_one_before()
    {
        var order = Waits(3, Provider.ChatGPT);
        var positions = Estimate(order, Lanes(Provider.ChatGPT, 0), _ => 60);
        Assert.Equal([(0, 0), (1, 60), (2, 120)], order.Select(w => (positions[w.Id].Ahead, positions[w.Id].EtaSeconds ?? -1)));
    }

    [Fact]
    public void A_busy_account_is_waited_out_and_the_estimate_moves_in_half_minute_steps()
    {
        var order = Waits(2, Provider.ChatGPT);
        var positions = Estimate(order, Lanes(Provider.ChatGPT, 20), _ => 60);
        Assert.Equal([30, 90], order.Select(w => positions[w.Id].EtaSeconds ?? -1));
    }

    [Fact]
    public void Two_accounts_halve_the_wait()
    {
        var order = Waits(4, Provider.ChatGPT);
        var positions = Estimate(order, Lanes(Provider.ChatGPT, 0, 0), _ => 60);
        Assert.Equal([(0, 0), (1, 0), (2, 60), (3, 60)], order.Select(w => (positions[w.Id].Ahead, positions[w.Id].EtaSeconds ?? -1)));
    }

    [Fact]
    public void Without_any_account_that_can_take_the_work_the_count_is_still_given_but_no_time()
    {
        var order = Waits(2, Provider.ChatGPT);
        var positions = Estimate(order, new Dictionary<Provider, List<double>>(), _ => 60);
        Assert.Equal([(0, (int?)null), (1, (int?)null)], order.Select(w => (positions[w.Id].Ahead, positions[w.Id].EtaSeconds)));
    }

    [Fact]
    public void Only_requests_for_the_same_provider_are_ahead()
    {
        List<Waiting> order = [new(Guid.NewGuid(), A, Provider.ChatGPT), new(Guid.NewGuid(), B, Provider.Gemini), new(Guid.NewGuid(), C, Provider.ChatGPT)];
        var positions = Estimate(order, new Dictionary<Provider, List<double>> { [Provider.ChatGPT] = [0], [Provider.Gemini] = [0] }, _ => 60);
        Assert.Equal([0, 0, 1], order.Select(w => positions[w.Id].Ahead));
    }

    [Fact]
    public void Only_people_whose_numbers_moved_are_told_and_someone_whose_work_left_the_queue_is_told_it_is_empty()
    {
        var estimates = new QueueEstimates();
        var order = new List<Waiting> { new(Guid.NewGuid(), A, Provider.ChatGPT), new(Guid.NewGuid(), B, Provider.ChatGPT) };
        var lanes = Lanes(Provider.ChatGPT, 0);
        Assert.Equal(2, estimates.Publish(order, Estimate(order, lanes, _ => 60)).Count);
        Assert.Empty(estimates.Publish(order, Estimate(order, lanes, _ => 60)));
        // A's request was taken: A hears the queue is empty for them, and B hears they moved up.
        var next = order.Skip(1).ToList();
        var told = estimates.Publish(next, Estimate(next, lanes, _ => 60)).ToDictionary(x => x.User, x => x.Positions);
        Assert.Empty(told[A]);
        Assert.Equal(0, Assert.Single(told[B]).Ahead);
        Assert.Empty(estimates.For(A));
    }

    [Fact]
    public void Timings_are_learned_from_answered_runs_only()
    {
        var estimates = new QueueEstimates();
        Assert.Equal(60, estimates.Average(Provider.ChatGPT));
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        estimates.Started(first, Provider.ChatGPT, T); estimates.Finished(first, answered: true, T.AddSeconds(40));
        estimates.Started(second, Provider.ChatGPT, T); estimates.Finished(second, answered: false, T.AddMinutes(10));
        Assert.Equal(40, estimates.Average(Provider.ChatGPT));
        estimates.Started(first, Provider.ChatGPT, T);
        Assert.Equal(25, estimates.Remaining(first, T.AddSeconds(15)));
        Assert.Equal(5, estimates.Remaining(first, T.AddSeconds(90)));
    }

    [Fact]
    public async Task The_worker_lets_a_later_person_go_next_and_tells_everyone_only_their_count()
    {
        await using var launcher = new DispatchLauncher { Hold = true };
        using var factory = new DispatchFactory(launcher);
        Guid owner, member, a1, a2, a3, b1;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            owner = (await db.Users.FirstAsync(u => u.NormalizedUsername == OwnerBootstrap.Username)).Id;
            var tester = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
            db.Users.Add(tester); member = tester.Id;
            db.Accounts.Add(new ProviderAccount { Label = "only", ProfileDirectory = "only", DailyCap = 25 });
            var mine = new ImageProject { UserId = owner, Title = "mine" }; var theirs = new ImageProject { UserId = member, Title = "theirs" };
            db.Projects.AddRange(mine, theirs);
            var now = DateTime.UtcNow;
            Generation Queue(ImageProject p, int second) { var g = new Generation { ProjectId = p.Id, UserId = p.UserId, Prompt = $"at {second}", CreatedAt = now.AddSeconds(second - 10) }; db.Generations.Add(g); return g; }
            (a1, a2, a3, b1) = (Queue(mine, 0).Id, Queue(mine, 1).Id, Queue(mine, 2).Id, Queue(theirs, 3).Id);
            await db.SaveChangesAsync();
        }
        var estimates = factory.Services.GetRequiredService<QueueEstimates>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((launcher.Held < 1 || estimates.For(member).Length == 0 || estimates.For(owner).Length < 2) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal(1, launcher.Held);
        var theirsNow = Assert.Single(estimates.For(member));
        Assert.Equal((b1, 0), (theirsNow.Id, theirsNow.Ahead));
        Assert.NotNull(theirsNow.EtaSeconds);
        Assert.Equal([(a2, 1), (a3, 2)], estimates.For(owner).OrderBy(p => p.Ahead).Select(p => (p.Id, p.Ahead)));
        launcher.Release();
    }

    private static List<Waiting> Waits(int count, Provider provider) => Enumerable.Range(0, count).Select(_ => new Waiting(Guid.NewGuid(), Guid.NewGuid(), provider)).ToList();
    private static Dictionary<Provider, List<double>> Lanes(Provider provider, params double[] free) => new() { [provider] = [.. free] };
}
