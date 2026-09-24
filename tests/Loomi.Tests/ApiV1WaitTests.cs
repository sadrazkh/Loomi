using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Loomi.Tests;
/// <summary>Waiting on a message that takes minutes: a long poll that answers the moment the message moves or when its time is up, and a stream of
/// events that ends with the message's final state. The re-check is five seconds, so an answer well inside that came from the announcement.</summary>
public class ApiV1WaitTests
{
    private static async Task<(Guid Chat, Guid Message)> StartedAsync(HttpClient api)
    {
        var body = await ApiV1World.JsonAsync(await api.PostAsJsonAsync("/api/v1/chats", new { prompt = "a lighthouse at dusk" }));
        return (ApiV1World.Id(body.GetProperty("chat")), ApiV1World.Id(body.GetProperty("message")));
    }

    [Fact]
    public async Task Wait_answers_not_done_when_its_time_runs_out()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (_, message) = await StartedAsync(api);
        // Zero is clamped up to one second rather than turned into a busy loop.
        foreach (var timeout in new[] { 1, 0 })
        {
            var clock = Stopwatch.StartNew();
            var result = await api.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message}/wait?timeout={timeout}");
            Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(4));
            Assert.False(result.GetProperty("done").GetBoolean());
            Assert.Equal(("queued", message), (result.GetProperty("message").GetProperty("status").GetString(), ApiV1World.Id(result.GetProperty("message"))));
        }
    }

    [Fact]
    public async Task Wait_answers_the_moment_the_message_moves()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (_, message) = await StartedAsync(api);
        var clock = Stopwatch.StartNew();
        var waiting = api.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message}/wait?timeout=30");
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);
        await w.FinishAsync(message, announce: true);
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        Assert.True(result.GetProperty("done").GetBoolean());
        Assert.Equal(("completed", $"/api/v1/messages/{message}/image"), (result.GetProperty("message").GetProperty("status").GetString(), result.GetProperty("message").GetProperty("imageUrl").GetString()));

        // A message already finished is answered at once, however long the caller was prepared to wait.
        clock.Restart();
        Assert.True((await api.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message}/wait?timeout=120")).GetProperty("done").GetBoolean());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Wait_notices_an_answer_in_words_that_nobody_announced()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (_, message) = await StartedAsync(api);
        var waiting = api.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message}/wait?timeout=30");
        await Task.Delay(300);
        await w.FinishAsync(message, picture: false);
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.GetProperty("done").GetBoolean());
        var answered = result.GetProperty("message");
        Assert.Equal(("answered", "Which style would you like?", JsonValueKind.Null), (answered.GetProperty("status").GetString(), answered.GetProperty("text").GetString(), answered.GetProperty("imageUrl").ValueKind));
    }

    [Fact]
    public async Task Events_send_the_status_first_and_end_after_the_final_one()
    {
        using var w = await ApiV1World.CreateAsync();
        var (_, api) = await w.MemberAsync("sara");
        var (_, message) = await StartedAsync(api);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await api.GetAsync($"/api/v1/messages/{message}/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal((HttpStatusCode.OK, "text/event-stream"), (response.StatusCode, response.Content.Headers.ContentType?.MediaType));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        async Task<List<(string Event, JsonElement Data)>> ReadAsync(int? count)
        {
            var events = new List<(string, JsonElement)>();
            string? name = null;
            while ((count is null || events.Count < count) && await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("event: ")) name = line[7..];
                else if (line.StartsWith("data: ")) events.Add((name!, JsonDocument.Parse(line[6..]).RootElement.Clone()));
            }
            return events;
        }
        var (first, data) = Assert.Single(await ReadAsync(1));
        Assert.Equal(("status", "queued", message), (first, data.GetProperty("status").GetString(), ApiV1World.Id(data)));

        await w.FinishAsync(message, announce: true);
        // Reading to the end only returns because the server closed the stream after the final state.
        var rest = await ReadAsync(null);
        Assert.Equal(("status", "completed"), (rest[^1].Event, rest[^1].Data.GetProperty("status").GetString()));
        Assert.All(rest, e => Assert.Equal("status", e.Event));
    }
}
