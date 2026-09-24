using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Xunit;

namespace Loomi.Tests;
/// <summary>A Telegram chat works like a conversation on ChatGPT: each chat is one project, whatever the person sends next continues it without the
/// picture going again, and /new or the button under an image starts another.</summary>
public class BotChatTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const long Chat = 6160;
    private sealed record Setup(AppFactory Factory, FakeTelegram Bot, TelegramBotWorker Worker, Guid User, Guid Account) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }
    private static async Task<Setup> SetupAsync()
    {
        var factory = new AppFactory(); var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }), Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory),
            factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
        var (user, account) = await WithDbAsync(factory, async db =>
        {
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara", Role = UserRole.Member };
            var gpt = new ProviderAccount { Label = "GPT1", ProfileDirectory = "gpt1", DailyCap = 25 };
            db.Users.Add(sara); db.Accounts.Add(gpt);
            db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = 1000, Kind = CreditKind.Grant });
            db.TelegramLinks.Add(new TelegramLink { UserId = sara.Id, ChatId = Chat });
            await db.SaveChangesAsync();
            return (sara.Id, gpt.Id);
        });
        bot.Files["photo-1"] = Png;
        return new(factory, bot, worker, user, account);
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static Message Text(string text) => new() { Chat = new() { Id = Chat }, From = new() { Id = 1, FirstName = "u" }, Text = text };
    private static Message Photo(string file, string caption) => new() { Chat = new() { Id = Chat }, From = new() { Id = 1, FirstName = "u" }, Caption = caption, Photo = [new() { FileId = file, Width = 1, Height = 1 }] };
    private static CallbackQuery Press(string data) => new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = 1, FirstName = "u" }, Message = new() { Chat = new() { Id = Chat } } };
    private static Task<Guid?> TalkingInAsync(Setup s) => WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).Select(l => l.ProjectId).SingleAsync());
    private static Task<Generation> LatestAsync(Setup s) =>
        WithDbAsync(s.Factory, db => db.Generations.AsNoTracking().Include(g => g.Project).Include(g => g.Inputs).OrderByDescending(g => g.CreatedAt).FirstAsync());
    private static Task<int> CountAsync<T>(Setup s) where T : class => WithDbAsync(s.Factory, db => db.Set<T>().CountAsync());
    /// <summary>What a finished run leaves, a picture and the conversation it lives in on the account that holds it, and then the bot delivering it.</summary>
    private static async Task FinishAsync(Setup s, Guid id, bool picture = true)
    {
        var done = await WithDbAsync(s.Factory, async db =>
        {
            var g = await db.Generations.SingleAsync(x => x.Id == id);
            (g.Status, g.ConversationUrl, g.AccountId) = (picture ? RunStatus.Completed : RunStatus.Answered, $"https://chatgpt.com/c/{id:N}", s.Account);
            if (picture) g.LocalImagePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
            else g.ReplyText = "Which style would you like?";
            await db.SaveChangesAsync(); return g;
        });
        await s.Worker.DeliverAsync(GenerationEvent.From(done), default);
    }
    /// <summary>A message sent in the chat, made and delivered back to it.</summary>
    private static async Task<Generation> MadeAsync(Setup s, string text)
    {
        await s.Worker.HandleAsync(Text(text), default);
        var made = await LatestAsync(s);
        await FinishAsync(s, made.Id);
        return made;
    }

    [Fact]
    public async Task The_first_message_starts_a_project_named_by_it_and_the_chat_talks_in_it()
    {
        using var s = await SetupAsync();
        Assert.Null(await TalkingInAsync(s));
        await s.Worker.HandleAsync(Text("a red bicycle by the sea"), default);
        var first = await LatestAsync(s);
        Assert.Equal(("a red bicycle by the sea", Operation.Generate, s.User), (first.Project.Title, first.Operation, first.Project.UserId));
        Assert.Equal(first.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task A_follow_up_continues_the_same_conversation_without_sending_the_picture_again()
    {
        using var s = await SetupAsync();
        var first = await MadeAsync(s, "a red bicycle");
        Assert.Single(s.Bot.Photos);
        await s.Worker.HandleAsync(Text("make it blue"), default);
        var next = await LatestAsync(s);
        Assert.Equal((Operation.Reply, first.Id, first.ProjectId, s.Account), (next.Operation, next.ParentGenerationId!.Value, next.ProjectId, next.AccountId!.Value));
        Assert.Empty(next.Inputs);
        Assert.Equal(1, await CountAsync<ImageProject>(s));
        Assert.Equal(first.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task A_photo_sent_as_a_follow_up_goes_along_as_a_new_reference_only()
    {
        using var s = await SetupAsync();
        var first = await MadeAsync(s, "a red bicycle");
        await s.Worker.HandleAsync(Photo("photo-1", "paint it like this"), default);
        var next = await LatestAsync(s);
        Assert.Equal((Operation.Reply, first.ProjectId), (next.Operation, next.ProjectId));
        Assert.NotNull(Assert.Single(next.Inputs).UploadId);
    }

    [Fact]
    public async Task An_answer_in_words_is_continued_by_the_next_message()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a poster"), default);
        var asked = await LatestAsync(s);
        await FinishAsync(s, asked.Id, picture: false);
        Assert.Equal("Which style would you like?", s.Bot.LastText);
        await s.Worker.HandleAsync(Text("flat, please"), default);
        var answer = await LatestAsync(s);
        Assert.Equal((Operation.Reply, asked.Id, asked.ProjectId), (answer.Operation, answer.ParentGenerationId!.Value, answer.ProjectId));
    }

    [Fact]
    public async Task A_follow_up_while_the_last_is_still_being_made_is_refused_and_creates_nothing()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a red bicycle"), default);
        await s.Worker.HandleAsync(Text("make it blue"), default);
        Assert.Equal(BotMessages.Text("ProjectBusy", false), s.Bot.LastText);
        Assert.Contains("/new", s.Bot.LastText);
        // Told in the language the person writes in; and a photo sent meanwhile is not even fetched, so no upload is left behind.
        await s.Worker.HandleAsync(Photo("photo-1", "آبی‌اش کن"), default);
        Assert.Equal(BotMessages.Text("ProjectBusy", true), s.Bot.LastText);
        Assert.Equal((1, 1, 0), (await CountAsync<Generation>(s), await CountAsync<ImageProject>(s), await CountAsync<Upload>(s)));
        Assert.Equal(990, await WithDbAsync(s.Factory, db => db.BalanceAsync(s.User, default)));
    }

    [Fact]
    public async Task New_makes_the_next_message_start_a_second_project()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a red bicycle"), default);
        var first = await LatestAsync(s);
        await s.Worker.HandleAsync(Text("/new"), default);
        Assert.Equal(BotMessages.Text("newChat", false), s.Bot.LastText);
        Assert.Null(await TalkingInAsync(s));
        // The first is still being made, and that does not hold up a new chat.
        await s.Worker.HandleAsync(Text("a green kite"), default);
        var second = await LatestAsync(s);
        Assert.NotEqual(first.ProjectId, second.ProjectId);
        Assert.Equal(("a green kite", Operation.Generate), (second.Project.Title, second.Operation));
        Assert.Equal(second.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task The_new_chat_button_under_an_image_does_the_same_and_drops_a_half_made_edit()
    {
        using var s = await SetupAsync();
        var first = await MadeAsync(s, "a red bicycle");
        Assert.Contains("new", s.Bot.PhotoButtons[^1]!.SelectMany(r => r).Select(b => b.Data));
        await s.Worker.HandleCallbackAsync(Press($"edit:{first.Id:N}"), default);
        await s.Worker.HandleCallbackAsync(Press("new"), default);
        Assert.Equal(BotMessages.Text("newChat", false), s.Bot.LastText);
        await s.Worker.HandleAsync(Text("a green kite"), default);
        var second = await LatestAsync(s);
        Assert.NotEqual(first.ProjectId, second.ProjectId);
        Assert.Equal((Operation.Generate, (Guid?)null), (second.Operation, second.ParentGenerationId));
    }

    [Fact]
    public async Task Edit_on_an_older_chats_picture_switches_the_chat_back_to_it()
    {
        using var s = await SetupAsync();
        var older = await MadeAsync(s, "a red bicycle");
        await s.Worker.HandleAsync(Text("/new"), default);
        var newer = await MadeAsync(s, "a green kite");
        Assert.Equal(newer.ProjectId, await TalkingInAsync(s));
        await s.Worker.HandleCallbackAsync(Press($"edit:{older.Id:N}"), default);
        Assert.Equal(older.ProjectId, await TalkingInAsync(s));
        await s.Worker.HandleAsync(Text("make it night"), default);
        var edit = await LatestAsync(s);
        // It is that chat's latest picture, so the edit is simply the next message there and nothing is uploaded again.
        Assert.Equal((Operation.Reply, older.Id, older.ProjectId), (edit.Operation, edit.ParentGenerationId!.Value, edit.ProjectId));
        Assert.Empty(edit.Inputs);
        Assert.Equal(older.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task Another_version_carries_on_in_that_pictures_chat()
    {
        using var s = await SetupAsync();
        var older = await MadeAsync(s, "a red bicycle");
        await s.Worker.HandleAsync(Text("/new"), default);
        await MadeAsync(s, "a green kite");
        await s.Worker.HandleCallbackAsync(Press($"again:{older.Id:N}"), default);
        var again = await LatestAsync(s);
        Assert.Equal((older.ProjectId, "a red bicycle"), (again.ProjectId, again.Prompt));
        Assert.Equal(older.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task Using_a_template_makes_its_project_the_chats_current_one()
    {
        using var s = await SetupAsync();
        var template = await WithDbAsync(s.Factory, async db =>
        {
            var category = new TemplateCategory { Name = "Couples" };
            var t = new PromptTemplate { CategoryId = category.Id, Title = "Golden hour", Prompt = "HIDDEN golden hour portrait", MinInputs = 1, MaxInputs = 1 };
            t.SamplePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveTemplateAsync(t.Id, Png, default);
            db.TemplateCategories.Add(category); db.Templates.Add(t); await db.SaveChangesAsync();
            return t.Id;
        });
        await s.Worker.HandleAsync(Text("a red bicycle"), default);
        var first = await LatestAsync(s);
        await s.Worker.HandleCallbackAsync(Press($"use:{template:N}"), default);
        await s.Worker.HandleAsync(Photo("photo-1", "warm light"), default);
        var used = await LatestAsync(s);
        Assert.Equal((template, "Golden hour"), (used.TemplateId!.Value, used.Project.Title));
        Assert.NotEqual(first.ProjectId, used.ProjectId);
        Assert.Equal(used.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task A_chat_never_talks_in_a_project_that_is_gone_or_is_not_its_users()
    {
        using var s = await SetupAsync();
        var theirs = await WithDbAsync(s.Factory, async db =>
        {
            var omid = new AppUser { Username = "omid", NormalizedUsername = "omid", Role = UserRole.Member };
            var project = new ImageProject { UserId = omid.Id, Title = "omid's" };
            db.Users.Add(omid); db.Projects.Add(project);
            (await db.TelegramLinks.SingleAsync(l => l.ChatId == Chat)).ProjectId = project.Id;
            await db.SaveChangesAsync(); return project.Id;
        });
        await s.Worker.HandleAsync(Text("a red bicycle"), default);
        var mine = await LatestAsync(s);
        Assert.Equal((s.User, "a red bicycle"), (mine.Project.UserId, mine.Project.Title));
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.Generations.CountAsync(g => g.ProjectId == theirs)));

        await FinishAsync(s, mine.Id);
        await WithDbAsync(s.Factory, db => db.Projects.Where(p => p.Id == mine.ProjectId).ExecuteDeleteAsync());
        await s.Worker.HandleAsync(Text("a green kite"), default);
        var fresh = await LatestAsync(s);
        Assert.Equal(("a green kite", Operation.Generate), (fresh.Project.Title, fresh.Operation));
        Assert.Equal(fresh.ProjectId, await TalkingInAsync(s));
    }

    [Fact]
    public async Task Work_in_the_old_shared_project_is_still_delivered_and_the_next_message_starts_afresh()
    {
        using var s = await SetupAsync();
        // Queued before requests were recorded: no BotRequest, only the bot's old project to say whose chat it came from.
        var legacy = await WithDbAsync(s.Factory, async db =>
        {
            var project = new ImageProject { UserId = s.User, Title = TelegramBotWorker.BotProject };
            var g = new Generation { ProjectId = project.Id, UserId = s.User, Prompt = "an old lighthouse", Status = RunStatus.Completed };
            g.LocalImagePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveAsync(project.Id, g.Id, Png, default);
            db.Projects.Add(project); db.Generations.Add(g); await db.SaveChangesAsync(); return g;
        });
        await s.Worker.DeliverAsync(GenerationEvent.From(legacy), default);
        Assert.Equal((Chat, "an old lighthouse"), (s.Bot.Photos.Single().Chat, s.Bot.Photos.Single().Caption));
        await s.Worker.HandleAsync(Text("a red bicycle"), default);
        Assert.NotEqual(legacy.ProjectId, (await LatestAsync(s)).ProjectId);
    }

    [Fact]
    public async Task The_command_menu_and_help_offer_a_new_chat_in_both_languages()
    {
        using var s = await SetupAsync();
        await s.Worker.RegisterCommandsAsync(default);
        var command = Assert.Single(s.Bot.Commands!, c => c.Command == "new");
        Assert.True(BotMessages.LooksPersian(command.Fa) && !BotMessages.LooksPersian(command.En));
        foreach (var persian in new[] { false, true }) Assert.Contains("/new", BotMessages.Text("help", persian));
    }
}
