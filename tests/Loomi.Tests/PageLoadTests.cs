using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>The live site draws its message box long before it has finished loading, and a file handed to it in between is dropped without a word:
/// on a slow connection the box came 24 seconds before the page's load, and every picture attached in that gap ended as UploadFailed.</summary>
public class PageLoadTests
{
    /// <summary>A box that shows at once and an attachment input that only listens once the page has loaded, the load held back by a slow resource.</summary>
    private const string ListensAfterLoad = """
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea><input type="file" multiple>
        <div id="attachments"></div>
        <button data-testid="send-button" onclick="send()">Send</button>
        <img src="https://chatgpt.com/slow/asset.png" alt="">
        <script>
        window.attached = 0;
        window.addEventListener('load', () => document.querySelector('input[type=file]').addEventListener('change', e => {
          window.attached += e.target.files.length;
          document.querySelector('#attachments').innerHTML = Array.from({length: window.attached}, () => '<button aria-label="Remove file">Remove</button>').join('');
        }));
        function send() {
          history.replaceState({}, '', '/c/fixture-conversation');
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          reply.textContent = 'files ' + window.attached; document.body.append(reply);
        }
        </script></body></html>
        """;

    [Fact]
    public async Task A_picture_is_attached_only_once_the_page_has_loaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-load-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var picture = Path.Combine(root, "in.png");
        await File.WriteAllBytesAsync(picture, [137, 80, 78, 71, 13, 10, 26, 10]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        await using var launcher = new FixtureLauncher { Body = ListensAfterLoad, SlowMs = 3000 };
        await using var session = new BrowserSession("default", Options.Create(new BrowserOptions { Headless = true, StableSeconds = 1, GenerationTimeoutSeconds = 30, UploadTimeoutMs = 2000 }), config, launcher);
        var result = await session.RunAsync(Operation.Generate, "draw", [picture], null, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("files 1", result.ReplyText);
    }
}
