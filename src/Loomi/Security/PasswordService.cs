using Loomi.Models;
using Microsoft.AspNetCore.Identity;
namespace Loomi.Security;
/// <summary>Hashes and checks sign-in passwords. Salting and the fixed-time comparison are the framework hasher's.</summary>
public class PasswordService
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly AppUser Nobody = new();
    /// <summary>A hash of a value nobody can supply. Checking against it costs the same as a real miss, so a wrong name and a wrong password are indistinguishable.</summary>
    private static readonly string NoPassword = Hasher.HashPassword(Nobody, Guid.NewGuid().ToString());
    public string Hash(AppUser user, string password) => Hasher.HashPassword(user, password);
    public bool Verify(AppUser? user, string password) => Hasher.VerifyHashedPassword(user ?? Nobody, user?.PasswordHash ?? NoPassword, password) != PasswordVerificationResult.Failed && user is not null;
}
