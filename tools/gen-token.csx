#!/usr/bin/env dotnet-script
#r "nuget: System.IdentityModel.Tokens.Jwt, 8.0.0"
#r "nuget: Microsoft.IdentityModel.Tokens, 8.0.0"

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

var userId = Args.Count > 0 ? Args[0] : "dev-user-001";
var role   = Args.Count > 1 ? Args[1] : "Finance";

var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("dev-signing-key-must-be-32-chars!!"));
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

var claims = new List<Claim>
{
    new Claim(JwtRegisteredClaimNames.Sub, userId),
    new Claim(ClaimTypes.NameIdentifier, userId),
    new Claim(ClaimTypes.Role, role)
};

var token = new JwtSecurityToken(
    issuer:            "billing-ledger-dev",
    audience:          "billing-ledger-api-dev",
    claims:            claims,
    expires:           DateTime.UtcNow.AddDays(30), // Válido por 30 dias para facilitar
    signingCredentials: creds);

Console.WriteLine(new JwtSecurityTokenHandler().WriteToken(token));
