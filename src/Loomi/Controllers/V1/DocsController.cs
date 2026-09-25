using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace Loomi.Controllers.V1;
/// <summary>The machine-readable reference for /api/v1, for anyone signed in. The whole document at /openapi/v1.json stays the owner's outside
/// Development: it lists the owner's routes, and everything outside v1 is the app's own and may change under anyone who builds on it.</summary>
[ApiExplorerSettings(IgnoreApi = true)]
public class DocsController : ApiV1Controller
{
    private const string Schemas = "#/components/schemas/";
    [HttpGet("api/v1/openapi.json")]
    public async Task<IActionResult> OpenApi(CancellationToken ct)
    {
        var source = HttpContext.RequestServices.GetKeyedService<IOpenApiDocumentProvider>("v1") ?? throw new KeyNotFoundException();
        var document = JsonNode.Parse(await (await source.GetOpenApiDocumentAsync(ct)).SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, ct))!.AsObject();
        var paths = document["paths"]?.AsObject() ?? [];
        foreach (var path in paths.Select(p => p.Key).Where(p => !p.StartsWith("/api/v1/")).ToList()) paths.Remove(path);
        // Only the schemas those routes reach, followed through every reference, so the owner's side is not described by the back door either.
        if (document["components"]?["schemas"] is JsonObject schemas)
        {
            var reached = new HashSet<string>();
            var pending = new Stack<JsonNode?>([paths]);
            while (pending.TryPop(out var node))
            {
                if (node is JsonArray array) foreach (var item in array) pending.Push(item);
                if (node is not JsonObject obj) continue;
                foreach (var (key, value) in obj)
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var target) && target.StartsWith(Schemas) && reached.Add(target[Schemas.Length..])) pending.Push(schemas[target[Schemas.Length..]]);
                    else pending.Push(value);
            }
            foreach (var name in schemas.Select(s => s.Key).Where(n => !reached.Contains(n)).ToList()) schemas.Remove(name);
        }
        document["info"] = new JsonObject { ["title"] = "Loomi API", ["version"] = "v1", ["description"] = "Chats and messages that make images through ChatGPT. Send Authorization: Bearer lm_… with a personal token from Settings → API tokens." };
        document["security"] = new JsonArray(new JsonObject { ["Bearer"] = new JsonArray() });
        return Content(document.ToJsonString(), "application/json");
    }
    /// <summary>Anything else under /api/v1 is a miss in the usual shape, rather than an empty 404 a client cannot tell from a wrong address.</summary>
    [Route("api/v1/{**rest}")]
    public IActionResult Unknown() => NotFound(new ErrorDto("NotFound"));
}
