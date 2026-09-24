using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>When the model answers with words instead of a picture — a question about the prompt, a refusal, anything — the person who asked has to
/// see it and be able to answer back in the same conversation. Before this, the run sat in "waiting" for ten minutes and the words were thrown away.</summary>
public class AnsweredTests
{
    /// <summary>The site as it is now: no data-testids, a reply marked only by its markdown style, and a question instead of an image. Navigating to a
    /// conversation the site no longer has lands on the home page, which is how a lost thread looks from outside.</summary>
    public const string QuestionPage = """
        <html><body>
        <button aria-label="Open profile menu">Me</button>
        <textarea id="prompt-textarea"></textarea>
        <input type="file" multiple onchange="window.attached = (window.attached || 0) + this.files.length">
        <button aria-label="Send prompt" onclick="send()">Send</button>
        <script>
        if (location.pathname.startsWith('/c/gone')) history.replaceState({}, '', '/');
        function send() {
          history.replaceState({},'', '/c/fixture-conversation');
          window.submission = { prompt: document.querySelector('textarea').value, files: window.attached || 0 };
          const reply = document.createElement('div');
          reply.dataset.markdownTextStyle = 'assistant-message';
          reply.innerHTML = '<p>Which style do you want:</p><p>flat or photographic?</p>';
          document.body.append(reply);
        }
        </script></body></html>
        """;
    /// <summary>What an image run really does, measured on the live site: the stop control goes away several seconds before the picture lands, while a
    /// "Generating image" marker is still up. Judging the turn over when the stop control goes is what made successes a matter of luck.</summary>
    private const string SlowImagePage = """
        <html><body>
        <button aria-label="Open profile menu">Me</button>
        <textarea id="prompt-textarea"></textarea>
        <button aria-label="Send prompt" onclick="send()">Send</button>
        <script>
        function send() {
          history.replaceState({},'', '/c/slow-image');
          const stop = document.createElement('button'); stop.setAttribute('aria-label', 'Stop streaming'); stop.textContent = 'Stop'; document.body.append(stop);
          setTimeout(() => {
            stop.remove();
            const pending = document.createElement('div'); pending.setAttribute('aria-label', 'Generating image...'); pending.textContent = '…'; document.body.append(pending);
            setTimeout(() => {
              pending.remove();
              const gallery = document.createElement('div'); gallery.dataset.testid = 'generated-image-gallery';
              const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
              const ctx = canvas.getContext('2d'); ctx.fillStyle = '#d4a017'; ctx.fillRect(0, 0, 256, 256);
              const image = document.createElement('img'); image.alt = 'Generated image 1'; image.src = canvas.toDataURL('image/png');
              gallery.append(image); document.body.append(gallery);
            }, 5000);
          }, 1500);
        }
        </script></body></html>
        """;

    /// <summary>What the live site does when the model asks before it draws, measured on 2026-09-23: a short preface in the reply, then the composer is
    /// replaced by a form holding the question, a plain text field and a Skip button. Submitting the field answers the question and the picture follows.
    /// Opening the thread later shows the same pending question.</summary>
    public const string QuestionFormPage = """
        <html><body>
        <button aria-label="Open profile menu">Me</button>
        <div id="composer"><textarea id="prompt-textarea"></textarea><button aria-label="Send prompt" onclick="ask()">Send</button></div>
        <script>
        function question() {
          document.querySelector('#composer').hidden = true;
          const form = document.createElement('form'); form.id = 'question';
          form.innerHTML = '<div>What colour would you like the cat to be?</div><input placeholder="e.g. orange, black, white"><button type="button">Skip</button>';
          form.onsubmit = e => { e.preventDefault(); answer(form.querySelector('input').value); };
          document.body.append(form);
        }
        function ask() {
          history.replaceState({}, '', '/c/question-thread');
          window.submission = { prompt: document.querySelector('textarea').value };
          const reply = document.createElement('div'); reply.dataset.markdownTextStyle = 'assistant-message';
          reply.innerHTML = '<p>I will get the colour first, then create the cat poster.</p>';
          document.body.append(reply);
          setTimeout(question, 600);
        }
        function answer(value) {
          window.answered = value;
          document.querySelector('#question').remove();
          const gallery = document.createElement('div'); gallery.dataset.testid = 'generated-image-gallery';
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = '#e07b22'; ctx.fillRect(0, 0, 256, 256);
          const image = document.createElement('img'); image.alt = 'Generated image 1'; image.src = canvas.toDataURL('image/png');
          gallery.append(image); document.body.append(gallery);
        }
        if (location.pathname === '/c/question-thread') question();
        </script></body></html>
        """;

    private static (BrowserSession Session, string Root) Session(FixtureLauncher launcher, int timeout = 60)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-answered-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        return (new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = timeout }), config, launcher), root);
    }

    [Fact]
    public async Task A_question_from_the_model_comes_back_as_its_own_words()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionPage };
        var (session, root) = Session(launcher, timeout: 120);
        var started = DateTime.UtcNow;
        await using (session)
        {
            var result = await session.RunAsync(Operation.Generate, "a poster", [], null, (_, _) => Task.CompletedTask, default);
            Assert.Null(result.Image);
            Assert.Contains("Which style do you want", result.ReplyText);
            Assert.Contains("flat or photographic?", result.ReplyText);
            Assert.EndsWith("/c/fixture-conversation", result.ConversationUrl);
        }
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), $"took {(DateTime.UtcNow - started).TotalSeconds:0}s");
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_picture_still_on_its_way_after_the_stop_control_has_gone_is_waited_for()
    {
        await using var launcher = new FixtureLauncher { Body = SlowImagePage };
        var (session, root) = Session(launcher);
        await using (session)
        {
            var result = await session.RunAsync(Operation.Generate, "a yellow square", [], null, (_, _) => Task.CompletedTask, default);
            Assert.NotNull(result.Image);
            Assert.True(result.Image!.Length > 100);
            Assert.Null(result.ReplyText);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_reply_goes_into_the_same_conversation_and_sends_no_picture_of_its_own()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionPage };
        var (session, root) = Session(launcher);
        await using (session)
        {
            launcher.Visits.Clear();
            var result = await session.RunAsync(Operation.Reply, "flat, please", [], "https://chatgpt.com/c/fixture-conversation", (_, _) => Task.CompletedTask, default);
            Assert.Equal("https://chatgpt.com/c/fixture-conversation", launcher.Visits.Last(v => v.Contains("/c/")));
            Assert.Equal("flat, please", await launcher.Context.Pages[0].EvaluateAsync<string>("window.submission.prompt"));
            Assert.Equal(0, await launcher.Context.Pages[0].EvaluateAsync<int>("window.submission.files"));
            Assert.NotNull(result.ReplyText);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_conversation_the_site_no_longer_has_is_refused_rather_than_silently_started_afresh()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionPage };
        var (session, root) = Session(launcher);
        await using (session)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.RunAsync(Operation.Reply, "flat, please", [], "https://chatgpt.com/c/gone-conversation", (_, _) => Task.CompletedTask, default));
            Assert.Equal("ConversationNotSaved", failure.Message);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_question_the_site_asks_in_its_own_form_comes_back_with_the_question_itself()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionFormPage };
        var (session, root) = Session(launcher);
        await using (session)
        {
            var result = await session.RunAsync(Operation.Generate, "a cat poster", [], null, (_, _) => Task.CompletedTask, default);
            Assert.Null(result.Image);
            // The preface alone reads like progress; the question is the part the person has to answer.
            Assert.Contains("What colour would you like the cat to be?", result.ReplyText);
            Assert.Contains("I will get the colour first", result.ReplyText);
            Assert.DoesNotContain("Skip", result.ReplyText);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_reply_to_a_pending_question_goes_into_the_question_and_the_picture_that_follows_comes_back()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionFormPage };
        var (session, root) = Session(launcher);
        await using (session)
        {
            // The composer is gone while the question waits; the answer belongs in the question's own field.
            var result = await session.RunAsync(Operation.Reply, "orange", [], "https://chatgpt.com/c/question-thread", (_, _) => Task.CompletedTask, default);
            Assert.Equal("orange", await launcher.Context.Pages[0].EvaluateAsync<string>("window.answered"));
            Assert.NotNull(result.Image);
            Assert.EndsWith("/c/question-thread", result.ConversationUrl);
        }
        Directory.Delete(root, true);
    }

    // ---- The queue: an answer is not a failure, is not charged, and does not park the account ----

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
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task<Guid> MemberAsync(AppFactory factory, string username, int credits)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, username + "-strong-pass");
        db.Users.Add(user);
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user.Id;
    }
    private static Task<Guid> AccountAsync(AppFactory factory, string profile, DateTime? lastUsed = null) => WithDbAsync(factory, async db =>
    {
        var account = new ProviderAccount { Label = profile, ProfileDirectory = profile, DailyCap = 25, LastUsedAt = lastUsed };
        db.Accounts.Add(account); await db.SaveChangesAsync(); return account.Id;
    });
    private static async Task<Generation> SettledAsync(AppFactory factory, Guid id, int seconds = 150)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var g = await WithDbAsync(factory, db => db.Generations.AsNoTracking().FirstAsync(x => x.Id == id));
            if (g.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Answered or RunStatus.Cancelled) return g;
            await Task.Delay(300);
        }
        throw new TimeoutException($"Generation {id} never settled.");
    }
    private static async Task<JsonElement> QueueAsync(HttpClient client, string route, string prompt)
    {
        var response = await client.PostAsJsonAsync(route, new { prompt });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task An_answer_ends_as_answered_with_its_words_its_credits_back_and_the_account_still_in_service()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionPage };
        using var factory = new DispatchFactory(launcher);
        var sara = await MemberAsync(factory, "sara", 100);
        await AccountAsync(factory, "only");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { title = "a poster" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await SettledAsync(factory, (await QueueAsync(client, $"/api/projects/{project}/generate", "a poster")).GetProperty("id").GetGuid());
        Assert.Equal(RunStatus.Answered, first.Status);
        Assert.Contains("Which style do you want", first.ReplyText);
        Assert.Null(first.ErrorMessage);
        Assert.EndsWith("/c/fixture-conversation", first.ConversationUrl);
        // No picture was delivered, so nothing is kept for it. The refund is written just after the status, so it is waited for, not raced: read
        // in between, under a full parallel run, the balance was sometimes still 90.
        var balance = 0;
        for (var deadline = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < deadline && (balance = await WithDbAsync(factory, db => db.BalanceAsync(sara, default))) != 100;) await Task.Delay(100);
        Assert.Equal(100, balance);
        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/generations/{first.Id}");
        Assert.Equal("Answered", dto.GetProperty("status").GetString());
        Assert.Contains("flat or photographic?", dto.GetProperty("replyText").GetString());
        // A question is not an exhausted account: the only account takes the next piece of work instead of sitting parked for the day.
        var second = await SettledAsync(factory, (await QueueAsync(client, $"/api/projects/{project}/generate", "another poster")).GetProperty("id").GetGuid());
        Assert.Equal(RunStatus.Answered, second.Status);
        Assert.Equal(0, await WithDbAsync(factory, db => db.UsedByAsync(sara, default)));
    }

    [Fact]
    public async Task Replying_continues_the_same_conversation_on_the_account_that_holds_it()
    {
        await using var launcher = new FixtureLauncher { Body = QuestionPage };
        using var factory = new DispatchFactory(launcher);
        await MemberAsync(factory, "sara", 100);
        var older = await AccountAsync(factory, "older", DateTime.UtcNow.AddHours(-3));
        var newer = await AccountAsync(factory, "newer", DateTime.UtcNow.AddHours(-1));
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { title = "a poster" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var asked = await SettledAsync(factory, (await QueueAsync(client, $"/api/projects/{project}/generate", "a poster")).GetProperty("id").GetGuid());
        Assert.Equal(older, asked.AccountId);
        // "older" has just been used, so any unpinned work would now go to "newer". The answer belongs to the thread, and the thread lives on "older".
        launcher.Visits.Clear();
        var reply = await QueueAsync(client, $"/api/generations/{asked.Id}/reply", "flat, please");
        Assert.Equal("Reply", reply.GetProperty("operation").GetString());
        Assert.Equal(asked.Id, reply.GetProperty("parentGenerationId").GetGuid());
        var answered = await SettledAsync(factory, reply.GetProperty("id").GetGuid());
        Assert.Equal(older, answered.AccountId);
        Assert.Contains(launcher.Visits, v => v.EndsWith("/c/fixture-conversation"));
        Assert.NotEqual(newer, answered.AccountId);
    }

    [Fact]
    public async Task Only_a_finished_turn_with_a_conversation_can_be_replied_to_and_only_by_its_owner()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", 100);
        await MemberAsync(factory, "omid", 100);
        var account = await AccountAsync(factory, "only");
        var (withThread, withoutThread, running) = await WithDbAsync(factory, async db =>
        {
            var project = new ImageProject { UserId = sara, Title = "sara's" };
            var a = new Generation { ProjectId = project.Id, UserId = sara, Prompt = "asked", Status = RunStatus.Answered, AccountId = account, ConversationUrl = "https://chatgpt.com/c/thread", ReplyText = "Which style?" };
            var b = new Generation { ProjectId = project.Id, UserId = sara, Prompt = "no thread", Status = RunStatus.Answered, AccountId = account };
            var c = new Generation { ProjectId = project.Id, UserId = sara, Prompt = "busy", Status = RunStatus.WaitingForResponse, AccountId = account, ConversationUrl = "https://chatgpt.com/c/busy" };
            db.Projects.Add(project); db.Generations.AddRange(a, b, c); await db.SaveChangesAsync();
            return (a.Id, b.Id, c.Id);
        });
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        using var other = await SignInAsync(factory, "omid", "omid-strong-pass");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/generations/{withThread}/reply", new { prompt = "flat" })).StatusCode);
        foreach (var id in new[] { withoutThread, running })
        {
            var refused = await client.PostAsJsonAsync($"/api/generations/{id}/reply", new { prompt = "flat" });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("NoConversation", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/generations/{withThread}/reply", new { prompt = "mine now" })).StatusCode);
        // The reply that was accepted is pinned to the account holding the thread before any dispatcher sees it.
        Assert.Equal(account, await WithDbAsync(factory, db => db.Generations.Where(g => g.Operation == Operation.Reply).Select(g => g.AccountId).SingleAsync()));
    }
}
