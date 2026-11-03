using Loomi.Models;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
public record SiteSettingsRequest(List<SitePrice?>? Prices, string? Contact);
/// <summary>What the intro page says about prices and where to buy, which only the owner writes. Sign-up has its own switches on the bot page.</summary>
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/site/settings")]
public class SiteController(GrowthSettings settings) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await ReadAsync(ct));
    /// <summary>Replaced whole, and judged whole before either value is written, so a refusal leaves the page as it was.</summary>
    [HttpPut] public async Task<IActionResult> Put(SiteSettingsRequest request, CancellationToken ct)
    {
        if (SiteContent.Contact(request.Contact) is not { } contact) return BadRequest(new { error = "InvalidContact" });
        if (SiteContent.Encode(request.Prices) is not { } prices) return BadRequest(new { error = "InvalidPrices" });
        await settings.SetAsync(GrowthSettings.SitePrices, prices, ct);
        await settings.SetAsync(GrowthSettings.SiteContact, contact, ct);
        return Ok(await ReadAsync(ct));
    }
    private async Task<object> ReadAsync(CancellationToken ct)
    {
        var contact = await settings.GetAsync(GrowthSettings.SiteContact, ct);
        return new { prices = SiteContent.Decode(await settings.GetAsync(GrowthSettings.SitePrices, ct)), contact = string.IsNullOrEmpty(contact) ? null : contact };
    }
}
