using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace BillingLedger.IntegrationTests.Infrastructure;

/// <summary>
/// Generates signed JWT tokens for use in integration tests.
/// Signing key, issuer, and audience match what BillingApiFactory injects
/// into the test host configuration.
/// </summary>
public static class JwtTestHelper
{
    public const string Issuer = "billing-ledger-test";
    public const string Audience = "billing-ledger-api-test";
    public const string SigningKey = "billing-ledger-test-signing-key-32!!";

    public static string GenerateToken(string userId, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, role),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
