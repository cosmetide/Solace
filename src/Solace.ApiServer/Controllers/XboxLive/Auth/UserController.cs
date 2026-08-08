using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Solace.ApiServer.Models;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer.Controllers.XboxLive.Auth;

[Route("user/authenticate")]
[Route("user.auth.xboxlive.com/user/authenticate")]
internal sealed class UserController : SolaceControllerBase
{
    private static Config config => Program.config;

    public sealed record AuthenticateRequest(
        AuthenticateRequest.PropertiesR Properties,
        string RelyingParty,
        string TokenType
    )
    {
        public sealed record PropertiesR(
            string AuthMethod,
            string RpsTicket,
            string SiteName
        );
    }

    private sealed record AuthenticateResponse(
        string IssueInstant,
        string NotAfter,
        string Token,
        Dictionary<string, Dictionary<string, string>[]> DisplayClaims
    );

    [HttpPost]
    public Results<ContentHttpResult, UnauthorizedHttpResult> Authenticate([FromBody] AuthenticateRequest request)
    {
        string? rpsTicket = request.Properties.RpsTicket;

        if (rpsTicket is not null && rpsTicket.Length > 2 && (rpsTicket[0] is 'd' or 't') && rpsTicket[1] == '=')
        {
            rpsTicket = rpsTicket[2..];
        }

        var ticket = JwtUtils.Verify<Tokens.Shared.XboxTicketToken>(rpsTicket ?? string.Empty, config.Login.XboxTokenSecretBytes)?.Data;

        if (ticket is null)
        {
            return TypedResults.Unauthorized();
        }

        var tokenValidity = ValidityDatePair.Create(config.XboxLive.TokenValidityMinutes);
        var token = new Tokens.Xbox.UserToken()
        {
            Xid = ticket.UserId,
            Uhs = ticket.UserId,

            UserId = ticket.UserId,
            Username = ticket.Username,
        };

        return JsonPascalCase(new AuthenticateResponse(
            tokenValidity.IssuedStr,
            tokenValidity.ExpiresStr,
            JwtUtils.SignXboxUserToken(token, config.XboxLive.AuthTokenSecretBytes, tokenValidity),
            new()
            {
                ["xui"] = [
                    new()
                    {
                        ["uhs"] = token.Uhs,
                    },
                ],
            }
        ));
    }
}
