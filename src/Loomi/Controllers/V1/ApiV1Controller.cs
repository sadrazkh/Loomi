using Loomi.Models;
using Loomi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
namespace Loomi.Controllers.V1;
/// <summary>What every /api/v1 route shares: a signed-in caller, by personal token or by cookie, and errors in the one shape the rest of the app uses.</summary>
[ApiController, Authorize, ReadableRequest]
[ProducesResponseType<ErrorDto>(400), ProducesResponseType(401), ProducesResponseType<ErrorDto>(404), ProducesResponseType<ErrorDto>(429)]
public abstract class ApiV1Controller : ControllerBase
{
    /// <summary>The same ceiling GenerationService applies; checked here so a long prompt is a 400 like every other malformed request.</summary>
    public const int MaxPromptLength = 12000;
    protected Viewer Me => Viewer.From(User);
    protected static BadRequestObjectResult Invalid(string code) => new(new ErrorDto(code));
    protected static string? PromptError(string? prompt) => string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptLength ? "InvalidPrompt" : null;
    /// <summary>By name, as every response writes it. A number that happens to parse is not a provider.</summary>
    protected static bool TryProvider(string? name, out Provider? provider)
    {
        provider = name is null ? null : Enum.GetValues<Provider>().Cast<Provider?>().FirstOrDefault(p => p.ToString()!.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null || provider is not null;
    }
}
/// <summary>Answers a body or query that could not be read with a code, in the app's own error shape. It runs ahead of [ApiController]'s own check,
/// which would answer with a ProblemDetails document no other route here uses.</summary>
public sealed class ReadableRequestAttribute : ActionFilterAttribute
{
    public ReadableRequestAttribute() => Order = -3000;
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) context.Result = new BadRequestObjectResult(new ErrorDto("InvalidRequest"));
    }
}
