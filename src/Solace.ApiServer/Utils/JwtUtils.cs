using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Solace.ApiServer.Models;
using Solace.Common;

namespace Solace.ApiServer.Utils;

internal static class JwtUtils
{
    private static readonly JwtSecurityTokenHandler jwtHandler = new JwtSecurityTokenHandler();

    public static string Sign<TData>(Token<TData> token, byte[] secret)
        where TData : ITokenData<TData>
        => SignInternal<TData>(token, secret, new ValidityDatePair(token.Issued, token.Expires));

    public static string Sign<TData>(TData data, byte[] secret, ValidityDatePair validity)
        where TData : ITokenData<TData>
        => SignInternal<TData>(data, secret, validity);

    public static string SignXboxUserToken(Tokens.Xbox.UserToken user, byte[] secret, ValidityDatePair validity)
    {
        var xui = new[]
        {
            new Dictionary<string, object?>()
            {
                ["xid"] = user.Xid,
                ["uhs"] = user.Uhs,
                ["gtg"] = user.Username,
                ["agg"] = "Adult",
                ["usr"] = "185 190 234",
                ["prv"] = "184 186 187 188 191 193 195 196 198 199 200 201 203 204 205 206 208 211 217 220 224 227 228 235 238 245 247 249 252 254 255",
            },
        };

        string headerJson = Json.Serialize(new { alg = "HS256", typ = "JWT" });
        string payloadJson = Json.Serialize(new Dictionary<string, object?>()
        {
            ["iat"] = validity.Issued.ToUnixTimeSeconds(),
            ["nbf"] = validity.Issued.ToUnixTimeSeconds(),
            ["exp"] = validity.Expires.ToUnixTimeSeconds(),
            ["data"] = Json.Serialize<Tokens.Xbox.AuthToken>(user),
            ["xid"] = user.Xid,
            ["uhs"] = user.Uhs,
            ["gtg"] = user.Username,
            ["agg"] = "Adult",
            ["usr"] = "185 190 234",
            ["prv"] = "184 186 187 188 191 193 195 196 198 199 200 201 203 204 205 206 208 211 217 220 224 227 228 235 238 245 247 249 252 254 255",
            ["xui"] = xui,
        });

        byte[] headerBytes = Encoding.UTF8.GetBytes(headerJson);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        string headerAndPayload = WebEncoders.Base64UrlEncode(headerBytes) + "." + WebEncoders.Base64UrlEncode(payloadBytes);

        using var hmac = new HMACSHA256(secret);
        string signature = WebEncoders.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(headerAndPayload)));

        return headerAndPayload + "." + signature;
    }

    private static string SignInternal<TData>(object dataOrToken, byte[] secret, ValidityDatePair validity)
        where TData : ITokenData<TData>
    {
        TData data = dataOrToken switch
        {
            Token<TData> token => token.Data,
            TData tokenData => tokenData,
            _ => throw new UnreachableException(),
        };

        Claim[] payload =
        [
            new Claim("iat", validity.Issued.ToUnixTimeSeconds().ToString()),
            new Claim("nbf", validity.Issued.ToUnixTimeSeconds().ToString()),
            new Claim("exp", validity.Expires.ToUnixTimeSeconds().ToString()),
            new Claim("data", Json.Serialize(data)),
        ];

        return jwtHandler.WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(
                new SymmetricSecurityKey(secret),
                SecurityAlgorithms.HmacSha256)),
            new JwtPayload(payload)
        ));
    }

    public static Token<TData>? Verify<TData>(string token, byte[] secret, bool allowExpired = false)
        where TData : ITokenData<TData>
    {
        try
        {
            var claims = jwtHandler.ValidateToken(token, new TokenValidationParameters()
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = !allowExpired,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(secret),
            }, out _).Claims.ToDictionary(claim => claim.Type, claim => claim.Value);

            if (!claims.TryGetValue("iat", out string? iat) || !claims.TryGetValue("exp", out string? exp) || !claims.TryGetValue("data", out string? dataJson))
            {
                return null;
            }

            if (!long.TryParse(iat, out long issuedSeconds) || !long.TryParse(exp, out long expiresSeconds))
            {
                return null;
            }

            var expires = DateTimeOffset.FromUnixTimeSeconds(expiresSeconds);

            var data = Json.Deserialize<TData>(dataJson);
            if (data is null)
            {
                return null;
            }

            return new Token<TData>(DateTimeOffset.FromUnixTimeSeconds(issuedSeconds), expires, allowExpired && expires < DateTimeOffset.UtcNow, data);
        }
        catch (SecurityTokenException ex)
        {
            Log.Debug($"JWT verification failed: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            Log.Debug($"JWT data deserialization failed: {ex.Message}");
            return null;
        }
    }
}
