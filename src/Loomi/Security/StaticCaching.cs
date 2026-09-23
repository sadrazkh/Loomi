namespace Loomi.Security;
/// <summary>How long a browser may keep the app's own files. Without a rule it guesses from Last-Modified and keeps index.html for a while, so after an
/// update it goes on running the old app against the new server. The page is revalidated every time; the bundles it names carry a content hash in
/// their file name, so a new build is a new name and they can be kept for good.</summary>
public static class StaticCaching
{
    public static void Apply(string path, IHeaderDictionary headers)
    {
        if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) || path.EndsWith('/')) headers.CacheControl = "no-cache";
        else if (path.StartsWith("/dist/assets/", StringComparison.OrdinalIgnoreCase)) headers.CacheControl = "public, max-age=31536000, immutable";
    }
}
