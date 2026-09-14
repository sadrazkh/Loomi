namespace Loomi.Security;
/// <summary>Built-in workspace key so a Development run works out of the box. Startup refuses it in every other environment.</summary>
public static class DevelopmentAccess
{
    public const string Key = "loomi-development-access-key-change-me";
    /// <summary>The key to show on the login form, or null when the configured key is a real secret that must not leak.</summary>
    public static string? Hint(IWebHostEnvironment environment, string? configured) => environment.IsDevelopment() && configured == Key ? Key : null;
}
