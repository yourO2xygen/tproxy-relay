using Xunit;
using TproxyRelay;

namespace TproxyRelay.Tests;

public class TokensTests
{
    private const string TestSecretHex = "000102030405060708090a0b0c0d0e0f";

    [Fact]
    public void Capability_Vectors_FromProtocol()
    {
        var secret = Convert.FromHexString(TestSecretHex);
        Assert.Equal(
            "MHLEY5PmW1GWqJkSrlmJpvJUiLhBH_QKy6yKg8a0JPk",
            CapabilityDeriver.Derive("proxy.example.com", secret));
    }

    [Fact]
    public void Capability_Matches_DecodesAndCompares()
    {
        var secret = Convert.FromHexString(TestSecretHex);
        var cap = CapabilityDeriver.Derive("proxy.example.com", secret);
        var capBytes = System.Buffers.Text.Base64Url.DecodeFromChars(cap);

        Assert.True(CapabilityDeriver.Matches(cap, capBytes));
        Assert.False(CapabilityDeriver.Matches("AAAA", capBytes));
        Assert.False(CapabilityDeriver.Matches("not-base64!", capBytes));
        var otherHost = CapabilityDeriver.Derive("other.example.com", secret);
        Assert.False(CapabilityDeriver.Matches(otherHost, capBytes));
    }

    [Fact]
    public void TokenMinter_MintValidate_RoundtripPerKind()
    {
        var key = new byte[32];
        var minter = new TokenMinter(key);
        var bootstrap = minter.Mint(TokenMinter.KindBootstrap);
        var session = minter.Mint(TokenMinter.KindSession);

        Assert.Equal(43, bootstrap.Length);
        Assert.True(minter.TryValidate(bootstrap, TokenMinter.KindBootstrap));
        Assert.True(minter.TryValidate(session, TokenMinter.KindSession));
        Assert.False(minter.TryValidate(bootstrap, TokenMinter.KindSession));
        Assert.False(minter.TryValidate(session, TokenMinter.KindBootstrap));
    }

    [Fact]
    public void TokenMinter_RejectsTamperedAndMalformed()
    {
        var minter = new TokenMinter(new byte[32]);
        var token = minter.Mint(TokenMinter.KindSession);
        var raw = Convert.FromBase64String(
            token.Replace('-', '+').Replace('_', '/').PadRight(44, '='));
        raw[10] ^= 0xFF;
        var tampered = Convert.ToBase64String(raw)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(minter.TryValidate(tampered, TokenMinter.KindSession));
        Assert.False(minter.TryValidate("short", TokenMinter.KindSession));
        Assert.False(minter.TryValidate(new string('A', 43), TokenMinter.KindSession));
    }

    [Fact]
    public void TokenMinter_KeyIsStableAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tproxy-test-key-{Guid.NewGuid():N}");
        try
        {
            var key1 = TokenMinter.LoadOrCreateKey(path);
            var key2 = TokenMinter.LoadOrCreateKey(path);
            Assert.Equal(32, key1.Length);
            Assert.Equal(key1, key2);

            var token = new TokenMinter(key1).Mint(TokenMinter.KindSession);
            Assert.True(new TokenMinter(key2).TryValidate(token, TokenMinter.KindSession));
        }
        finally { File.Delete(path); }
    }
}
