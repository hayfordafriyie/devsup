using Microsoft.AspNetCore.Identity;
using DevSup.Core.Models;

namespace DevSup.Infrastructure.Security;

public interface IPasswordHasherService
{
    string Hash(string password);

    bool Verify(string password, string storedHash);
}

/// <summary>
/// Wraps ASP.NET Core's PBKDF2 password hasher (the same scheme Identity uses).
/// The stored value is a self-describing hash — never the plain-text password.
/// </summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private static readonly PasswordHasher<User> Hasher = new();

    private static readonly User Placeholder = new()
    {
        Id = Guid.Empty,
        Email = string.Empty,
        DisplayName = string.Empty,
        PasswordHash = string.Empty
    };

    public string Hash(string password)
        => Hasher.HashPassword(Placeholder, password);

    public bool Verify(string password, string storedHash)
        => Hasher.VerifyHashedPassword(Placeholder, storedHash, password)
           != PasswordVerificationResult.Failed;
}