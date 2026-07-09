using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MobileData.Core;

namespace MobileData.Collector;

internal sealed record Credentials(string Username, string Password);

/// <summary>
/// Stores carrier login credentials encrypted with Windows DPAPI (per-user).
/// The ciphertext can only be decrypted by the same Windows user account, and
/// lives under the gitignored %USERPROFILE%\.mobile-widget\ folder.
/// </summary>
internal static class CredentialStore
{
    // Extra entropy mixed into DPAPI so the blob isn't decryptable by other apps
    // that might guess the scope. Not a secret, just app-specific salt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MobileData.Collector.v1");

    private static string PathFor(string providerId) =>
        System.IO.Path.Combine(UsageCache.Directory, $"{providerId}.cred");

    public static bool Exists(string providerId) => File.Exists(PathFor(providerId));

    public static void Save(string providerId, Credentials credentials)
    {
        Directory.CreateDirectory(UsageCache.Directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var cipher = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(providerId), cipher);
    }

    public static Credentials? Load(string providerId)
    {
        try
        {
            var path = PathFor(providerId);
            if (!File.Exists(path)) return null;
            var cipher = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Credentials>(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
