using System.ComponentModel.DataAnnotations;
using Loomi.Data;
using Loomi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Controllers;
public record PricingRuleDto(Provider Provider, Operation? Operation, [Range(0, 10000)] int Cost);
[ApiController, Authorize(Roles = nameof(UserRole.Owner)), Route("api/pricing")]
public class PricingController(AppDbContext db) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.PricingRules.AsNoTracking().OrderBy(r => r.Provider).ThenBy(r => r.Operation)
            .Select(r => new PricingRuleDto(r.Provider, r.Operation, r.Cost)).ToListAsync(ct));
    /// <summary>The list is replaced whole, so dropping a rule is simply leaving it out rather than a delete call of its own.</summary>
    [HttpPut] public async Task<IActionResult> Replace(List<PricingRuleDto> rules, CancellationToken ct)
    {
        if (rules.Any(r => r.Cost is < 0 or > 10000)) return BadRequest(new { error = "InvalidCost" });
        if (rules.GroupBy(r => (r.Provider, r.Operation)).Any(g => g.Count() > 1)) return BadRequest(new { error = "DuplicateRule" });
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.PricingRules.ExecuteDeleteAsync(ct);
        db.PricingRules.AddRange(rules.Select(r => new PricingRule { Provider = r.Provider, Operation = r.Operation, Cost = r.Cost }));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Ok(rules);
    }
}
