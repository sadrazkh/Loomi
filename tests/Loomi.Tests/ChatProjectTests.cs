using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>A project is one conversation with the AI. A message continues it where the picture already is, so the picture is not uploaded again; only
/// when that conversation is out of reach does the project's latest picture open a new one.</summary>
public class ChatProjectTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private sealed record World(AppFactory Factory, Viewer Owner, Guid Project, Guid Account) : IDisposable { public void Dispose() => Factory.Dispose(); }
    private static async Task<World> WorldAsync()
    {
        var factory = new AppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.FirstAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
        var project = new ImageProject { UserId = owner.Id, Title = "a chat" };
        var account = new ProviderAccount { Label = "GPT1", ProfileDirectory = "gpt1", DailyCap = 25 };
        db.Projects.Add(project); db.Accounts.Add(account); await db.SaveChangesAsync();
        return new(factory, Viewer.For(owner), project.Id, account.Id);
    }
    private static async Task<Generation> SendAsync(World w, string prompt, List<InputRef>? inputs = null)
    {
        using var scope = w.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GenerationService>().SendAsync(w.Owner, w.Project, prompt, inputs, null, default);
    }
    private static async Task<Generation> SubmitAsync(World w, Operation operation, Guid parent, string prompt)
    {
        using var scope = w.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GenerationService>().SubmitAsync(w.Owner, w.Project, prompt, Provider.ChatGPT, operation, parent, null, default);
    }
    /// <summary>What a finished run leaves: a picture, the conversation it lives in, and the account holding it.</summary>
    private static async Task FinishAsync(World w, Guid id, string conversation = "https://chatgpt.com/c/one", bool picture = true)
    {
        using var scope = w.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = await db.Generations.SingleAsync(x => x.Id == id);
        (g.Status, g.ConversationUrl, g.AccountId) = (picture ? RunStatus.Completed : RunStatus.Answered, conversation, w.Account);
        if (picture) g.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(w.Project, g.Id, Png, default);
        await db.SaveChangesAsync();
    }
    private static async Task<Generation> RowAsync(World w, Guid id)
    {
        using var scope = w.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.AsNoTracking().Include(g => g.Inputs).SingleAsync(g => g.Id == id);
    }

    [Fact]
    public async Task The_next_message_continues_the_conversation_without_sending_the_picture_again()
    {
        using var w = await WorldAsync();
        var first = await SendAsync(w, "a red bicycle");
        Assert.Equal((Operation.Generate, (Guid?)null), (first.Operation, first.ParentGenerationId));
        // One message at a time, as on the site itself.
        Assert.Equal("ProjectBusy", (await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(w, "too soon"))).Message);

        await FinishAsync(w, first.Id);
        var next = await RowAsync(w, (await SendAsync(w, "make it blue")).Id);
        Assert.Equal((Operation.Reply, first.Id, w.Account), (next.Operation, next.ParentGenerationId!.Value, next.AccountId!.Value));
        Assert.Empty(next.Inputs);
    }

    [Fact]
    public async Task An_answer_in_words_is_continued_the_same_way()
    {
        using var w = await WorldAsync();
        var asked = await SendAsync(w, "a poster");
        await FinishAsync(w, asked.Id, picture: false);
        var answer = await SendAsync(w, "flat, please");
        Assert.Equal((Operation.Reply, asked.Id), (answer.Operation, answer.ParentGenerationId!.Value));
    }

    [Fact]
    public async Task When_the_conversation_is_out_of_reach_the_latest_picture_opens_a_new_one()
    {
        using var w = await WorldAsync();
        var first = await SendAsync(w, "a red bicycle");
        await FinishAsync(w, first.Id);
        using (var scope = w.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.SingleAsync()).IsEnabled = false; await db.SaveChangesAsync();
        }
        var next = await RowAsync(w, (await SendAsync(w, "make it blue")).Id);
        Assert.Equal((Operation.Branch, first.Id), (next.Operation, next.ParentGenerationId!.Value));
        Assert.Equal(first.Id, Assert.Single(next.Inputs).SourceGenerationId);
    }

    [Fact]
    public async Task Editing_the_latest_picture_is_the_next_message_and_an_older_one_is_sent_along()
    {
        using var w = await WorldAsync();
        var first = await SendAsync(w, "a red bicycle");
        await FinishAsync(w, first.Id);
        var edit = await RowAsync(w, (await SubmitAsync(w, Operation.Edit, first.Id, "make it blue")).Id);
        Assert.Equal(Operation.Reply, edit.Operation);
        Assert.Empty(edit.Inputs);

        await FinishAsync(w, edit.Id);
        // The first picture is no longer the conversation's latest: to be sure the AI works on that one, it goes along.
        var older = await RowAsync(w, (await SubmitAsync(w, Operation.Edit, first.Id, "the first one, in green")).Id);
        Assert.Equal(Operation.Edit, older.Operation);
        Assert.Equal(first.Id, older.Inputs.OrderBy(i => i.Order).First().SourceGenerationId);
    }

    [Fact]
    public async Task An_edit_waits_for_the_answer_before_it_like_any_other_message()
    {
        using var w = await WorldAsync();
        var first = await SendAsync(w, "a red bicycle");
        await FinishAsync(w, first.Id);
        await SendAsync(w, "make it blue");
        Assert.Equal("ProjectBusy", (await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(w, Operation.Edit, first.Id, "and green"))).Message);
    }

    [Fact]
    public async Task A_new_chat_is_a_new_project_named_by_its_first_message_and_a_refused_one_leaves_nothing()
    {
        using var w = await WorldAsync();
        using var scope = w.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GenerationService>();
        var started = await service.StartAsync(w.Owner, null, "a lighthouse at dusk", null, null, default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(("a lighthouse at dusk", Operation.Generate), (await db.Projects.Where(p => p.Id == started.ProjectId).Select(p => p.Title).SingleAsync(), started.Operation));
        var before = await db.Projects.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(w.Owner, "empty", "", null, null, default));
        Assert.Equal(before, await db.Projects.CountAsync());
    }

    [Theory]
    [InlineData("  a   red\nbicycle  ", "a red bicycle")]
    [InlineData("", "…")]
    public void A_project_started_by_a_message_is_named_by_its_opening_words(string prompt, string title) => Assert.Equal(title, GenerationService.TitleFrom(prompt));

    [Fact]
    public void A_long_message_is_cut_to_a_readable_title() => Assert.Equal(61, GenerationService.TitleFrom(new string('x', 200)).Length);
}
