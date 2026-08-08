using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Solace.ApiServer.Models;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer.Controllers.XboxLive.Auth;

[Route("sisu.xboxlive.com/authorize")]
internal sealed class SisuAuthorizeController : SolaceControllerBase
{
    private static Config config => Program.config;

    internal sealed record AuthorizeRequest(
        string AppId,
        string DeviceToken,
        string Sandbox,
        string AccessToken,
        string? SessionId,
        string? SiteName,
        bool? UseModernGamertag
    );

    private sealed record Ticket(
        string IssueInstant,
        string NotAfter,
        string Token,
        Dictionary<string, object?> DisplayClaims
    );

    private sealed record AuthorizeResponse(
        string DeviceToken,
        Ticket UserToken,
        Ticket TitleToken,
        Ticket AuthorizationToken,
        string Sandbox,
        bool UseModernGamertag,
        string Flow
    );

    [HttpPost]
    public Results<ContentHttpResult, UnauthorizedHttpResult> Authorize([FromBody] AuthorizeRequest request)
    {
        string accessToken = request.AccessToken is { Length: > 2 } && request.AccessToken.StartsWith("t=")
            ? request.AccessToken[2..]
            : request.AccessToken;

        var ticket = JwtUtils.Verify<Tokens.Shared.XboxTicketToken>(accessToken, config.Login.XboxTokenSecretBytes)?.Data;
        var deviceAuth = JwtUtils.Verify<Tokens.Xbox.AuthToken>(request.DeviceToken, config.XboxLive.AuthTokenSecretBytes)?.Data;

        if (ticket is null || deviceAuth is not Tokens.Xbox.DeviceToken deviceToken)
        {
            return TypedResults.Unauthorized();
        }

        var tokenValidity = ValidityDatePair.Create(config.XboxLive.TokenValidityMinutes);

        var newDeviceToken = new Tokens.Xbox.DeviceToken()
        {
            Did = deviceToken.Did,
        };

        string deviceTokenString = JwtUtils.Sign<Tokens.Xbox.AuthToken>(newDeviceToken, config.XboxLive.AuthTokenSecretBytes, tokenValidity);

        var userToken = new Tokens.Xbox.UserToken()
        {
            Xid = ticket.UserId,
            Uhs = ticket.UserId,
            UserId = ticket.UserId,
            Username = ticket.Username,
        };

        string userTokenString = JwtUtils.Sign<Tokens.Xbox.AuthToken>(userToken, config.XboxLive.AuthTokenSecretBytes, tokenValidity);

        var titleToken = new Tokens.Xbox.TitleToken()
        {
            Tid = "2037747551",
        };

        string titleTokenString = JwtUtils.Sign<Tokens.Xbox.AuthToken>(titleToken, config.XboxLive.AuthTokenSecretBytes, tokenValidity);

        string authorizationTokenString = JwtUtils.Sign<Tokens.Xbox.AuthToken>(
            new Tokens.Xbox.UserToken()
            {
                Xid = ticket.UserId,
                Uhs = ticket.UserId,
                UserId = ticket.UserId,
                Username = ticket.Username,
            },
            config.XboxLive.AuthTokenSecretBytes,
            tokenValidity
        );

        var xui = new[]
        {
            new Dictionary<string, string>()
            {
                ["xid"] = userToken.Xid,
                ["uhs"] = userToken.Uhs,

                ["gtg"] = userToken.Username,
                ["agg"] = "Adult",

                ["usr"] = "185 190 234",
                ["prv"] = "184 186 187 188 191 193 195 196 198 199 200 201 203 204 205 206 208 211 217 220 224 227 228 235 238 245 247 249 252 254 255",
            },
        };

        return JsonPascalCaseRelaxed(new AuthorizeResponse(
            deviceTokenString,
            new Ticket(
                tokenValidity.IssuedStr,
                tokenValidity.ExpiresStr,
                userTokenString,
                new()
                {
                    ["xui"] = xui,
                }
            ),
            new Ticket(
                tokenValidity.IssuedStr,
                tokenValidity.ExpiresStr,
                titleTokenString,
                new()
                {
                    ["xdi"] = new Dictionary<string, string>()
                    {
                        ["tid"] = titleToken.Tid,
                    },
                }
            ),
            new Ticket(
                tokenValidity.IssuedStr,
                tokenValidity.ExpiresStr,
                authorizationTokenString,
                new()
                {
                    ["xui"] = xui,
                }
            ),
            request.Sandbox is { Length: > 0 } ? request.Sandbox : "RETAIL",
            true,
            "signin"
        ));
    }
}
