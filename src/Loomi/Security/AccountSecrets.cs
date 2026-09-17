using Microsoft.AspNetCore.DataProtection;
namespace Loomi.Security;
/// <summary>Seals an API key for storage. Its own purpose string, so a payload lifted out of the accounts table cannot be unsealed by anything else here,
/// and a copy of the database without the key ring on disk is not a copy of the key.</summary>
public class AccountSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Loomi.ProviderAccount.Secret.v1");
    public string Protect(string secret) => protector.Protect(secret);
    /// <summary>Null when there is nothing stored or the payload cannot be unsealed — a key ring rotated away leaves an account that has to be given its key again, not an exception on every request.</summary>
    public string? Reveal(string? sealedSecret)
    {
        if (string.IsNullOrEmpty(sealedSecret)) return null;
        try { return protector.Unprotect(sealedSecret); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
    /// <summary>All anyone is ever shown: enough to tell two keys apart, not enough to be one.</summary>
    public static string? Hint(string? key) => key is null ? null : "••••" + (key.Length <= 4 ? key : key[^4..]);
}
