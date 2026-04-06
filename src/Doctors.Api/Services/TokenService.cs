using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Doctors.Api.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Doctors.Api.Services;

public class TokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public TokenResponse? GenerateToken(LoginRequest request)
    {
        // Demo credentials — in production this would validate against a user store
        if (request.Username != "admin" || request.Password != "admin123")
        {
            return null;
        }

        var signingKey = _configuration["JwtSettings:SigningKey"]
            ?? throw new InvalidOperationException("JwtSettings:SigningKey is not configured");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiryMinutes = _configuration.GetValue<int>("JwtSettings:ExpiryMinutes", 60);
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var token = new JwtSecurityToken(
            issuer: _configuration["JwtSettings:Issuer"],
            audience: _configuration["JwtSettings:Audience"],
            expires: expiresAt,
            signingCredentials: credentials);

        return new TokenResponse(
            Token: new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt: expiresAt);
    }
}