using Loomi.Services;
using Xunit;

namespace Loomi.Tests;
public class ErrorCodeTests
{
    [Fact]
    public void Closing_the_browser_mid_run_is_named_instead_of_being_buried_in_a_generic_failure()
        => Assert.Equal("BrowserClosed", GenerationWorker.ErrorCodeFor("TargetClosedException", "Target page, context or browser has been closed"));

    [Theory]
    [InlineData("LoginRequired")]
    [InlineData("VerificationRequired")]
    [InlineData("GenerationTimeout")]
    [InlineData("InvalidImage")]
    [InlineData("ConversationNotSaved")]
    [InlineData("NoImageReturned")]
    [InlineData("QuotaExceeded")]
    public void Known_operation_failures_keep_their_own_code(string code)
        => Assert.Equal(code, GenerationWorker.ErrorCodeFor(new InvalidOperationException(code)));

    [Fact]
    public void Anything_else_stays_generic_so_page_text_cannot_leak_into_the_ui()
        => Assert.Equal("AutomationFailed", GenerationWorker.ErrorCodeFor(new InvalidOperationException("https://chatgpt.com/c/secret-conversation")));
}
