using Loomi.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Loomi.Tests;
/// <summary>After an update, a browser that kept the old index.html keeps running the old app against the new server. The page itself is revalidated
/// every time; the bundles it names carry a hash in their file name, so a new build is a new name and they can be kept for good.</summary>
public class StaticCachingTests
{
    private static string? CacheControlFor(string path)
    {
        IHeaderDictionary headers = new HeaderDictionary();
        StaticCaching.Apply(path, headers);
        return headers.CacheControl.ToString() is { Length: > 0 } value ? value : null;
    }

    [Theory]
    [InlineData("/dist/index.html")]
    [InlineData("/dist/")]
    [InlineData("/index.html")]
    public void The_page_is_asked_about_every_time(string path) => Assert.Equal("no-cache", CacheControlFor(path));

    [Theory]
    [InlineData("/dist/assets/index-DSd35hsZ.js")]
    [InlineData("/dist/assets/index-Db_o-k5H.css")]
    [InlineData("/dist/assets/vazirmatn-arabic-400-normal-BxmY-B46.woff2")]
    public void A_hashed_bundle_is_kept_for_good(string path) => Assert.Equal("public, max-age=31536000, immutable", CacheControlFor(path));

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/dist/logo.svg")]
    public void Anything_else_is_left_to_the_default(string path) => Assert.Null(CacheControlFor(path));
}
