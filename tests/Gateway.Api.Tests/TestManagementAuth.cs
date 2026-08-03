using System.Text;
using Gateway.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Api.Tests;

/// <summary>
/// Test-only Cognito-token machinery. The gateway validates management tokens
/// against a JWKS discovered from <c>GATEWAY_COGNITO_AUTHORITY</c>; tests must not
/// reach the network, so this issues locally-signed access tokens with a fixed
/// symmetric key and overrides the JwtBearer <see cref="TokenValidationParameters"/>
/// to trust that same key (tech-spec §5, "no network JWKS fetch in tests").
/// <para>
/// Groups and scopes are configurable so a test can mint an <c>ops-admin</c>,
/// <c>ops-deploy</c>, CI (<c>mgmt/deploy</c> scope), or role-less token at will.
/// </para>
/// </summary>
public static class TestManagementAuth
{
    /// <summary>Issuer stamped into test tokens and used as the configured authority.</summary>
    public const string Issuer = "https://test.cognito.local/pool";

    // 32+ bytes so HMAC-SHA256 accepts the key. Fixed → deterministic tests.
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("gateway-api-test-signing-key-0123456789"));

    /// <summary>Validation parameters trusting the local <see cref="SigningKey"/> only.</summary>
    public static TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey,
    };

    /// <summary>
    /// Point a live JwtBearer scheme at the local signing key with no metadata
    /// fetch. Nulling <c>Authority</c>/<c>MetadataAddress</c>/<c>ConfigurationManager</c>
    /// stops the framework post-configure from rebuilding a JWKS-backed
    /// configuration, so validation uses <see cref="ValidationParameters"/> directly.
    /// Applied as a <c>PostConfigure</c> so it wins over the app's own setup.
    /// </summary>
    public static void ApplyTestSigningKey(JwtBearerOptions options)
    {
        options.Authority = null;
        options.MetadataAddress = string.Empty;
        options.Configuration = null;
        options.ConfigurationManager = null;
        options.RequireHttpsMetadata = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = ValidationParameters;
    }

    /// <summary>
    /// Mint a signed access token. Defaults to a valid, non-expired
    /// <c>token_use=access</c> token; pass <paramref name="groups"/> /
    /// <paramref name="scopes"/> to grant roles, or override
    /// <paramref name="tokenUse"/> / <paramref name="expires"/> to exercise the
    /// validation failure paths.
    /// </summary>
    public static string CreateToken(
        IEnumerable<string>? groups = null,
        IEnumerable<string>? scopes = null,
        string tokenUse = ManagementAuth.AccessTokenUse,
        DateTime? expires = null,
        string username = "test-ops-user")
    {
        var claims = new Dictionary<string, object>
        {
            [ManagementAuth.TokenUseClaim] = tokenUse,
            ["username"] = username,
            ["client_id"] = "test-client",
        };

        var groupList = groups?.ToArray() ?? Array.Empty<string>();
        if (groupList.Length > 0)
        {
            // A JSON array claim deserializes back to one Claim per element —
            // exactly the shape Cognito's cognito:groups produces.
            claims[ManagementAuth.GroupsClaim] = groupList;
        }

        var scopeList = scopes?.ToArray() ?? Array.Empty<string>();
        if (scopeList.Length > 0)
        {
            claims[ManagementAuth.ScopeClaim] = string.Join(' ', scopeList);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(60),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
