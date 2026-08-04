using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Solace.ApiServer.Controllers.XboxLive.Auth;

[Route("auth.xboxlive.com")]
internal sealed class AuthRootController : SolaceControllerBase
{
    [HttpGet("")]
    public async Task<ContentHttpResult> Root()
    {
        await LogRequestAsync(Request, "AuthRoot");
        return JsonCamelCase(new { });
    }
}
