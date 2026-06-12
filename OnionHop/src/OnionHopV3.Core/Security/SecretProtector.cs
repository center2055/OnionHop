using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OnionHopV3.Core.Security;

/// <summary>
/// Protects small secrets (e.g. an upstream-proxy password) at rest. On Windows the value is
/// encrypted with DPAPI (CurrentUser scope) so it is never stored in cleartext. On other platforms
/// DPAPI is unavailable, so the value is stored unchanged - no regression versus the prior behaviour;
/// a Keychain/libsecret backend can be added later. Stored values are tagged so <see cref="Unprotect"/>
/// can tell an encrypted blob from a legacy/plaintext value and migrate transparently.
/// </summary>
public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:v1:";

    /// <summary>Encrypts a plaintext secret for storage. Returns null/empty unchanged.</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return DpapiPrefix + ProtectWindows(plaintext);
            }
            catch
            {
                // If DPAPI fails for any reason, fall back to storing the value as-is rather than
                // losing the user's setting. Worst case matches the previous (plaintext) behaviour.
                return plaintext;
            }
        }

        return plaintext;
    }

    /// <summary>Decrypts a stored secret back to plaintext. Legacy plaintext values pass through.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                // A DPAPI blob created on Windows can't be decrypted here; the secret is unusable on
                // this platform. Return empty rather than handing back the ciphertext.
                return string.Empty;
            }

            try
            {
                return UnprotectWindows(stored.Substring(DpapiPrefix.Length));
            }
            catch
            {
                return string.Empty;
            }
        }

        // Legacy plaintext (or a plaintext value written on a non-Windows OS) - return unchanged.
        return stored;
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindows(string base64)
    {
        var protectedBytes = Convert.FromBase64String(base64);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
