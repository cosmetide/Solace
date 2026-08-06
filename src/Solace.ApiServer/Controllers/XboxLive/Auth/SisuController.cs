using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Solace.ApiServer.Models;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer.Controllers.XboxLive.Auth;

[Route("sisu.xboxlive.com/authenticate")]
internal sealed class SisuController : SolaceControllerBase
{
    private static Config config => Program.config;

    internal sealed record AuthenticateRequest(
        string AppId,
        string RedirectUri,
        string DeviceToken,
        string Sandbox,
        string TokenType,
        string[] Offers,
        AuthenticateRequest.QueryR Query
    )
    {
        internal sealed record QueryR(
            string? Display,
            string? CodeChallenge,
            string? CodeChallengeMethod,
            string? State
        );
    }

    private sealed record AuthenticateResponse(
        string MsaOauthRedirect,
        object MsaRequestParameters
    );

    [HttpPost]
    public Results<ContentHttpResult, UnauthorizedHttpResult> Authenticate([FromBody] AuthenticateRequest request)
    {
        var deviceToken = JwtUtils.Verify<Tokens.Xbox.AuthToken>(request.DeviceToken, config.XboxLive.AuthTokenSecretBytes)?.Data;

        if (deviceToken is not Tokens.Xbox.DeviceToken)
        {
            return TypedResults.Unauthorized();
        }

        Response.Headers["X-SessionId"] = Guid.NewGuid().ToString();

        var queryParams = new List<KeyValuePair<string, string>>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                queryParams.Add(new(key, value));
            }
        }

        Add("client_id", request.AppId);
        Add("response_type", "code");
        Add("scope", "service::user.auth.xboxlive.com::MBI_SSL");
        Add("redirect_uri", request.RedirectUri);
        Add("code_challenge", request.Query.CodeChallenge);
        Add("code_challenge_method", request.Query.CodeChallengeMethod);
        Add("state", request.Query.State);
        Add("display", request.Query.Display);

        string redirect = $"{Request.Scheme}://{Request.Host}/oauth20_authorize.srf?"
            + string.Join("&", queryParams.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return JsonPascalCase(new AuthenticateResponse(redirect, new { }));
    }
}
