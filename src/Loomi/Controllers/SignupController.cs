using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
// Nullable on purpose: a non-nullable value here would make [ApiController] answer bad input with its own ProblemDetails instead of a stable error code. What is left out stays as it is.
public record SignupSettings(bool? Open, int? GiftImages, int? DailyLimit, int? DailyWords);
/// <summary>The owner's switch for sign-up in the bot, and how much it may give away in a day. Telegram says whether a bot is set up at all: without one
/// the switch has nobody to let in.</summary>
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/signup/settings")]
public class SignupController(AppDbContext db, GrowthSettings growth, BotSettings bot) : ControllerBase
{
    private const int MaxGiftImages = 50, MaxDailyLimit = 1000;
    [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(new
    {
        open = await growth.OnAsync(GrowthSettings.SignupOpen, ct),
        giftImages = await growth.IntAsync(GrowthSettings.SignupGiftImages, GrowthSettings.DefaultGiftImages, ct),
        dailyLimit = await growth.IntAsync(GrowthSettings.SignupDailyLimit, GrowthSettings.DefaultSignupDailyLimit, ct),
        dailyWords = await growth.IntAsync(GrowthSettings.SignupDailyWords, GrowthSettings.DefaultSignupDailyWords, ct),
        today =await Signup.TodayAsync(db, ct),
        telegram = bot.Configured,
    });
    [HttpPut] public async Task<IActionResult> Put(SignupSettings request, CancellationToken ct)
    {
        if (request.GiftImages is < 0 or > MaxGiftImages || request.DailyLimit is < 0 or > MaxDailyLimit || request.DailyWords is < 0 or > MaxDailyLimit) return BadRequest(new { error = "InvalidSignupSettings" });
        if (request.GiftImages is { } gift) await growth.SetAsync(GrowthSettings.SignupGiftImages, gift.ToString(), ct);
        if (request.DailyLimit is { } limit) await growth.SetAsync(GrowthSettings.SignupDailyLimit, limit.ToString(), ct);
        if (request.DailyWords is { } words) await growth.SetAsync(GrowthSettings.SignupDailyWords, words.ToString(), ct);
        if (request.Open is { } open) await growth.SetOnAsync(GrowthSettings.SignupOpen, open, ct);
        return await Get(ct);
    }
}
