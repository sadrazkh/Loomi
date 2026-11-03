using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>Loading the site again cost 10 to 16 seconds for a new chat and 25 to 55 for a follow-up on every request. The tab is now used where it
/// stands when it already shows what the run needs, and loaded only when it does not.</summary>
public class PageReuseTests
{
    /// <summary>A stand-in with the site's New chat button. Each send answers with a picture of its own colour, in a conversation the first send names.</summary>
    private const string Body = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <a data-testid="create-new-chat-button" href="/" onclick="event.preventDefault(); history.pushState({}, '', '/'); document.querySelectorAll('[data-message-author-role]').forEach(e => e.remove());">New chat</a>
        <textarea id="prompt-textarea"></textarea>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        window.sends = 0;
        function send() {
          window.sends++;
          if (!location.pathname.startsWith('/c/')) history.replaceState({}, '', '/c/fixture-' + window.sends);
          const me = document.createElement('div'); me.dataset.messageAuthorRole = 'user'; me.textContent = document.querySelector('textarea').value; document.body.append(me);
          setTimeout(() => {
            const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
            const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
            const ctx = canvas.getContext('2d'); ctx.fillStyle = ['#7aa5cf', '#cf7a7a', '#7acf8f', '#cfc27a'][window.sends % 4]; ctx.fillRect(0, 0, 256, 256);
            const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
            reply.append(image); document.body.append(reply);
          }, 300);
        }
        </script></body></html>
        """;
    /// <summary>The live site's page after New chat, as measured: the previous chat's whole form stays in the page, hidden and ahead of the new one, each
    /// with file inputs of its own. An attachment shows only in the form it was handed to.</summary>
    private const string TwoForms = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <div id="app"></div>
        <script>
        window.sends = 0; window.taken = []; let boxes = 0, current;
        function box() {
          const n = ++boxes;
          current = document.createElement('section');
          current.innerHTML = '<div class="thread"></div><form onsubmit="return false"><input type="file" onchange="attach(this, ' + n + ')"><input type="file" multiple onchange="attach(this, ' + n + ')"><div class="files"></div><textarea id="prompt-textarea"></textarea><button type="button" data-testid="send-button" onclick="send()">Send</button></form>';
          document.querySelector('#app').append(current);
        }
        function attach(input, n) {
          window.taken.push(n);
          input.closest('form').querySelector('.files').insertAdjacentHTML('beforeend', '<button type="button" aria-label="Remove file">x</button>');
        }
        function newChat() { current.style.display = 'none'; history.pushState({}, '', '/'); box(); }
        function send() {
          window.sends++;
          if (!location.pathname.startsWith('/c/')) history.replaceState({}, '', '/c/fixture-' + window.sends);
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = ['#7aa5cf', '#cf7a7a', '#7acf8f', '#cfc27a'][window.sends % 4]; ctx.fillRect(0, 0, 256, 256);
          const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
          reply.append(image); current.querySelector('.thread').append(reply);
        }
        document.addEventListener('keydown', e => { if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'o') newChat(); });
        box();
        </script></body></html>
        """;
    private static BrowserSession Session(FixtureLauncher launcher)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-reuse-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        return new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 30, ConversationLoadSeconds = 2, UploadTimeoutMs = 4000 }), config, launcher);
    }
    private static string Picture()
    {
        var path = Path.Combine(Path.GetTempPath(), "loomi-reuse-" + Guid.NewGuid() + ".png");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
    private static Task Report(RunStatus status, string? conversation = null) => Task.CompletedTask;
    private static int Loads(FixtureLauncher launcher, string url) => launcher.Visits.Count(v => v == url);

    [Fact]
    public async Task A_follow_up_in_the_chat_the_tab_is_already_on_does_not_load_it_again()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher);
        var first = await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Reply, "make it red", [], first.ConversationUrl, Report, CancellationToken.None);
        Assert.Equal("https://chatgpt.com/c/fixture-1", second.ConversationUrl);
        Assert.NotNull(second.Image);
        Assert.NotEqual(Convert.ToBase64String(first.Image!), Convert.ToBase64String(second.Image!));
        Assert.Equal(0, Loads(launcher, "https://chatgpt.com/c/fixture-1"));
    }

    [Fact]
    public async Task A_new_chat_is_made_with_the_sites_own_button_rather_than_by_loading_the_site_again()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher);
        var first = await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Generate, "draw something else", [], null, Report, CancellationToken.None);
        Assert.Equal("https://chatgpt.com/c/fixture-1", first.ConversationUrl);
        Assert.Equal("https://chatgpt.com/c/fixture-2", second.ConversationUrl);
        Assert.NotEqual(Convert.ToBase64String(first.Image!), Convert.ToBase64String(second.Image!));
        Assert.Equal(1, Loads(launcher, "https://chatgpt.com/"));
    }

    [Fact]
    public async Task A_different_conversation_is_still_loaded()
    {
        await using var launcher = new FixtureLauncher { Body = Body };
        await using var session = Session(launcher);
        await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var other = await session.RunAsync(Operation.Reply, "again", [], "https://chatgpt.com/c/earlier", Report, CancellationToken.None);
        Assert.Equal(1, Loads(launcher, "https://chatgpt.com/c/earlier"));
        Assert.Equal("https://chatgpt.com/c/earlier", other.ConversationUrl);
    }

    [Fact]
    public async Task Without_a_New_chat_button_the_site_is_loaded_as_before()
    {
        await using var launcher = new FixtureLauncher { Body = Body.Replace("data-testid=\"create-new-chat-button\"", "data-testid=\"something-else\"") };
        await using var session = Session(launcher);
        await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Generate, "draw something else", [], null, Report, CancellationToken.None);
        Assert.Equal(2, Loads(launcher, "https://chatgpt.com/"));
        Assert.NotNull(second.Image);
    }

    [Fact]
    public async Task A_picture_for_a_chat_started_in_place_goes_to_the_box_on_screen_and_not_the_hidden_one()
    {
        await using var launcher = new FixtureLauncher { Body = TwoForms };
        await using var session = Session(launcher);
        await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Generate, "like this", [Picture()], null, Report, CancellationToken.None);
        Assert.Equal("https://chatgpt.com/c/fixture-2", second.ConversationUrl);
        Assert.NotNull(second.Image);
        Assert.Equal(new[] { 2 }, await launcher.Context.Pages[0].EvaluateAsync<int[]>("() => window.taken"));
    }

    [Fact]
    public async Task Several_pictures_for_a_chat_started_in_place_all_reach_the_box_on_screen()
    {
        await using var launcher = new FixtureLauncher { Body = TwoForms };
        await using var session = Session(launcher);
        await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Generate, "like these", [Picture(), Picture()], null, Report, CancellationToken.None);
        Assert.NotNull(second.Image);
        Assert.Equal(new[] { 2, 2 }, await launcher.Context.Pages[0].EvaluateAsync<int[]>("() => window.taken"));
    }

    [Fact]
    public async Task A_follow_up_with_a_picture_goes_to_the_box_on_screen_too()
    {
        await using var launcher = new FixtureLauncher { Body = TwoForms };
        await using var session = Session(launcher);
        await session.RunAsync(Operation.Generate, "draw", [], null, Report, CancellationToken.None);
        var second = await session.RunAsync(Operation.Generate, "another", [], null, Report, CancellationToken.None);
        var third = await session.RunAsync(Operation.Edit, "with this", [Picture()], second.ConversationUrl, Report, CancellationToken.None);
        Assert.NotNull(third.Image);
        Assert.Equal(new[] { 2 }, await launcher.Context.Pages[0].EvaluateAsync<int[]>("() => window.taken"));
    }
}
