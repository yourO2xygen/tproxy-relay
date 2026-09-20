using System.Security.Cryptography;
using System.Text;

namespace TproxyRelay;

/// <summary>
/// Base path support (BASE_PATH.md): the relay surface can live under a path
/// prefix so one hostname can run an ordinary website alongside the relay.
/// </summary>
public static class BasePaths
{
    /// <summary>
    /// One or more '/'-separated segments, each [A-Za-z0-9][A-Za-z0-9_-]*,
    /// at most 128 characters in total, stored without leading/trailing slash.
    /// Case-sensitive; %xx escapes and empty segments are rejected.
    /// </summary>
    public static bool IsValid(string path)
    {
        if (path.Length == 0)
            return true; // root deployment
        if (path.Length > 128 || path.StartsWith('/') || path.EndsWith('/'))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
                return false; // empty segment (a//b)
            var first = segment[0];
            if (!(char.IsAsciiLetterOrDigit(first)))
                return false;
            foreach (var c in segment)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    return false;
        }
        return true;
    }

    /// <summary>The URL form served: "/" at the root, "/<path>/" with a prefix.</summary>
    public static string WebPath(string path) =>
        path.Length == 0 ? "/" : "/" + path + "/";

    /// <summary>
    /// Default slug: lowercase RFC 4648 base32 of 10 random bytes —
    /// 16 characters, 80 bits, every character valid as a first character.
    /// Ten bytes are exactly two base32 groups, so no padding is involved.
    /// </summary>
    public static string Generate()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var bytes = RandomNumberGenerator.GetBytes(10);
        var sb = new StringBuilder(16);
        for (var group = 0; group < 2; group++)
        {
            var b0 = bytes[group * 5];
            var b1 = bytes[group * 5 + 1];
            var b2 = bytes[group * 5 + 2];
            var b3 = bytes[group * 5 + 3];
            var b4 = bytes[group * 5 + 4];
            sb.Append(alphabet[b0 >> 3]);
            sb.Append(alphabet[(b0 & 7) << 2 | b1 >> 6]);
            sb.Append(alphabet[b1 >> 1 & 31]);
            sb.Append(alphabet[(b1 & 1) << 4 | b2 >> 4]);
            sb.Append(alphabet[(b2 & 15) << 1 | b3 >> 7]);
            sb.Append(alphabet[b3 >> 2 & 31]);
            sb.Append(alphabet[(b3 & 3) << 3 | b4 >> 5]);
            sb.Append(alphabet[b4 & 31]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Client-facing proxy values: the secret as typed, and the share link.
/// With a base path the address carries the prefix in the server parameter
/// and the secret changes to the marked base64url form (0x70 prefix).
/// </summary>
public static class ProxyLinks
{
    /// <summary>base64url(0x70 || secret) — the marked form for base-path links.</summary>
    public static string MarkedSecret(byte[] secret)
    {
        var marked = new byte[1 + secret.Length];
        marked[0] = 0x70;
        secret.CopyTo(marked.AsSpan(1));
        return System.Buffers.Text.Base64Url.EncodeToString(marked);
    }

    public static string SecretForLink(byte[] secret, string basePath) =>
        basePath.Length == 0 ? Convert.ToHexString(secret).ToLowerInvariant() : MarkedSecret(secret);

    public static string ServerParam(string hostname, string basePath) =>
        basePath.Length == 0 ? hostname : hostname + "%2F" + basePath;

    public static string Tme(string hostname, string basePath, byte[] secret) =>
        $"https://t.me/webproxy?server={ServerParam(hostname, basePath)}&secret={SecretForLink(secret, basePath)}";

    public static string Tg(string hostname, string basePath, byte[] secret) =>
        $"tg://webproxy?server={ServerParam(hostname, basePath)}&secret={SecretForLink(secret, basePath)}";
}
