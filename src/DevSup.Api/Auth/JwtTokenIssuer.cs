using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DevSup.Core.Models;
using Microsoft.IdentityModel.Tokens;

namespace DevSup.Api.Auth;

public sealed class JwtSettings
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SecretKey { get; init; }
    public int ExpiryMinutes { get; init; } = 60;
}

public sealed class JwtTokenIssuer(JwtSettings settings)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(settings.ExpiryMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}