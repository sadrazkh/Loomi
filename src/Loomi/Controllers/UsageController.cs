using Loomi.Models;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers;
/// <summary>Who uses Loomi and how much: the owner's view of everybody's requests. The pictures themselves come from /api/generations/{id}/image,
/// which an owner may already open for anyone.</summary>
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/usage")]
public class UsageController(UsageReport report, BotSettings bot, IOptions<ReportOptions> options) : ControllerBase
{
    public record DailyReport(bool Enabled);
    private BadRequestObjectResult InvalidPeriod() => BadRequest(new { error = "InvalidPeriod" });
    [HttpGet] public async Task<IActionResult> Summary(string? period, CancellationToken ct) =>
        UsageReport.TryParse(period, out var p) ? Ok(await report.SummaryAsync(p, ct)) : InvalidPeriod();
    [HttpGet("users/{id:guid}")] public async Task<IActionResult> Person(Guid id, string? period, CancellationToken ct) =>
        !UsageReport.TryParse(period, out var p) ? InvalidPeriod() : await report.UserAsync(id, p, ct) is { } usage ? Ok(usage) : NotFound();
    [HttpGet("users/{id:guid}/requests")] public async Task<IActionResult> Requests(Guid id, int skip = 0, int take = 20, CancellationToken ct = default) =>
        Ok(await report.RequestsAsync(id, skip, take, ct));
    /// <summary>Telegram says whether a bot is set up at all: without one the switch has nothing to send through.</summary>
    [HttpGet("daily-report")] public async Task<IActionResult> Daily(CancellationToken ct) =>
        Ok(new { enabled = await report.DailyEnabledAsync(ct), hour = options.Value.DailyHour, telegram = bot.Configured });
    [HttpPut("daily-report")] public async Task<IActionResult> SetDaily(DailyReport request, CancellationToken ct)
    {
        await report.SetDailyEnabledAsync(request.Enabled, ct);
        return await Daily(ct);
    }
}
