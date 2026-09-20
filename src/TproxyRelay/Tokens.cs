using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TproxyRelay;

/// <summary>
/// Derivation of the bridge capability:
/// context = UTF-8("tdesktop-web-proxy-bridge-v1\n" + hostname)                 (root)
///         = UTF-8("tdesktop-web-proxy-bridge-v2\n" + hostname + "\n" + path)   (base path)
/// capability = base64url-no-padding(HMAC-SHA256(key=secret, message=context))
/// </summary>
public static class CapabilityDeriver
{
    public static string Derive(string hostname, byte[] secret) =>
        Derive(hostname, secret, "");

    public static string Derive(string hostname, byte[] secret, string basePath)
    {
        var context = basePath.Length == 0
            ? Encoding.UTF8.GetBytes("tdesktop-web-proxy-bridge-v1\n" + hostname)
            : Encoding.UTF8.GetBytes("tdesktop-web-proxy-bridge-v2\n" + hostname + "\n" + basePath);
        var mac = HMACSHA256.HashData(secret, context);
        return Base64Url.EncodeToString(mac);
    }

    /// <summary>Constant-time comparison of a 43-char capability against the configured one.</summary>
    public static bool Matches(string candidate, byte[] expected)
    {
        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(candidate);
        }
        catch
        {
            return false;
        }
        if (decoded.Length != expected.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(decoded, expected);
    }
}

/// <summary>
/// Opaque bearer tokens: 16 random bytes || 16-byte truncated HMAC-SHA256.
/// MAC input: "tproxy-server-token-v1\0" || kind || nonce.
/// </summary>
public sealed class TokenMinter(byte[] key)
{
    public const byte KindBootstrap = 1;
    public const byte KindSession = 2;

    private static readonly byte[] DomainPrefix = "tproxy-server-token-v1\0"u8.ToArray();

    public string Mint(byte kind)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        return Base64Url.EncodeToString(Compose(nonce, kind));
    }

    public bool TryValidate(string token, byte kind)
    {
        if (token.Length != 43)
            return false;
        byte[] raw;
        try
        {
            raw = Base64Url.DecodeFromChars(token);
        }
        catch
        {
            return false;
        }
        if (raw.Length != 32)
            return false;
        var expected = Compose(raw.AsSpan(0, 16).ToArray(), kind);
        return CryptographicOperations.FixedTimeEquals(raw, expected);
    }

    private byte[] Compose(byte[] nonce, byte kind)
    {
        var macInput = new byte[DomainPrefix.Length + 1 + nonce.Length];
        DomainPrefix.CopyTo(macInput, 0);
        macInput[DomainPrefix.Length] = kind;
        nonce.CopyTo(macInput, DomainPrefix.Length + 1);
        var mac = HMACSHA256.HashData(key, macInput);
        var result = new byte[32];
        nonce.CopyTo(result, 0);
        mac.AsSpan(0, 16).CopyTo(result.AsSpan(16));
        return result;
    }

    public static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length >= 32)
                return existing[..32];
            throw new InvalidOperationException($"token key file {path} is too short");
        }
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        return key;
    }
}
