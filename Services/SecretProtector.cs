using System.Security.Cryptography;
using System.Text;

namespace RobloxMultiLauncher.Services;

/// <summary>
/// Encrypts secrets with Windows DPAPI (current-user scope). The blob can only be decrypted by the same
/// Windows user on the same machine. Note: other processes running as the same user can also call DPAPI.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxMultiLauncher.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Logger.Warn("A stored credential could not be decrypted (different Windows user or machine?).");
            return null;
        }
    }
}
