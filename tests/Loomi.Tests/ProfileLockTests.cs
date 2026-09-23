using Loomi.BrowserAutomation;
using Xunit;

namespace Loomi.Tests;
/// <summary>A container killed rather than stopped leaves Chromium's SingletonLock naming its own hostname. The next container has another one, and
/// Chromium refuses the profile for good, so every ChatGPT account read as disconnected after each redeploy.</summary>
public class ProfileLockTests
{
    [Theory]
    [InlineData("3f2a9c1b7d10-412", "9e81c0aa2b44", true)]    // left by the container this one replaced
    [InlineData("3f2a9c1b7d10-412", "3f2a9c1b7d10", false)]   // this host's own: Chromium checks whether that process is alive
    [InlineData("3F2A9C1B7D10-412", "3f2a9c1b7d10", false)]
    [InlineData("loomi.internal-77", "loomi", false)]          // Chromium writes the full name, .NET reports the part before the first dot
    [InlineData("my-host-name-9", "my-host-name", false)]      // a hyphen in the hostname is not the pid separator
    [InlineData("my-host-name-9", "my-host", true)]
    [InlineData(null, "anything", false)]
    [InlineData("", "anything", false)]
    public void Only_a_lock_naming_another_host_is_foreign(string? target, string host, bool foreign) => Assert.Equal(foreign, ProfileLock.IsForeign(target, host));

    [Fact]
    public void A_foreign_lock_is_cleared_with_its_socket_and_cookie_and_the_profile_is_left_alone()
    {
        var profile = Directory.CreateTempSubdirectory("loomi-profile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(profile, "Cookies"), "keep me");
            if (!TryLink(Path.Combine(profile, "SingletonLock"), "3f2a9c1b7d10-412")) return;   // Windows without symlink rights; Chromium does not use this lock there either
            TryLink(Path.Combine(profile, "SingletonSocket"), "/tmp/.org.chromium.Chromium.a1b2c3/SingletonSocket");
            TryLink(Path.Combine(profile, "SingletonCookie"), "1234567890");
            Assert.False(ProfileLock.ClearForeign(profile, "3f2a9c1b7d10"));
            Assert.NotNull(new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget);
            Assert.True(ProfileLock.ClearForeign(profile, "9e81c0aa2b44"));
            foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" }) Assert.Null(new FileInfo(Path.Combine(profile, name)).LinkTarget);
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(profile, "Cookies")));
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public void A_profile_that_was_never_opened_is_not_touched() => Assert.False(ProfileLock.ClearForeign(Directory.CreateTempSubdirectory("loomi-profile-").FullName, "anyhost"));

    private static bool TryLink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); return true; }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
