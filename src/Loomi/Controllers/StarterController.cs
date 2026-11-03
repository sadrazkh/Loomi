using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Loomi.Controllers;
/// <summary>The starter pack and the run that makes its samples: the owner's alone, since one spends the accounts' images and the other changes what people are offered.</summary>
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/templates")]
public class StarterController(StarterService starter) : ControllerBase
{
    [HttpPost("starter")] public async Task<IActionResult> Add(CancellationToken ct) => Ok(await starter.AddPackAsync(ct));
    [HttpGet("samples/status")] public async Task<IActionResult> Status(CancellationToken ct) => Ok(await starter.StatusAsync(ct));
    [HttpPost("samples/start")] public async Task<IActionResult> Start(CancellationToken ct) => Ok(await starter.StartAsync(Viewer.From(User).Id, ct));
    [HttpPost("samples/stop")] public async Task<IActionResult> Stop(CancellationToken ct) => Ok(await starter.StopAsync(ct));
}
