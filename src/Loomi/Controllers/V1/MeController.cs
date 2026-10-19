using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Controllers.V1;
/// <summary>Who the token speaks for and what they have left to spend, so a tool can check both before it sends anything.</summary>
[Route("api/v1/me")]
public class MeController(AppDbContext db, IOptions<BrowserOptions> browser) : ApiV1Controller
{
    [HttpGet, ProducesResponseType<MeDto>(200)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Me.Id, ct) ?? throw new KeyNotFoundException();
        var used = await db.UsedByAsync(user.Id, ct);
        // Zero is no daily ceiling at all, the reading Quota.RemainingAsync gives it.
        int? quota = user.DailyQuota > 0 ? user.DailyQuota : null;
        return Ok(new MeDto(user.Id, user.Username, user.Role.ToString().ToLowerInvariant(), user.Role == UserRole.Member ? await db.BalanceAsync(user.Id, ct) : null,
                            quota, used, quota is { } q ? Math.Max(0, q - used) : null, new Limits(browser.Value.MaxInputs, UploadsController.MaxFileBytes, MaxPromptLength),
                            await db.MineAsync(user.Id, DateTime.UtcNow, ct)));
    }
}
