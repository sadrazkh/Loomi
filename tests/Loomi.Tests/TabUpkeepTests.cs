using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>A tab kept from one run to the next grows: a chat started in place leaves the previous one in the page. On the live server the kernel killed
/// such tabs twice, once with nothing running at all. So a tab is loaded afresh now and then, one left idle is cleaned up, and one that dies before the
/// prompt has gone costs the person nothing.</summary>
public class TabUpkeepTests
{
    private const string Home = "https://chatgpt.com/";
    private static readonly string Body = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <button aria-label="New chat" onclick="history.pushState({}, '', '/'); document.querySelectorAll('[data-message-author-role]').forEach(e => e.remove());">New chat</button>
        <textarea id="prompt-textarea"></textarea>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        window.sends = 0;
        function send() {
          window.sends++;
          if (!location.pathname.startsWith('/c/')) history.replaceState({}, '', '/c/fixture-' + window.sends);
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = ['#7aa5cf', '#cf7a7a', '#7acf8f', '#cfc27a'][(window.sends + Math.floor(Math.random() * 4)) % 4]; ctx.fillRect(Math.random() * 200, 0, 256, 256);
          const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
          reply.append(image); document.body.append(reply);
        }
        document.addEventListener('keydown', e => { if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'o') { history.pushState({}, '', '/'); document.querySelectorAll('[data-message-author-role]').forEach(e => e.remove()); } });
        </script></body></html>
        """;
    private static BrowserSession Session(FixtureLauncher launcher, Action<BrowserOptions>? set = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-upkeep-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        var options = new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 30, ConversationLoadSeconds = 2 };
        set?.Invoke(options);
        return new BrowserSession("default", Options.Create(options), config, launcher);
    }
    private static Task Report(RunStatus status, string? conversation = null) => Task.CompletedTask;
    private static int Loads(FixtureLauncher launcher, string url) => launcher.Visits.Count(v => v == url);
    private static async Task UntilAsync(Func<bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(50); }
    }

    [Fact]
    public async Task After_the_set_number_of_runs_the_next_new_chat_loads_the_site_afresh()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher, o => o.RunsPerLoad = 2);
        await session.RunAsync(Operation.Generate, "one", [], null, Report, CancellationToken.None);
        await session.RunAsync(Operation.Generate, "two", [], null, Report, CancellationToken.None);
        Assert.Equal(1, Loads(launcher, Home));
        await session.RunAsync(Operation.Generate, "three", [], null, Report, CancellationToken.None);
        Assert.Equal(2, Loads(launcher, Home));
        await session.RunAsync(Operation.Generate, "four", [], null, Report, CancellationToken.None);
        Assert.Equal(2, Loads(launcher, Home));
    }

    [Fact]
    public async Task A_follow_up_stays_where_it_is_however_many_runs_the_page_has_taken()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher, o => o.RunsPerLoad = 1);
        var first = await session.RunAsync(Operation.Generate, "one", [], null, Report, CancellationToken.None);
        await session.RunAsync(Operation.Reply, "two", [], first.ConversationUrl, Report, CancellationToken.None);
        await session.RunAsync(Operation.Reply, "three", [], first.ConversationUrl, Report, CancellationToken.None);
        Assert.Equal(0, Loads(launcher, first.ConversationUrl!));
    }

    [Fact]
    public async Task A_tab_grown_past_its_limit_is_loaded_afresh_even_for_a_follow_up()
    {
        // Enough of the page's own memory to pass the limit set here, whatever a blank page takes.
        var heavy = Body.Replace("window.sends = 0;", "window.sends = 0; window.ballast = Array.from({ length: 6000000 }, (_, i) => i + 0.5);");
        await using var launcher = new FixtureLauncher { Body = heavy };
        await using var session = Session(launcher, o => o.HeapLimitMb = 20);
        var first = await session.RunAsync(Operation.Generate, "one", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Reply, "two", [], first.ConversationUrl, Report, CancellationToken.None);
        Assert.NotNull(second.Image);
        Assert.Equal(1, Loads(launcher, first.ConversationUrl!));
    }

    [Fact]
    public async Task A_tab_left_idle_goes_back_to_a_clean_page_once()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher, o => o.IdleTrimSeconds = 1);
        await session.RunAsync(Operation.Generate, "one", [], null, Report, CancellationToken.None);
        await session.TrimIfIdleAsync(CancellationToken.None);
        Assert.Equal(1, Loads(launcher, Home));
        await Task.Delay(1200);
        await session.TrimIfIdleAsync(CancellationToken.None);
        Assert.Equal(2, Loads(launcher, Home));
        Assert.Equal("Connected", session.State);
        await Task.Delay(1200);
        await session.TrimIfIdleAsync(CancellationToken.None);
        Assert.Equal(2, Loads(launcher, Home));
    }

    [Fact]
    public async Task A_tab_at_work_is_never_trimmed()
    {
        await using var launcher = new FixtureLauncher { Body = Body.Replace("function send() {", "function send() { history.replaceState({}, '', '/c/fixture-1'); return;") };
        await using var session = Session(launcher, o => o.IdleTrimSeconds = 0);
        using var cancel = new CancellationTokenSource();
        var running = false;
        var run = session.RunAsync(Operation.Generate, "one", [], null, (status, _) => { running |= status == RunStatus.WaitingForResponse; return Task.CompletedTask; }, cancel.Token);
        await UntilAsync(() => running);
        await session.TrimIfIdleAsync(CancellationToken.None);
        Assert.Equal(1, Loads(launcher, Home));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_tab_that_died_while_idle_is_brought_back_by_itself()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher, o => o.IdleTrimSeconds = 0);
        await session.RunAsync(Operation.Generate, "one", [], null, Report, CancellationToken.None);
        var tab = launcher.Context.Pages[0];
        _ = (await launcher.Context.NewCDPSessionAsync(tab)).SendAsync("Page.crash");
        await UntilAsync(() => session.State == "Disconnected");
        await session.TrimIfIdleAsync(CancellationToken.None);
        Assert.Equal("Connected", session.State);
        var next = await session.RunAsync(Operation.Generate, "two", [], null, Report, CancellationToken.None);
        Assert.NotNull(next.Image);
    }

    [Fact]
    public async Task A_tab_that_dies_before_the_prompt_goes_costs_only_a_fresh_start()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher);
        var crashes = 0;
        var result = await session.RunAsync(Operation.Generate, "one", [], null, async (status, url) =>
        {
            if (status != RunStatus.SendingPrompt || crashes++ > 0) return;
            var tab = launcher.Context.Pages[0];
            _ = (await launcher.Context.NewCDPSessionAsync(tab)).SendAsync("Page.crash");
            await UntilAsync(() => tab.IsClosed);
        }, CancellationToken.None);
        Assert.Equal(2, crashes);
        Assert.NotNull(result.Image);
        Assert.Equal("https://chatgpt.com/c/fixture-1", result.ConversationUrl);
    }

    [Fact]
    public async Task A_second_death_before_the_prompt_goes_is_the_end_of_it()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher);
        var crashes = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(Operation.Generate, "one", [], null, async (status, url) =>
        {
            if (status != RunStatus.SendingPrompt) return;
            crashes++;
            var tab = launcher.Context.Pages[0];
            _ = (await launcher.Context.NewCDPSessionAsync(tab)).SendAsync("Page.crash");
            await UntilAsync(() => tab.IsClosed);
        }, CancellationToken.None));
        Assert.Equal(2, crashes);
    }

    [Fact]
    public async Task A_tab_that_dies_as_send_is_pressed_is_not_tried_again_because_the_prompt_may_have_gone()
    {
        // The press never comes back: the page's thread is stuck in the click, and the tab dies there.
        var stuck = Body.Replace("function send() {", "function send() { window.pressed = true; const until = Date.now() + 60000; while (Date.now() < until) { }");
        await using var launcher = new FixtureLauncher { Body = stuck };
        await using var session = Session(launcher);
        var sending = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(Operation.Generate, "one", [], null, async (status, url) =>
        {
            if (status != RunStatus.SendingPrompt) return;
            sending++;
            var cdp = await launcher.Context.NewCDPSessionAsync(launcher.Context.Pages[0]);
            _ = Task.Run(async () => { await Task.Delay(1500); await cdp.SendAsync("Page.crash"); });
        }, CancellationToken.None));
        Assert.Equal(1, sending);
    }
}
