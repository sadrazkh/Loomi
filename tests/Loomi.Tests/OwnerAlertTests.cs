using Loomi.Data;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Loomi.Services.AlertRules;

namespace Loomi.Tests;
/// <summary>The owner hears on Telegram when an account needs a person, once, and again when it recovers, before a tester notices the queue stopped.</summary>
public class OwnerAlertTests
{
    private static readonly DateTime T = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.NewGuid();
    private static AlertRules Rules() => new(stallAfter: TimeSpan.FromMinutes(2), quietFor: TimeSpan.FromMinutes(15));
    private static AccountView Account(string state, bool spent = false, bool capped = false) => new(Id, "GPT1", state, spent, capped, 25);
    private static List<string> Keys(IEnumerable<Alert> alerts) => alerts.Select(a => a.Key).ToList();

    [Fact]
    public void Signing_out_is_told_once_while_it_lasts_and_the_recovery_is_told_too()
    {
        var rules = Rules();
        Assert.Empty(rules.Observe([Account("Connected")], 0, true, T));
        var told = Assert.Single(rules.Observe([Account("LoginRequired")], 0, false, T.AddSeconds(20)));
        Assert.Equal(("alertLoginRequired", "GPT1"), (told.Key, told.Args[0]));
        Assert.Empty(rules.Observe([Account("LoginRequired")], 0, false, T.AddSeconds(40)));
        Assert.Equal(["alertBack"], Keys(rules.Observe([Account("Connected")], 0, true, T.AddMinutes(5))));
    }

    [Fact]
    public void An_account_flapping_between_states_is_not_a_stream_of_messages_but_one_that_stays_down_is_told_again()
    {
        var rules = Rules();
        Assert.Equal(["alertVerificationRequired"], Keys(rules.Observe([Account("VerificationRequired")], 0, false, T)));
        Assert.Equal(["alertBack"], Keys(rules.Observe([Account("Connected")], 0, true, T.AddMinutes(1))));
        Assert.Empty(rules.Observe([Account("VerificationRequired")], 0, false, T.AddMinutes(2)));
        Assert.Empty(rules.Observe([Account("VerificationRequired")], 0, false, T.AddMinutes(10)));
        Assert.Equal(["alertVerificationRequired"], Keys(rules.Observe([Account("VerificationRequired")], 0, false, T.AddMinutes(16))));
        Assert.Equal(["alertBack"], Keys(rules.Observe([Account("Connected")], 0, true, T.AddMinutes(17))));
    }

    [Fact]
    public void A_restart_that_brings_accounts_up_normally_says_nothing()
    {
        var rules = Rules();
        Assert.Empty(rules.Observe([Account("Disconnected")], 0, true, T));
        Assert.Empty(rules.Observe([Account("Connected")], 0, true, T.AddSeconds(20)));
    }

    [Fact]
    public void A_spent_allowance_and_a_reached_cap_are_told_once_each_and_again_the_next_day()
    {
        var rules = Rules();
        Assert.Equal(["alertSpent", "alertCapped"], Keys(rules.Observe([Account("Connected", spent: true, capped: true)], 0, false, T)));
        Assert.Empty(rules.Observe([Account("Connected", spent: true, capped: true)], 0, false, T.AddMinutes(1)));
        rules.Observe([Account("Connected")], 0, true, T.AddDays(1));
        Assert.Equal(["alertCapped"], Keys(rules.Observe([Account("Connected", capped: true)], 0, false, T.AddDays(1).AddHours(5))));
    }

    [Fact]
    public void A_queue_nothing_can_take_is_told_after_two_minutes_once_and_re_armed_when_it_moves()
    {
        var rules = Rules();
        Assert.DoesNotContain(rules.Observe([Account("Connected", capped: true)], 3, false, T), a => a.Key == "alertStalled");
        Assert.Empty(rules.Observe([Account("Connected", capped: true)], 3, false, T.AddSeconds(100)));
        var stalled = Assert.Single(rules.Observe([Account("Connected", capped: true)], 4, false, T.AddSeconds(125)));
        Assert.Equal(("alertStalled", 4), (stalled.Key, (int)stalled.Args[0]));
        Assert.Empty(rules.Observe([Account("Connected", capped: true)], 4, false, T.AddMinutes(30)));
        rules.Observe([Account("Connected")], 0, true, T.AddMinutes(31));
        rules.Observe([Account("Connected", capped: true)], 1, false, T.AddMinutes(32));
        Assert.Equal(["alertStalled"], Keys(rules.Observe([Account("Connected", capped: true)], 1, false, T.AddMinutes(35))));
    }

    [Fact]
    public void Three_failures_in_a_row_are_told_once_and_anything_delivered_ends_the_streak()
    {
        var rules = Rules();
        Assert.Null(rules.Outcome(RunStatus.Failed, "GenerationTimeout"));
        Assert.Null(rules.Outcome(RunStatus.Cancelled, null));
        Assert.Null(rules.Outcome(RunStatus.Failed, "Interrupted"));
        Assert.Null(rules.Outcome(RunStatus.Failed, "UploadFailed"));
        var told = rules.Outcome(RunStatus.Failed, "GenerationTimeout");
        Assert.Equal(("alertFailures", 3, "GenerationTimeout, UploadFailed"), (told?.Key, (int)told!.Value.Args[0], (string)told.Value.Args[1]));
        Assert.Null(rules.Outcome(RunStatus.Failed, "GenerationTimeout"));
        Assert.Null(rules.Outcome(RunStatus.Answered, null));
        Assert.Null(rules.Outcome(RunStatus.Failed, "a")); Assert.Null(rules.Outcome(RunStatus.Failed, "b"));
        Assert.NotNull(rules.Outcome(RunStatus.Failed, "c"));
    }

    [Fact]
    public async Task Alerts_reach_only_the_owners_linked_chat_in_both_languages()
    {
        using var factory = new AppFactory();
        const long ownerChat = 11, memberChat = 22;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await db.Users.FirstAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
            var member = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
            db.Users.Add(member);
            db.TelegramLinks.AddRange(new TelegramLink { UserId = owner.Id, ChatId = ownerChat }, new TelegramLink { UserId = member.Id, ChatId = memberChat });
            await db.SaveChangesAsync();
        }
        var bot = new FakeTelegram();
        var events = factory.Services.GetRequiredService<GenerationEvents>();
        var worker = new OwnerAlertWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<ProviderRegistry>(),
            factory.Services.GetRequiredService<AccountHealth>(), events, bot, Options.Create(new TelegramOptions { BotToken = "test-token" }), NullLogger<OwnerAlertWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < AlertRules.FailureStreak; i++)
                await events.PublishAsync(new GenerationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RunStatus.Failed, null, "GenerationTimeout", "prompt"), CancellationToken.None);
            var (chat, text) = Assert.Single(bot.Texts);
            Assert.Equal(ownerChat, chat);
            Assert.Contains("ناموفق", text);
            Assert.Contains("failed in a row", text);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}
