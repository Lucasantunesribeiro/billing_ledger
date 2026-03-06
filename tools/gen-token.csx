#!/usr/bin/env dotnet-script
#r "nuget: System.IdentityModel.Tokens.Jwt, 8.0.0"
#r "nuget: Microsoft.IdentityModel.Tokens, 8.0.0"

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

var userId = args.Length > 0 ? args[0] : "dev-user-001";
var role   = args.Length > 1 ? args[1] : "Finance";

var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("dev-signing-key-must-be-32-chars!!"));
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

var token = new JwtSecurityToken(
    issuer:            "billing-ledger-dev",
    audience:          "billing-ledger-api-dev",
    claims:            [
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim(ClaimTypes.Role, role),
    ],
    expires:           DateTime.UtcNow.AddDays(30), // Válido por 30 dias para facilitar
    signingCredentials: creds);

Console.WriteLine(new JwtSecurityTokenHandler().WriteToken(token));
