using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using Solace.ApiServer.Models;
using Solace.ApiServer.Utils;
using Solace.Common.Utils;

namespace Solace.ApiServer.Controllers.Live;

[Route("")]
[Route("login.live.com")]
internal sealed partial class LoginController : SolaceControllerBase
{
    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    private static Config config => Program.config;

    private static readonly ConcurrentDictionary<string, OAuthCode> _oauthCodes = new(StringComparer.Ordinal);

    private sealed record OAuthCode(string UserId, string Username, string RedirectUri, string ClientId, long ExpiresAtUnixSeconds);

    private sealed record OAuthIdToken(string Sub, string Name, string PreferredUsername) : ITokenData<OAuthIdToken>;

    private readonly LiveDbContext _dbContext;

    private static readonly (string, string)[] namespaces =
    [
        ("S", "http://www.w3.org/2003/05/soap-envelope"),
        ("wsse", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"),
        ("wsu", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"),
        ("wsp", "http://schemas.xmlsoap.org/ws/2004/09/policy"),
        ("wst", "http://schemas.xmlsoap.org/ws/2005/02/trust"),
        ("wssc", "http://schemas.xmlsoap.org/ws/2005/02/sc"),
        ("wsa", "http://www.w3.org/2005/08/addressing"),
        ("ps", "http://schemas.microsoft.com/Passport/SoapServices/PPCRL"),
        ("psf", "http://schemas.microsoft.com/Passport/SoapServices/SOAPFault"),
        ("e", "http://www.w3.org/2001/04/xmlenc#"),
        ("ds", "http://www.w3.org/2000/09/xmldsig#"),
        ("ns1", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"),
    ];

    public LoginController(LiveDbContext context)
    {
        _dbContext = context;
    }

    [HttpGet("ppsecure/InlineConnect.srf")]
    public VirtualFileHttpResult GetLoginPage()
        => TypedResults.VirtualFile("/login.html", "text/html");

    [HttpGet("ppsecure/reauthenticateStart")]
    public VirtualFileHttpResult GetReauthenticatePage()
        => TypedResults.VirtualFile("/reauthenticate.html", "text/html");

    private sealed record LoginResponse(
        string UserId,
        string Username,
        string FirstName,
        string LastName,
        string Token,
        string TokenIssuedAt,
        string TokenExpires,
        string SessionKey
    );

    [HttpPost("ppsecure/login")]
    public async Task<Results<ContentHttpResult, BadRequest<string>>> Login([FromForm] string username, [FromForm] string password, CancellationToken cancellationToken)
    {
        username = username.Trim();
        password = password.Trim();

        Log.Debug($"Login attempt: Username: {username}");

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(account => account.Username == username, cancellationToken);

        if (account is null)
        {
            return TypedResults.BadRequest("Username or password is incorrect");
        }

        byte[] passwordHash = HashPassword(password, account.PasswordSalt);

        if (!passwordHash.AsSpan().SequenceEqual(account.PasswordHash))
        {
            return TypedResults.BadRequest("Username or password is incorrect");
        }

        return JsonCamelCase(CreateLoginResponse(account));
    }

    [HttpPost("ppsecure/register")]
    public async Task<Results<ContentHttpResult, BadRequest<string>>> Register([FromForm] string username, [FromForm] string password, [FromForm] string? firstName, [FromForm] string? lastName, CancellationToken cancellationToken)
    {
        username = username.Trim();
        password = password.Trim();
        firstName = firstName?.Trim();
        lastName = lastName?.Trim();

        if (firstName is { Length: 0 })
        {
            firstName = null;
        }

        if (lastName is { Length: 0 })
        {
            lastName = null;
        }

        Log.Debug($"Register attempt: Username: {username}, First name: {firstName}, Last name: {lastName}");

        if (string.IsNullOrWhiteSpace(username) || username.Length < 3 || username.Length > 16)
        {
            return TypedResults.BadRequest("Username must be 3-16 characters long");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 4 || password.Length > 32)
        {
            return TypedResults.BadRequest("Password must be 4-32 characters long");
        }

        if (!string.IsNullOrWhiteSpace(firstName) && (firstName.Length < 2 || firstName.Length > 100))
        {
            return TypedResults.BadRequest("First name must be 2-100 characters long");
        }

        if (!string.IsNullOrWhiteSpace(lastName) && (lastName.Length < 2 || lastName.Length > 100))
        {
            return TypedResults.BadRequest("Last name must be 2-100 characters long");
        }

        if (!GetUsernameRegex().IsMatch(username))
        {
            return TypedResults.BadRequest("Username must contain only: lowercase letters, numbers, underscore and colon");
        }

        var account = await _dbContext.Accounts
            .FirstOrDefaultAsync(account => account.Username == username, cancellationToken);

        if (account is not null)
        {
            return TypedResults.BadRequest("Account with the specified username already exists");
        }

        string userId = GenerateUserId(username);

        byte[] passwordSalt = new byte[16];
        _rng.GetBytes(passwordSalt);

        byte[] paswordHash = HashPassword(password, passwordSalt);

        account = new Account()
        {
            Id = userId,
            CreatedDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Username = username,
            ProfilePictureUrl = $"images/default_pfp.png", // TODO
            FirstName = firstName,
            LastName = lastName,
            PasswordSalt = passwordSalt,
            PasswordHash = paswordHash,
        };

        try
        {
            _dbContext.Accounts.Add(account);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            Log.Error(ex, "Failed to create account {Username}", username);
            return TypedResults.BadRequest("Failed to create account, please try again");
        }

        Log.Information($"Account created: {username} ({userId})");

        return JsonCamelCase(CreateLoginResponse(account));
    }

    [HttpPost("ppsecure/reauthenticate")]
    public async Task<IActionResult> Reauthenticate([FromForm] string userToken, [FromForm] string password, CancellationToken cancellationToken)
        => throw new NotImplementedException(); // TODO

    [HttpPost("ppsecure/deviceaddcredential.srf")]
    public ContentHttpResult DeviceAddCredential()
        => TypedResults.Content("""
            <DeviceAddResponse Success="true"><success>true</success><puid>0</puid></DeviceAddResponse>
            """);

    [HttpGet("oauth20_desktop.srf")]
    [HttpGet("oauth20_authorize.srf")]
    public VirtualFileHttpResult GetOAuthLoginPage()
        => TypedResults.VirtualFile("/oauth_login.html", "text/html");

    [HttpPost("oauth20_desktop.srf")]
    public async Task<Results<StatusCodeHttpResult, ContentHttpResult, BadRequest<string>>> OAuthLogin(
        [FromForm] string username,
        [FromForm] string password,
        [FromForm(Name = "redirect_uri")] string? redirectUri,
        [FromForm] string? state,
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm] string? register,
        [FromForm] string? firstName,
        [FromForm] string? lastName,
        CancellationToken cancellationToken)
    {
        username = username.Trim();
        password = password.Trim();
        firstName = firstName?.Trim();
        lastName = lastName?.Trim();

        if (firstName is { Length: 0 })
        {
            firstName = null;
        }

        if (lastName is { Length: 0 })
        {
            lastName = null;
        }

        Log.Debug($"OAuth20 login attempt: Username: {username}, RedirectUri: {redirectUri}");

        var account = await _dbContext.Accounts.FirstOrDefaultAsync(account => account.Username == username, cancellationToken);

        if (account is null)
        {
            if (register is not { Length: > 0 })
            {
                return TypedResults.BadRequest("Username or password is incorrect");
            }

            account = await CreateOAuthAccountAsync(username, password, firstName, lastName, cancellationToken);

            if (account is null)
            {
                return TypedResults.BadRequest("Username must be 3-16 characters long and password must be 4-32 characters long");
            }
        }
        else
        {
            byte[] passwordHash = HashPassword(password, account.PasswordSalt);

            if (!passwordHash.AsSpan().SequenceEqual(account.PasswordHash))
            {
                return TypedResults.BadRequest("Username or password is incorrect");
            }
        }

        HttpContext.Response.Cookies.Append("solace_user", account.Username, new CookieOptions()
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });

        string code = GenerateOAuthCode();
        string target = string.IsNullOrWhiteSpace(redirectUri) ? "ms-xal-0000000040281e53://xbox-signedin" : redirectUri.Trim();

        _oauthCodes[code] = new OAuthCode(account.Id, account.Username, target, clientId ?? string.Empty, DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

        string query = $"code={code}";
        if (!string.IsNullOrWhiteSpace(state))
        {
            query += $"&state={Uri.EscapeDataString(state)}";
        }

        string location = $"{target}{(target.Contains('?') ? "&" : "?")}{query}";

        Log.Debug($"OAuth20 redirect: {location}");

        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        HttpContext.Response.Headers.Location = location;
        return TypedResults.StatusCode(StatusCodes.Status302Found);
    }

    [HttpPost("oauth20_token.srf")]
#pragma warning disable IDE0060 // Remove unused parameter
    public ContentHttpResult OAuthToken(
        [FromForm(Name = "grant_type")] string grantType,
        [FromForm(Name = "code")] string? code,
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "redirect_uri")] string? redirectUri,
        [FromForm(Name = "scope")] string? scope,
        [FromForm(Name = "refresh_token")] string? refreshToken)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        Log.Debug($"OAuth20 token request: GrantType: {grantType}, ClientId: {clientId}");

        switch (grantType)
        {
            case "authorization_code":
                {
                    if (code is null || !_oauthCodes.TryRemove(code, out OAuthCode? oauthCode))
                    {
                        return TypedResults.Content("""{"error":"invalid_grant"}""", "application/json", statusCode: 400);
                    }

                    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > oauthCode.ExpiresAtUnixSeconds)
                    {
                        return TypedResults.Content("""{"error":"invalid_grant"}""", "application/json", statusCode: 400);
                    }

                    return CreateOAuthTokenResponse(oauthCode.UserId, oauthCode.Username, scope);
                }

            case "refresh_token":
                {
                    if (refreshToken is null)
                    {
                        return TypedResults.Content("""{"error":"invalid_grant"}""", "application/json", statusCode: 400);
                    }

                    var refreshData = JwtUtils.Verify<Tokens.Shared.XboxTicketToken>(refreshToken, config.Login.XboxTokenSecretBytes)?.Data;

                    if (refreshData is null)
                    {
                        return TypedResults.Content("""{"error":"invalid_grant"}""", "application/json", statusCode: 400);
                    }

                    return CreateOAuthTokenResponse(refreshData.UserId, refreshData.Username, scope);
                }

            default:
                return TypedResults.Content("""{"error":"unsupported_grant_type"}""", "application/json", statusCode: 400);
        }
    }

    [HttpGet("oauth20_logout.srf")]
    [HttpPost("oauth20_logout.srf")]
    public ContentHttpResult OAuthLogout(
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery] string? state,
        [FromQuery(Name = "client_id")] string? clientId)
    {
        string target = string.IsNullOrWhiteSpace(redirectUri) ? "ms-xal-0000000040281e53://auth" : redirectUri.Trim();

        string location = target;
        if (!string.IsNullOrWhiteSpace(state))
        {
            location += (location.Contains('?') ? "&" : "?") + $"state={Uri.EscapeDataString(state)}";
        }

        string username = HttpContext.Request.Cookies["solace_user"] ?? string.Empty;
        string displayName = string.IsNullOrWhiteSpace(username)
            ? "Welcome back!"
            : $"Welcome back, {HttpUtility.HtmlEncode(username)}!";
        string redirectJs = location.Replace("\\", "\\\\").Replace("'", "\\'");

        string loginUri = "/oauth20_authorize.srf";
        var loginParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(target))
        {
            loginParams.Add($"redirect_uri={Uri.EscapeDataString(target)}");
        }
        if (!string.IsNullOrWhiteSpace(state))
        {
            loginParams.Add($"state={Uri.EscapeDataString(state)}");
        }
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            loginParams.Add($"client_id={Uri.EscapeDataString(clientId)}");
        }
        if (loginParams.Count > 0)
        {
            loginUri += "?" + string.Join("&", loginParams);
        }
        string loginJs = loginUri.Replace("\\", "\\\\").Replace("'", "\\'");

        Log.Debug($"OAuth20 logout: Location: {location}, User: {username}");

        return TypedResults.Content($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>Sign-in complete</title>
                <style>
                    body {
                        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                        background: #f5f5f7;
                        color: #1d1d1f;
                        margin: 0;
                        height: 100vh;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                    }
                    .card { text-align: center; max-width: 360px; }
                    h1 { font-size: 24px; font-weight: 600; margin: 0 0 8px; }
                    p { font-size: 15px; color: #6e6e73; margin: 0 0 28px; }
                    .btn {
                        display: block; width: 100%; box-sizing: border-box;
                        padding: 14px 16px; font-size: 16px; font-weight: 600;
                        border: none; border-radius: 12px; cursor: pointer;
                        background: #6b8e23; color: #fff; margin-bottom: 16px;
                    }
                    .btn:active { transform: scale(0.98); }
                    .link {
                        display: inline-block; font-size: 14px; color: #0071e3;
                        text-decoration: none; cursor: pointer; background: none; border: none;
                    }
                </style>
            </head>
            <body>
                <div class="card">
                    <h1>{{displayName}}</h1>
                    <p>You're signed in. Ready to play?</p>
                    <button type="button" id="letsGoBtn" class="btn">Let's Go</button>
                    <button type="button" id="switchBtn" class="link">Sign in with a different account</button>
                </div>
                <script>
                    document.getElementById('letsGoBtn').addEventListener('click', function () {
                        window.location.replace('{{redirectJs}}');
                    });
                    document.getElementById('switchBtn').addEventListener('click', function () {
                        window.location.href = '{{loginJs}}';
                    });
                </script>
            </body>
            </html>
            """);
    }

    private static ContentHttpResult CreateOAuthTokenResponse(string userId, string username, string? scope)
    {
        var tokenValidity = ValidityDatePair.Create(config.Login.XboxTokenValidityMinutes);
        string accessToken = JwtUtils.Sign(new Tokens.Shared.XboxTicketToken(userId, username), config.Login.XboxTokenSecretBytes, tokenValidity);

        var idTokenValidity = ValidityDatePair.Create(config.Login.UserTokenValidityMinutes);
        string idToken = JwtUtils.Sign(new OAuthIdToken(userId, username, username), config.Login.UserTokenSecretBytes, idTokenValidity);

        return JsonCamelCase(new Dictionary<string, object?>()
        {
            ["access_token"] = accessToken,
            ["token_type"] = "bearer",
            ["expires_in"] = (long)tokenValidity.Expires.Subtract(tokenValidity.Issued).TotalSeconds,
            ["refresh_token"] = accessToken,
            ["scope"] = string.IsNullOrWhiteSpace(scope) ? "service::user.auth.xboxlive.com::MBI_SSL" : scope,
            ["id_token"] = idToken,
            ["user_id"] = userId,
        });
    }

    private static string GenerateOAuthCode()
    {
        Span<byte> buffer = stackalloc byte[24];
        _rng.GetBytes(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    private async Task<Account?> CreateOAuthAccountAsync(string username, string password, string? firstName, string? lastName, CancellationToken cancellationToken)
    {
        if (username.Length is < 3 or > 16)
        {
            return null;
        }

        if (password.Length is < 4 or > 32)
        {
            return null;
        }

        if (firstName is not null && (firstName.Length is < 2 or > 100))
        {
            return null;
        }

        if (lastName is not null && (lastName.Length is < 2 or > 100))
        {
            return null;
        }

        if (!GetUsernameRegex().IsMatch(username))
        {
            return null;
        }

        byte[] passwordSalt = new byte[16];
        _rng.GetBytes(passwordSalt);

        byte[] passwordHash = HashPassword(password, passwordSalt);

        var account = new Account()
        {
            Id = GenerateUserId(username),
            CreatedDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Username = username,
            ProfilePictureUrl = "images/default_pfp.png",
            FirstName = firstName,
            LastName = lastName,
            PasswordSalt = passwordSalt,
            PasswordHash = passwordHash,
        };

        try
        {
            _dbContext.Accounts.Add(account);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            Log.Error(ex, "Failed to create OAuth20 account {Username}", username);
            return null;
        }

        Log.Information($"OAuth20 account created: {username} ({account.Id})");

        return account;
    }

    [HttpPost("RST2.srf")]
    public async Task<Results<ContentHttpResult, BadRequest>> RST2()
    {
        var cancellationToken = Request.HttpContext.RequestAborted;

        var request = new XmlDocument();
        string rq;
        try
        {
            rq = await Request.Body.ReadAsString(cancellationToken);
            request.LoadXml(rq);
        }
        catch
        {
            return TypedResults.BadRequest();
        }

        var nsmgr = new XmlNamespaceManager(request.NameTable);
        foreach (var (prefix, uri) in namespaces)
        {
            nsmgr.AddNamespace(prefix, uri);
        }

        if (request.SelectSingleNode("/S:Envelope/S:Body/wst:RequestSecurityToken", nsmgr) is not null)
        {
            // device token request
#pragma warning disable IDE0059 // Unnecessary assignment of a value
            string? username = request.SelectSingleNode("/S:Envelope/S:Header/wsse:Security/wsse:UsernameToken/wsse:Username/text()", nsmgr)?.Value;
            string? password = request.SelectSingleNode("/S:Envelope/S:Header/wsse:Security/wsse:UsernameToken/wsse:Password/text()", nsmgr)?.Value;
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            string? requestType = request.SelectSingleNode("/S:Envelope/S:Body/wst:RequestSecurityToken/wst:RequestType/text()", nsmgr)?.Value;
            string? requestAppliesTo = request.SelectSingleNode("/S:Envelope/S:Body/wst:RequestSecurityToken/wsp:AppliesTo/wsa:EndpointReference/wsa:Address/text()", nsmgr)?.Value;

            if (requestType is not "http://schemas.xmlsoap.org/ws/2005/02/trust/Issue" || requestAppliesTo is not "http://Passport.NET/tb")
            {
                return TypedResults.BadRequest();
            }

            var headerValidity = ValidityDatePair.Create(config.Login.SoapHeaderValidityMinutes);

            var deviceTokenValidity = ValidityDatePair.Create(config.Login.DeviceTokenValidityMinutes);
            var deviceToken = new Tokens.Live.DeviceToken();
            string deviceTokenString = JwtUtils.Sign(deviceToken, config.Login.DeviceTokenSecretBytes, deviceTokenValidity);

            var response = new XmlDocument();

            var envelope = CreateElement(response, "S", "Envelope");
            envelope.SetAttribute("xmlns:wsse", nsmgr.LookupNamespace("wsse"));
            envelope.SetAttribute("xmlns:wsu", nsmgr.LookupNamespace("wsu"));
            envelope.SetAttribute("xmlns:wsp", nsmgr.LookupNamespace("wsp"));
            envelope.SetAttribute("xmlns:wst", nsmgr.LookupNamespace("wst"));
            envelope.SetAttribute("xmlns:wssc", nsmgr.LookupNamespace("wssc"));
            envelope.SetAttribute("xmlns:wsa", nsmgr.LookupNamespace("wsa"));
            envelope.SetAttribute("xmlns:ps", nsmgr.LookupNamespace("ps"));
            envelope.SetAttribute("xmlns:psf", nsmgr.LookupNamespace("psf"));
            envelope.SetAttribute("xmlns:e", nsmgr.LookupNamespace("e"));
            envelope.SetAttribute("xmlns:ds", nsmgr.LookupNamespace("ds"));

            var header = CreateElement(response, "S", "Header");
            {
                var security = CreateElement(response, "wsse", "Security");
                var timestamp = CreateElement(response, "wsu", "Timestamp");
                timestamp.SetAttribute("wsu:Id", "Timestamp");
                {
                    var created = CreateElement(response, "wsu", "Created");
                    created.InnerText = headerValidity.IssuedStr;
                    timestamp.AppendChild(created);
                    var expires = CreateElement(response, "wsu", "Expires");
                    expires.InnerText = headerValidity.ExpiresStr;
                    timestamp.AppendChild(expires);
                }

                security.AppendChild(timestamp);
                header.AppendChild(security);

                var pp = CreateElement(response, "psf", "pp");
                header.AppendChild(pp);
            }

            envelope.AppendChild(header);

            var body = CreateElement(response, "S", "Body");
            {
                var requestSecurityTokenResponse = CreateElement(response, "wst", "RequestSecurityTokenResponse");
                {
                    var tokenType = CreateElement(response, "wst", "TokenType");
                    tokenType.InnerText = "urn:passport:legacy";
                    requestSecurityTokenResponse.AppendChild(tokenType);

                    var appliesTo = CreateElement(response, "wsp", "AppliesTo");
                    {
                        var endpointReference = CreateElement(response, "wsa", "EndpointReference");
                        {
                            var address = CreateElement(response, "wsa", "Address");
                            address.InnerText = "http://Passport.NET/tb";
                            endpointReference.AppendChild(address);
                        }

                        appliesTo.AppendChild(endpointReference);
                    }

                    requestSecurityTokenResponse.AppendChild(appliesTo);

                    var lifetime = CreateElement(response, "wst", "Lifetime");
                    {
                        var created = CreateElement(response, "wsu", "Created");
                        created.InnerText = deviceTokenValidity.IssuedStr;
                        lifetime.AppendChild(created);

                        var expires = CreateElement(response, "wsu", "Expires");
                        expires.InnerText = deviceTokenValidity.ExpiresStr;
                        lifetime.AppendChild(expires);
                    }

                    requestSecurityTokenResponse.AppendChild(lifetime);

                    var requestedSecurityToken = CreateElement(response, "wst", "RequestedSecurityToken");
                    {
                        /*var encryptedData = CreateElement(response, "e", "EncryptedData");
                        encryptedData.SetAttribute("Id", "BinaryDAToken0");
                        {
                            var cipherData = CreateElement(response, "e", "CipherData");
                            {
                                var cipherValue = CreateElement(response, "e", "CipherValue");
                                cipherValue.InnerText = deviceTokenString;
                                cipherData.AppendChild(cipherValue);
                            }

                            encryptedData.AppendChild(cipherData);
                        }*/
                        var encryptedData = response.CreateElement("EncryptedData");
                        encryptedData.SetAttribute("Id", "BinaryDAToken0");
                        {
                            var cipherData = response.CreateElement("CipherData");
                            {
                                var cipherValue = response.CreateElement("CipherValue");
                                cipherValue.InnerText = deviceTokenString;
                                cipherData.AppendChild(cipherValue);
                            }

                            encryptedData.AppendChild(cipherData);
                        }

                        requestedSecurityToken.AppendChild(encryptedData);
                    }

                    requestSecurityTokenResponse.AppendChild(requestedSecurityToken);

                    var requestedProofToken = CreateElement(response, "wst", "RequestedProofToken");
                    {
                        var binarySecret = CreateElement(response, "wst", "BinarySecret");
                        binarySecret.InnerText = "0000";
                        requestedProofToken.AppendChild(binarySecret);
                    }

                    requestSecurityTokenResponse.AppendChild(requestedProofToken);
                }

                body.AppendChild(requestSecurityTokenResponse);
            }

            envelope.AppendChild(body);

            response.AppendChild(envelope);

            return TypedResults.Content("""
                <?xml version="1.0" encoding="UTF-8"?>

                """ + response.OuterXml);
        }
        else if (request.SelectSingleNode("/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens", nsmgr) is not null)
        {
            // user token request (user token + device token -> next user token + next session key + xbox token)

            string? userTokenString = request.SelectSingleNode("/S:Envelope/S:Header/wsse:Security/e:EncryptedData[@Id='BinaryDAToken0']/e:CipherData/e:CipherValue", nsmgr)?.InnerText;
            string? deviceDATokenString = request.SelectSingleNode("/S:Envelope/S:Header/wsse:Security/wsse:BinarySecurityToken[@Id='DeviceDAToken']", nsmgr)?.InnerText;

            string? deviceDATokenXMLStringEncoded = null;
            if (!string.IsNullOrEmpty(deviceDATokenString))
            {
                var match = GetDeviceDATokenStringRegex().Match(deviceDATokenString);
                if (match.Success && match.Groups.Count > 1)
                {
                    deviceDATokenXMLStringEncoded = match.Groups[1].Value;
                }
            }

            string? deviceDATokenXMLString = HttpUtility.UrlDecode(deviceDATokenXMLStringEncoded);

            string deviceTokenString = string.Empty;
            if (deviceDATokenXMLString is not null)
            {
                var deviceTokenXml = new XmlDocument();
                deviceTokenXml.LoadXml(deviceDATokenXMLString);
                if (deviceTokenXml is not null)
                {
                    deviceTokenString = deviceTokenXml.SelectSingleNode("/EncryptedData/CipherData/CipherValue")?.InnerText ?? string.Empty;
                }
            }

            double requestCount = EvaluateNumber(request, "count(/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens/*)", nsmgr);

            string? requestType1 = request.SelectSingleNode("/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens/wst:RequestSecurityToken[1]/wst:RequestType/text()", nsmgr)?.InnerText;
            string? appliesTo1 = request.SelectSingleNode("/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens/wst:RequestSecurityToken[1]/wsp:AppliesTo/wsa:EndpointReference/wsa:Address/text()", nsmgr)?.InnerText;
            string? requestType2 = request.SelectSingleNode("/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens/wst:RequestSecurityToken[2]/wst:RequestType/text()", nsmgr)?.InnerText;
            string? appliesTo2 = request.SelectSingleNode("/S:Envelope/S:Body/ps:RequestMultipleSecurityTokens/wst:RequestSecurityToken[2]/wsp:AppliesTo/wsa:EndpointReference/wsa:Address/text()", nsmgr)?.InnerText;

            if (requestCount is not 2 || requestType1 is not "http://schemas.xmlsoap.org/ws/2005/02/trust/Issue" || appliesTo1 is not "http://Passport.NET/tb" || requestType2 is not "http://schemas.xmlsoap.org/ws/2005/02/trust/Issue" || appliesTo2 is not "cobrandid=90023&scope=service%3A%3Auser.auth.xboxlive.com%3A%3Ambi_ssl" || userTokenString is null)
            {
                return TypedResults.BadRequest();
            }

            var userToken = JwtUtils.Verify<Tokens.Live.UserToken>(userTokenString, config.Login.UserTokenSecretBytes, allowExpired: true);
#pragma warning disable IDE0059 // Unnecessary assignment of a value
            var deviceToken = JwtUtils.Verify<Tokens.Live.DeviceToken>(deviceTokenString, config.Login.DeviceTokenSecretBytes, allowExpired: true);
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            if (userToken is null || userToken.Expired is true)
            {
                // TODO
                throw new NotImplementedException();
            }
            else
            {
                var headerValidity = ValidityDatePair.Create(config.Login.SoapHeaderValidityMinutes);
                string nonce = GenerateNonce();

                var nextUserTokenValidity = ValidityDatePair.Create(config.Login.UserTokenValidityMinutes);
                var nextUserToken = userToken.Data;
                string nextUserTokenString = JwtUtils.Sign(nextUserToken, config.Login.UserTokenSecretBytes, nextUserTokenValidity);

                var xboxTokenValidity = ValidityDatePair.Create(config.Login.XboxTokenValidityMinutes);
                var xboxToken = new Tokens.Shared.XboxTicketToken(userToken.Data.UserId, userToken.Data.Username);
                string xboxTokenString = JwtUtils.Sign(xboxToken, config.Login.XboxTokenSecretBytes, xboxTokenValidity);

                string nextSessionKey = config.Login.UserTokenSessionKey;

                var tokenDocument = new XmlDocument();

                var requestSecurityTokenResponseCollection = CreateElement(tokenDocument, "wst", "RequestSecurityTokenResponseCollection");
                {
                    var encryptedData = tokenDocument.CreateElement("EncryptedData");
                    encryptedData.SetAttribute("xmlns", "http://www.w3.org/2001/04/xmlenc#");
                    encryptedData.SetAttribute("Id", "BinaryDAToken0");
                    {
                        var cipherData = tokenDocument.CreateElement("CipherData");
                        {
                            var cipherValue = tokenDocument.CreateElement("CipherValue");
                            cipherValue.InnerText = nextUserTokenString;
                            cipherData.AppendChild(cipherValue);
                        }

                        encryptedData.AppendChild(cipherData);
                    }

                    var binarySecret = CreateElement(tokenDocument, "wst", "BinarySecret");
                    binarySecret.InnerText = nextSessionKey;

                    AddTokenResponse("urn:passport:legacy", "http://Passport.NET/tb",
                         nextUserTokenValidity.IssuedStr, nextUserTokenValidity.ExpiresStr,
                         encryptedData, binarySecret);

                    var binarySecurityToken = CreateElement(tokenDocument, "wsse", "BinarySecurityToken");
                    binarySecurityToken.SetAttribute("Id", "Compact1");
                    binarySecurityToken.InnerText = xboxTokenString;

                    AddTokenResponse("urn:passport:compact", "cobrandid=90023&scope=service%3A%3Auser.auth.xboxlive.com%3A%3Ambi_ssl", xboxTokenValidity.IssuedStr, xboxTokenValidity.ExpiresStr, binarySecurityToken, null);

                    void AddTokenResponse(string tokenType, string address, string issued, string expires, XmlElement securityToken, XmlElement? proofToken)
                    {
                        var requestSecurityTokenResponse = CreateElement(tokenDocument, "wst", "RequestSecurityTokenResponse");
                        {
                            var tokenTypeEle = CreateElement(tokenDocument, "wst", "TokenType");
                            tokenTypeEle.InnerText = tokenType;
                            requestSecurityTokenResponse.AppendChild(tokenTypeEle);

                            var appliesTo = CreateElement(tokenDocument, "wsp", "AppliesTo");
                            {
                                var endpointReference = CreateElement(tokenDocument, "wsa", "EndpointReference");
                                {
                                    var addressEle = CreateElement(tokenDocument, "wsa", "Address");
                                    addressEle.InnerText = address;
                                    endpointReference.AppendChild(addressEle);
                                }

                                appliesTo.AppendChild(endpointReference);
                            }

                            requestSecurityTokenResponse.AppendChild(appliesTo);

                            var lifetime = CreateElement(tokenDocument, "wst", "Lifetime");
                            {
                                var createdEle = CreateElement(tokenDocument, "wsu", "Created");
                                createdEle.InnerText = issued;
                                lifetime.AppendChild(createdEle);

                                var expiresEle = CreateElement(tokenDocument, "wsu", "Expires");
                                expiresEle.InnerText = expires;
                                lifetime.AppendChild(expiresEle);
                            }

                            requestSecurityTokenResponse.AppendChild(lifetime);

                            var requestedSecurityToken = CreateElement(tokenDocument, "wst", "RequestedSecurityToken");
                            requestedSecurityToken.AppendChild(securityToken);

                            requestSecurityTokenResponse.AppendChild(requestedSecurityToken);

                            if (proofToken is not null)
                            {
                                var requestedProofToken = CreateElement(tokenDocument, "wst", "RequestedProofToken");
                                requestedProofToken.AppendChild(proofToken);

                                requestSecurityTokenResponse.AppendChild(requestedProofToken);
                            }
                        }

                        requestSecurityTokenResponseCollection.AppendChild(requestSecurityTokenResponse);
                    }
                }

                tokenDocument.AppendChild(requestSecurityTokenResponseCollection);
                string tokenDocumentString = tokenDocument.OuterXml;

                string tokenDocumentCipherText = DoAESEncryption(config.Login.UserTokenSessionKeyBytes, nonce, tokenDocumentString);

                var response = new XmlDocument();
                var envelope = CreateElement(response, "S", "Envelope");
                {
                    var header = CreateElement(response, "S", "Header");
                    {
                        var security = CreateElement(response, "wsse", "Security");
                        {
                            var timestamp = CreateElement(response, "wsu", "Timestamp");
                            {
                                var created = CreateElement(response, "wsu", "Created");
                                created.InnerText = headerValidity.IssuedStr;
                                timestamp.AppendChild(created);

                                var expires = CreateElement(response, "wsu", "Expires");
                                expires.InnerText = headerValidity.ExpiresStr;
                                timestamp.AppendChild(expires);
                            }

                            security.AppendChild(timestamp);

                            XmlElement derivedKeyToken = response.CreateElement("wssc", "DerivedKeyToken", "http://schemas.xmlsoap.org/ws/2005/02/sc");
                            derivedKeyToken.SetAttribute("xmlns:wssc", "http://schemas.xmlsoap.org/ws/2005/02/sc");
                            derivedKeyToken.SetAttribute("xmlns:ns1", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd");
                            XmlAttribute idAttr = response.CreateAttribute("ns1", "Id", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd");
                            idAttr.Value = "EncKey";
                            derivedKeyToken.Attributes.Append(idAttr);
                            derivedKeyToken.SetAttribute("Algorithm", "urn:liveid:SP800-108CTR-HMAC-SHA256");
                            {
                                XmlElement nonceEle = response.CreateElement("wssc", "Nonce", "http://schemas.xmlsoap.org/ws/2005/02/sc");
                                nonceEle.InnerText = nonce;

                                derivedKeyToken.AppendChild(nonceEle);
                            }

                            security.AppendChild(derivedKeyToken);
                        }

                        header.AppendChild(security);
                    }

                    envelope.AppendChild(header);

                    var body = CreateElement(response, "S", "Body");
                    {
                        var encryptedData = response.CreateElement("EncryptedData");
                        encryptedData.SetAttribute("xmlns", "http://www.w3.org/2001/04/xmlenc#");
                        encryptedData.SetAttribute("Id", "RSTR");
                        encryptedData.SetAttribute("Type", "http://www.w3.org/2001/04/xmlenc#Element");
                        {
                            var encryptionMethod = response.CreateElement("EncryptionMethod");
                            encryptionMethod.SetAttribute("Algorithm", "http://www.w3.org/2001/04/xmlenc#aes256-cbc");
                            encryptedData.AppendChild(encryptionMethod);

                            var keyInfo = response.CreateElement("KeyInfo");
                            keyInfo.SetAttribute("xmlns", "http://www.w3.org/2000/09/xmldsig#");
                            {
                                var securityTokenReference = CreateElement(response, "wsse", "SecurityTokenReference");
                                {
                                    var reference = CreateElement(response, "wsse", "Reference");
                                    reference.SetAttribute("URI", "#EncKey");
                                    securityTokenReference.AppendChild(reference);
                                }

                                keyInfo.AppendChild(securityTokenReference);
                            }

                            encryptedData.AppendChild(keyInfo);

                            var cipherData = response.CreateElement("CipherData");
                            {
                                var cipherValue = response.CreateElement("CipherValue");
                                cipherValue.InnerText = tokenDocumentCipherText;
                                cipherData.AppendChild(cipherValue);
                            }

                            encryptedData.AppendChild(cipherData);
                        }

                        body.AppendChild(encryptedData);
                    }

                    envelope.AppendChild(body);
                }

                response.AppendChild(envelope);

                return TypedResults.Content(response.OuterXml);
            }
        }
        else
        {
            return TypedResults.BadRequest();
        }

        // return TypedResults.Ok();

        XmlElement CreateElement(XmlDocument doc, string prefix, string localName)
        {
            return doc.CreateElement(prefix, localName, nsmgr.LookupNamespace(prefix));
        }

        double EvaluateNumber(XmlDocument document, string xpath, XmlNamespaceManager nsmgr)
        {
            var expr = document.CreateNavigator()!.Compile(xpath);
            expr.SetContext(nsmgr);
            object result = document.CreateNavigator()!.Evaluate(expr);
            if (result is double d)
            {
                return d;
            }

            return 0;
        }
    }

    private static LoginResponse CreateLoginResponse(Account account)
    {
        var tokenValidity = ValidityDatePair.Create(config.Login.UserTokenValidityMinutes);
        var token = new Tokens.Live.UserToken(
            account.Id,
            account.Username,
            Convert.ToBase64String(account.PasswordSalt),
            Convert.ToBase64String(account.PasswordHash)
        );
        string tokenString = JwtUtils.Sign(token, config.Login.UserTokenSecretBytes, tokenValidity);

        return new LoginResponse(
            account.Id,
            account.Username,
            account.FirstName ?? account.Username,
            account.LastName ?? account.Username,
            tokenString,
            tokenValidity.IssuedStr,
            tokenValidity.ExpiresStr,
            config.Login.UserTokenSessionKey
        );
    }

    private static string GenerateNonce()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32);

        var bufferSpan = buffer.AsSpan();
        _rng.GetBytes(bufferSpan);
        string base64 = Convert.ToBase64String(bufferSpan);

        ArrayPool<byte>.Shared.Return(buffer);

        return base64;
    }

    private static string GenerateUserId(string username)
    {
        Span<byte> usernameUTF8 = stackalloc byte[51]; //Encoding.UTF8.GetMaxByteCount(16)
        int usernameUTF8Length = Encoding.UTF8.GetBytes(username, usernameUTF8);
        usernameUTF8 = usernameUTF8[..usernameUTF8Length];

        Span<byte> usernameHash = stackalloc byte[32];
        SHA256.HashData(usernameUTF8, usernameHash);

        return Convert.ToHexStringLower(usernameHash[..8]);
    }

    private static byte[] HashPassword(string password, byte[] salt)
    {
        Debug.Assert(password.Length <= 32);

        byte[] passwordUTF8 = Encoding.UTF8.GetBytes(password);

        return Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(passwordUTF8, salt, 16384, 8, 1, 64);
    }

    private static string DoAESEncryption(byte[] sessionKey, string nonceBase64, string plainText)
    {
        byte[] nonce = Convert.FromBase64String(nonceBase64);
        byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);

        byte[]? messageKey;
        using (var hmac = new HMACSHA256(sessionKey))
        {
            int w1 = hmac.TransformBlock([0, 0, 0, 1], 0, 4, null, 0);
            byte[] labelBytes = Encoding.UTF8.GetBytes("WS-SecureConversationWS-SecureConversation");
            int w2 = hmac.TransformBlock(labelBytes, 0, labelBytes.Length, null, 0);
            int w3 = hmac.TransformBlock([0], 0, 1, null, 0);
            int w4 = hmac.TransformBlock(nonce, 0, nonce.Length, null, 0);
            byte[] w5 = hmac.TransformFinalBlock([0, 0, 1, 0], 0, 4);

            messageKey = hmac.Hash;
        }

        Debug.Assert(messageKey is not null);

        byte[] iv = new byte[16];
        _rng.GetBytes(iv);

        // Encrypt with AES-256-CBC
        byte[] cipherText;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = messageKey;
            aes.IV = iv;

            using (var encryptor = aes.CreateEncryptor(messageKey, iv))
            {
                byte[] cipherData = encryptor.TransformFinalBlock(plainTextBytes, 0, plainTextBytes.Length);
                cipherText = new byte[iv.Length + cipherData.Length];
                iv.AsSpan().CopyTo(cipherText.AsSpan());
                cipherData.AsSpan().CopyTo(cipherText.AsSpan(iv.Length..));
            }
        }

        return Convert.ToBase64String(cipherText);
    }

    [GeneratedRegex("^[a-z0-9_:]+$")]
    private partial Regex GetUsernameRegex();

    [GeneratedRegex("&da=([^&]*)")]
    private partial Regex GetDeviceDATokenStringRegex();
}
