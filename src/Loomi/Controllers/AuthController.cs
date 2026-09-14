using Loomi.BrowserAutomation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
[ApiController, Authorize, Route("api/auth")]
public class AuthController(BrowserAutomationService browser) : ControllerBase
{
    [HttpGet("status")] public async Task<IActionResult> Status() => Ok(await browser.StatusAsync());
    [HttpPost("connect")] public async Task<IActionResult> Connect(CancellationToken ct) => Ok(await browser.ConnectAsync(ct));
    [HttpPost("reset")] public async Task<IActionResult> Reset(CancellationToken ct) { await browser.ResetAsync(ct); return NoContent(); }
}
