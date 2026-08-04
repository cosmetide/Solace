using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Solace.ApiServer.Controllers;

[Route("{**path}")]
internal sealed class CatchAllController : SolaceControllerBase
{
    [HttpGet]
    [HttpPost]
    [HttpPut]
    [HttpDelete]
    [HttpPatch]
    [HttpHead]
    [HttpOptions]
    public async Task<ContentHttpResult> CatchAll()
    {
        await LogRequestAsync(Request, "CatchAll");
        return JsonCamelCase(new { });
    }
}
