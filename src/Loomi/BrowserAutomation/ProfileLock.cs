namespace Loomi.BrowserAutomation;
/// <summary>Chromium marks a profile in use with a SingletonLock symlink naming "host-pid". A container that was killed rather than stopped leaves its
/// lock behind, naming a host no later container will ever be, and Chromium then refuses that profile for good: every account reads as disconnected
/// after each redeploy. Removing it is safe only while holding the <see cref="Services.InstanceLease"/>, when no other Loomi can be using the profile.</summary>
public static class ProfileLock
{
    private static readonly string[] Files = ["SingletonLock", "SingletonSocket", "SingletonCookie"];
    /// <summary>True when the lock names another host. A lock of this host is Chromium's own to judge: it checks whether that process is still alive.</summary>
    public static bool IsForeign(string? target, string host)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var dash = target.LastIndexOf('-');
        var owner = dash > 0 ? target[..dash] : target;
        return !owner.Equals(host, StringComparison.OrdinalIgnoreCase) && !owner.StartsWith(host + ".", StringComparison.OrdinalIgnoreCase);
    }
    public static bool ClearForeign(string profile, string host)
    {
        if (!IsForeign(new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget, host)) return false;
        foreach (var name in Files) File.Delete(Path.Combine(profile, name));
        return true;
    }
}
