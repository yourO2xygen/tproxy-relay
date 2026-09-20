using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TproxyRelay;

/// <summary>
/// A runtime profile: one client-facing secret bound to one MTProxy backend.
/// The built-in profile comes from the environment (single-secret mode); every
/// managed key contributes one more profile. Matching a bridge capability
/// selects the profile for the whole session, including its backend address.
/// </summary>
public sealed record RelayProfile(
    string KeyId,
    string Name,
    byte[] Secret,
    string BackendHostName,
    int BackendPort,
    string CarrierMode)
{
    public byte[] Capability(RelayOptions opt) =>
        Base64Url.DecodeFromChars(CapabilityDeriver.Derive(opt.PublicHostname, Secret, opt.BasePath));

    public string Backend => $"{BackendHostName}:{BackendPort}";
}

/// <summary>Thread-safe profile lookup by capability bytes.</summary>
public sealed class ProfileRegistry
{
    private readonly RelayOptions _opt;
    private readonly object _sync = new();
    private List<RelayProfile> _profiles = [];

    public ProfileRegistry(RelayOptions opt, RelayProfile builtin)
    {
        _opt = opt;
        _profiles = [builtin];
    }

    public RelayProfile Builtin => _profiles[0];

    public void ReplaceManaged(IEnumerable<RelayProfile> managed)
    {
        lock (_sync)
            _profiles = [_profiles[0], .. managed];
    }

    public IReadOnlyList<RelayProfile> All
    {
        get { lock (_sync) return _profiles.ToArray(); }
    }

    /// <summary>Constant-time-ish match over all profiles; a wrong candidate probes nothing.</summary>
    public RelayProfile? Match(string candidate)
    {
        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(candidate);
        }
        catch
        {
            return null;
        }
        RelayProfile? hit = null;
        lock (_sync)
        {
            foreach (var p in _profiles)
            {
                var expected = p.Capability(_opt);
                if (decoded.Length == expected.Length && CryptographicOperations.FixedTimeEquals(decoded, expected))
                    hit = p; // keep scanning: length-equal comparisons cost nothing extra
            }
        }
        return hit;
    }
}
