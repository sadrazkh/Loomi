using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Repositories;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var root = Path.GetFullPath(builder.Configuration["Storage:Root"] ?? "Storage");
Directory.CreateDirectory(root);
var accessKey = builder.Configuration["Security:AccessKey"];
if (string.IsNullOrWhiteSpace(accessKey) && builder.Environment.IsDevelopment())
    builder.Configuration["Security:AccessKey"] = accessKey = DevelopmentAccess.Key;
if (string.IsNullOrWhiteSpace(accessKey) || accessKey.Length < 32)
    throw new InvalidOperationException("Set Security__AccessKey to a random secret of at least 32 characters before starting Loomi.");
if (accessKey == DevelopmentAccess.Key && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("The built-in development access key cannot be used outside the Development environment. Set Security__AccessKey to a random secret of at least 32 characters.");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys"))).SetApplicationName("Loomi");
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(root, "loomi.db")};Foreign Keys=True;Default Timeout=30"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "Loomi.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(12); o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var value in builder.Configuration.GetSection("Security:KnownProxies").Get<string[]>() ?? []) o.KnownProxies.Add(IPAddress.Parse(value));
});
builder.Services.Configure<BrowserOptions>(builder.Configuration.GetSection("Browser"));
builder.Services.AddSingleton<BrowserPool>();
builder.Services.AddSingleton<BrowserAutomationService>();
builder.Services.AddSingleton<IChromiumLauncher, ChromiumLauncher>();
builder.Services.AddSingleton<IImageStorage, ImageStorage>();
builder.Services.AddScoped<ProjectRepository>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.AddReverseProxy().LoadFromMemory(
    [new RouteConfig { RouteId = "desktop", ClusterId = "desktop", Match = new() { Path = "/desktop/{**rest}" }, Transforms = [new Dictionary<string,string> { ["PathRemovePrefix"] = "/desktop" }, new Dictionary<string,string> { ["RequestHeaderRemove"] = "Cookie" }] }],
    [new ClusterConfig { ClusterId = "desktop", Destinations = new Dictionary<string,DestinationConfig> { ["local"] = new() { Address = $"http://127.0.0.1:{builder.Configuration.GetValue("Browser:DesktopPort", 6080)}/" } } }]);
var app = builder.Build();
if (accessKey == DevelopmentAccess.Key)
    app.Logger.LogWarning("Development mode: unlocking Loomi with the built-in access key \"{Key}\". Set Security__AccessKey to your own secret for anything but local testing.", DevelopmentAccess.Key);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
app.UseForwardedHeaders();
app.UseWebSockets();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self' ws: wss:; frame-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/desktop")) ctx.Response.Headers.CacheControl = "no-store";
    if (ctx.WebSockets.IsWebSocketRequest && ctx.Request.Headers.Origin.ToString() != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (KeyNotFoundException) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "NotFound" }); }
    catch (InvalidOperationException ex) when (new[] { "BrowserBusy", "QueueFull", "InvalidParent", "InvalidPrompt", "ProjectBusy", "NoAccount" }.Contains(ex.Message))
    { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
    { app.Logger.LogError("Request failed ({Type})", ex.GetType().Name); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "ServerError" }); }
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
    {
        try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "InvalidCsrf" }); return; }
    }
    await next();
});
app.MapControllers();
app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();
app.MapReverseProxy().RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/", () => Results.Redirect("/dist/index.html"));
app.Run();
public partial class Program { }
