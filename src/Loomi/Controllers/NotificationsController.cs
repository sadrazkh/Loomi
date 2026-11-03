using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
/// <summary>The owner's switches for the news the bot sends on its own, and whom each would reach. Session only: a leaked token should not be able to start
/// messaging everybody.</summary>
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), SessionOnly, Route("api/notifications")]
public class NotificationsController(GrowthSettings growth, NewsAudience audience, BotSettings bot) : ControllerBase
{
    /// <summary>A switch left out stays as it is, so two flipped close together cannot undo each other.</summary>
    public record Change(bool? NewTemplates, bool? WinBack, bool? LowCredit);
    [HttpGet("settings")] public async Task<IActionResult> Read(CancellationToken ct) => Ok(new
    {
        telegram = bot.Configured,
        newTemplates = await growth.OnAsync(GrowthSettings.NotifyNewTemplates, ct),
        winBack = await growth.OnAsync(GrowthSettings.NotifyWinBack, ct),
        lowCredit = await growth.OnAsync(GrowthSettings.NotifyLowCredit, ct),
        reach = await audience.ReachAsync(DateTime.UtcNow, ct),
    });
    [HttpPut("settings")] public async Task<IActionResult> Write(Change change, CancellationToken ct)
    {
        if (change.NewTemplates is { } newTemplates) await growth.SetOnAsync(GrowthSettings.NotifyNewTemplates, newTemplates, ct);
        if (change.WinBack is { } winBack) await growth.SetOnAsync(GrowthSettings.NotifyWinBack, winBack, ct);
        if (change.LowCredit is { } lowCredit) await growth.SetOnAsync(GrowthSettings.NotifyLowCredit, lowCredit, ct);
        return await Read(ct);
    }
}
