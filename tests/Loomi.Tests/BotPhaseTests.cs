using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Xunit;

namespace Loomi.Tests;
/// <summary>The bot does in a chat what the web does: templates chosen by their sample, an edit or another version of any image by a button under it,
/// the original file on request, and one line that says where the work stands until the result takes its place.</summary>
public class BotPhaseTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const long Chat = 5150;
    private sealed record Setup(AppFactory Factory, FakeTelegram Bot, TelegramBotWorker Worker, Guid User) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }
    private static async Task<Setup> SetupAsync(int credits = 1000)
    {
        var factory = new AppFactory(); var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }), Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory),
            factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
        var user = await WithDbAsync(factory, async db =>
        {
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara", Role = UserRole.Member };
            db.Users.Add(sara);
            db.CreditEntries.Add(new CreditEntry { UserId = sara.Id, Amount = credits, Kind = CreditKind.Grant });
            db.TelegramLinks.Add(new TelegramLink { UserId = sara.Id, ChatId = Chat });
            await db.SaveChangesAsync();
            return sara.Id;
        });
        bot.Files["photo-1"] = Png;
        return new(factory, bot, worker, user);
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static Message Text(string text, string? language = null) => new() { Chat = new() { Id = Chat }, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Text = text };
    private static Message Photo(string file, string? caption = null) => new() { Chat = new() { Id = Chat }, From = new() { Id = 1, FirstName = "u" }, Caption = caption, Photo = [new() { FileId = file, Width = 1, Height = 1 }] };
    private static CallbackQuery Press(string data) => new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = 1, FirstName = "u" }, Message = new() { Chat = new() { Id = Chat } } };
    private static async Task<Guid> TemplateAsync(AppFactory factory, int min = 1, int max = 1)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var category = new TemplateCategory { Name = "Couples" };
        var template = new PromptTemplate { CategoryId = category.Id, Title = "Golden hour", Instructions = "One clear photo of both of you.", Prompt = "HIDDEN golden hour portrait", MinInputs = min, MaxInputs = max };
        template.SamplePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveTemplateAsync(template.Id, Png, default);
        db.TemplateCategories.Add(category); db.Templates.Add(template); await db.SaveChangesAsync();
        return template.Id;
    }
    private static async Task<Generation> LatestAsync(AppFactory factory) =>
        await WithDbAsync(factory, db => db.Generations.AsNoTracking().Include(g => g.Project).Include(g => g.Inputs).OrderByDescending(g => g.CreatedAt).FirstAsync());
    /// <summary>What the worker does when a run finishes: the image saved and the row completed, then the event.</summary>
    private static async Task CompleteAsync(Setup s, Guid id)
    {
        var done = await WithDbAsync(s.Factory, async db =>
        {
            var g = await db.Generations.SingleAsync(x => x.Id == id);
            g.LocalImagePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
            g.Status = RunStatus.Completed; await db.SaveChangesAsync(); return g;
        });
        await s.Worker.DeliverAsync(GenerationEvent.From(done), default);
    }

    [Fact]
    public async Task A_template_is_found_by_its_sample_chosen_and_used_with_a_photo_from_the_chat()
    {
        using var s = await SetupAsync();
        var template = await TemplateAsync(s.Factory);
        await s.Worker.HandleAsync(Text("/templates"), default);
        Assert.Contains($"tpl:{template:N}", s.Bot.Offered);

        await s.Worker.HandleCallbackAsync(Press($"tpl:{template:N}"), default);
        var sample = s.Bot.Photos[^1];
        Assert.Equal(Png, sample.Photo);
        Assert.Contains("Golden hour", sample.Caption);
        Assert.Contains("One clear photo", sample.Caption);
        Assert.Contains("10", sample.Caption);
        Assert.DoesNotContain("HIDDEN", sample.Caption);
        Assert.Contains($"use:{template:N}", s.Bot.Offered);

        await s.Worker.HandleCallbackAsync(Press($"use:{template:N}"), default);
        Assert.Contains("Golden hour", s.Bot.LastText);
        // Words alone do not finish a template that asks for a photo; the choice waits.
        await s.Worker.HandleAsync(Text("please hurry"), default);
        Assert.Equal(BotMessages.Text("templateNeedsPhotos", false, BotMessages.Text("photosOne", false)), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.Generations.CountAsync()));

        await s.Worker.HandleAsync(Photo("photo-1", "blue background"), default);
        var made = await LatestAsync(s.Factory);
        Assert.Equal((template, "blue background", "Golden hour", 1), (made.TemplateId!.Value, made.Prompt, made.Project.Title, made.Inputs.Count));
        Assert.Equal(Chat, await WithDbAsync(s.Factory, db => db.BotRequests.Where(r => r.GenerationId == made.Id).Select(r => r.ChatId).SingleAsync()));
        Assert.Equal(BotMessages.Text("statusQueued", false), s.Bot.LastText);
        // The choice is spent: once the picture is back, the next message is an ordinary one, carrying on in the template's chat.
        await CompleteAsync(s, made.Id);
        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        var next = await LatestAsync(s.Factory);
        Assert.Equal(((Guid?)null, made.ProjectId), (next.TemplateId, next.ProjectId));
    }

    [Fact]
    public async Task The_status_line_follows_the_work_and_gives_way_to_the_image_with_its_buttons()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a lighthouse at dusk"), default);
        var made = await LatestAsync(s.Factory);
        var line = await WithDbAsync(s.Factory, db => db.BotRequests.Where(r => r.GenerationId == made.Id).Select(r => r.StatusMessageId!.Value).SingleAsync());
        Assert.Equal(BotMessages.Text("statusQueued", false), s.Bot.Sent[line]);

        // Its place in the queue, as the web shows it: a count and a time.
        var queue = s.Factory.Services.GetRequiredService<QueueEstimates>();
        List<QueueEstimates.Waiting> order = [new(made.Id, s.User, Provider.ChatGPT)];
        queue.Publish(order, new() { [made.Id] = new(made.Id, 2, 120) });
        await s.Worker.RefreshWaitingAsync(default);
        Assert.Equal((line, BotMessages.Text("statusAhead", false, "2", "2")), (s.Bot.Edits[^1].Message, s.Bot.Edits[^1].Text));
        await s.Worker.RefreshWaitingAsync(default);
        Assert.Single(s.Bot.Edits);

        await s.Worker.DeliverAsync(GenerationEvent.From(made) with { Status = RunStatus.GeneratingImage }, default);
        Assert.Equal(BotMessages.Text("statusWorking", false), s.Bot.Edits[^1].Text);

        await CompleteAsync(s, made.Id);
        Assert.Equal((Chat, line), s.Bot.Deleted.Single());
        var buttons = s.Bot.PhotoButtons[^1]!.SelectMany(r => r).Select(b => b.Data).ToList();
        Assert.Equal([$"edit:{made.Id:N}", $"again:{made.Id:N}", $"file:{made.Id:N}", "new"], buttons);
    }

    [Fact]
    public async Task Edit_continues_that_image_and_another_version_repeats_the_request()
    {
        using var s = await SetupAsync();
        var template = await TemplateAsync(s.Factory);
        await s.Worker.HandleCallbackAsync(Press($"use:{template:N}"), default);
        await s.Worker.HandleAsync(Photo("photo-1", "warm light"), default);
        var first = await LatestAsync(s.Factory);
        await CompleteAsync(s, first.Id);

        await s.Worker.HandleCallbackAsync(Press($"edit:{first.Id:N}"), default);
        Assert.Equal(BotMessages.Text("editPrompt", false), s.Bot.LastText);
        await s.Worker.HandleAsync(Text("make it night"), default);
        var edit = await LatestAsync(s.Factory);
        Assert.Equal((Operation.Edit, first.Id, first.ProjectId, "make it night"), (edit.Operation, edit.ParentGenerationId!.Value, edit.ProjectId, edit.Prompt));

        await s.Worker.HandleCallbackAsync(Press($"again:{first.Id:N}"), default);
        var again = await LatestAsync(s.Factory);
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal((Operation.Generate, template, "HIDDEN golden hour portrait", "warm light", first.ProjectId), (again.Operation, again.TemplateId!.Value, again.HiddenPrompt!, again.Prompt, again.ProjectId));
        Assert.Equal(first.Inputs.Select(i => i.UploadId), again.Inputs.OrderBy(i => i.Order).Select(i => i.UploadId));
        // Each is a new piece of work and charged as one.
        Assert.Equal(970, await WithDbAsync(s.Factory, db => db.BalanceAsync(s.User, default)));
    }

    [Fact]
    public async Task The_original_arrives_as_a_file_and_only_to_the_person_it_belongs_to()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a lighthouse at dusk"), default);
        var mine = await LatestAsync(s.Factory);
        await CompleteAsync(s, mine.Id);
        await s.Worker.HandleCallbackAsync(Press($"file:{mine.Id:N}"), default);
        var file = Assert.Single(s.Bot.Documents);
        Assert.Equal(Chat, file.Chat);
        Assert.Equal(Png, file.File);

        var theirs = await WithDbAsync(s.Factory, async db =>
        {
            var omid = new AppUser { Username = "omid", NormalizedUsername = "omid", Role = UserRole.Member };
            var project = new ImageProject { UserId = omid.Id, Title = "omid's" };
            var g = new Generation { ProjectId = project.Id, UserId = omid.Id, Prompt = "theirs", Status = RunStatus.Completed };
            g.LocalImagePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveAsync(project.Id, g.Id, Png, default);
            db.Users.Add(omid); db.Projects.Add(project); db.Generations.Add(g); await db.SaveChangesAsync(); return g.Id;
        });
        foreach (var verb in new[] { "file", "edit", "again" }) await s.Worker.HandleCallbackAsync(Press($"{verb}:{theirs:N}"), default);
        Assert.Single(s.Bot.Documents);
        Assert.Equal(BotMessages.Text("NotFound", false), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.Generations.CountAsync(g => g.ParentGenerationId == theirs || g.Prompt == "theirs" && g.Id != theirs)));
    }

    [Fact]
    public async Task Someone_writing_Persian_is_answered_in_Persian_whatever_their_Telegram_is_set_to()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("یک فانوس دریایی در غروب", language: "en"), default);
        Assert.Equal(BotMessages.Text("statusQueued", true), s.Bot.LastText);
        await s.Worker.HandleAsync(Text("/balance", language: "en"), default);
        Assert.Contains("اعتبار", s.Bot.LastText);
    }

    [Fact]
    public async Task Cancel_drops_a_half_made_choice_before_it_touches_any_running_work()
    {
        using var s = await SetupAsync();
        var template = await TemplateAsync(s.Factory);
        await s.Worker.HandleAsync(Text("a lighthouse at dusk"), default);
        await s.Worker.HandleCallbackAsync(Press($"use:{template:N}"), default);
        await s.Worker.HandleAsync(Text("/cancel"), default);
        Assert.Equal(BotMessages.Text("stateCleared", false), s.Bot.LastText);
        Assert.Equal(RunStatus.Queued, (await LatestAsync(s.Factory)).Status);
        await s.Worker.HandleAsync(Text("/cancel"), default);
        Assert.Equal(RunStatus.Cancelled, (await LatestAsync(s.Factory)).Status);
    }

    [Fact]
    public async Task A_template_that_needs_no_photo_is_made_with_one_tap()
    {
        using var s = await SetupAsync();
        var template = await TemplateAsync(s.Factory, min: 0, max: 1);
        await s.Worker.HandleCallbackAsync(Press($"use:{template:N}"), default);
        Assert.Contains($"go:{template:N}", s.Bot.Offered);
        await s.Worker.HandleCallbackAsync(Press($"go:{template:N}"), default);
        var made = await LatestAsync(s.Factory);
        Assert.Equal((template, "", 0), (made.TemplateId!.Value, made.Prompt, made.Inputs.Count));
    }

    [Fact]
    public async Task The_command_menu_is_set_in_both_languages()
    {
        using var s = await SetupAsync();
        await s.Worker.RegisterCommandsAsync(default);
        Assert.Equal(["new", "templates", "balance", "status", "cancel", "help"], s.Bot.Commands!.Select(c => c.Command));
        Assert.All(s.Bot.Commands!, c => Assert.True(BotMessages.LooksPersian(c.Fa) && !BotMessages.LooksPersian(c.En)));
    }

    [Fact]
    public async Task A_press_is_always_answered_so_the_spinner_stops()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("templates"), default);
        await s.Worker.HandleCallbackAsync(Press("nonsense:xyz"), default);
        Assert.Equal(2, s.Bot.Answered.Count);
        Assert.Equal(BotMessages.Text("noTemplates", false), s.Bot.LastText);
    }
}
