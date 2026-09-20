using Xunit;
using Microsoft.AspNetCore.Http;
using TproxyRelay;

namespace TproxyRelay.Tests;

/// <summary>Base path capability vectors from BASE_PATH.md.</summary>
public class BasePathCapabilityTests
{
    private static readonly byte[] Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] DdSecret = Convert.FromHexString("dd000102030405060708090a0b0c0d0e0f");

    [Fact]
    public void Root_Derivation_Is_Frozen_V1()
    {
        Assert.Equal("MHLEY5PmW1GWqJkSrlmJpvJUiLhBH_QKy6yKg8a0JPk",
            CapabilityDeriver.Derive("proxy.example.com", Secret));
        Assert.Equal("IpJrt3e7sKtzPyoXy6w-Zj6GGEvsvclN66JzQEfPYLA",
            CapabilityDeriver.Derive("proxy.example.com", DdSecret));
    }

    [Fact]
    public void Prefix_Derivation_Matches_Upstream_Vectors()
    {
        Assert.Equal("hHz99Xs93EN1j91G9gpNepXwGNNt5YdAFkEVk_LlqdQ",
            CapabilityDeriver.Derive("proxy.example.com", Secret, "dobry-cola-super-app"));
        Assert.Equal("TGUkZaevsavLbHvlNWipnRoYxgzZ51ioWvbxgGT3wHo",
            CapabilityDeriver.Derive("proxy.example.com", DdSecret, "dobry-cola-super-app"));
    }

    [Fact]
    public void Root_And_Prefix_Capabilities_Differ()
    {
        var root = CapabilityDeriver.Derive("proxy.example.com", Secret);
        var prefixed = CapabilityDeriver.Derive("proxy.example.com", Secret, "slug");
        Assert.NotEqual(root, prefixed);
    }
}

public class BasePathValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("slug")]
    [InlineData("a-b_C9")]
    [InlineData("AbC")]          // case-sensitive alphabet, never folded
    [InlineData("two/segments")]
    [InlineData("a/b/c/d/e")]
    public void Valid_Paths(string path) => Assert.True(BasePaths.IsValid(path));

    [Theory]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("a//b")]          // empty segment
    [InlineData("-abc")]          // first char must be alnum
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a%2Fb")]         // escapes rejected, not repaired
    [InlineData("a b")]
    [InlineData("до$та")]         // non-ASCII rejected
    public void Invalid_Paths(string path) => Assert.False(BasePaths.IsValid(path));

    [Fact]
    public void Total_Length_Limited_To_128()
    {
        Assert.True(BasePaths.IsValid(new string('a', 128)));
        Assert.False(BasePaths.IsValid(new string('a', 129)));
    }

    [Fact]
    public void Generated_Slug_Is_Sixteen_Base32_Chars()
    {
        for (var i = 0; i < 20; i++)
        {
            var slug = BasePaths.Generate();
            Assert.Equal(16, slug.Length);
            Assert.Matches("^[a-z2-7]+$", slug);
            Assert.True(BasePaths.IsValid(slug));
        }
        Assert.NotEqual(BasePaths.Generate(), BasePaths.Generate());
    }

    [Fact]
    public void WebPath_Forms()
    {
        Assert.Equal("/", BasePaths.WebPath(""));
        Assert.Equal("/slug/", BasePaths.WebPath("slug"));
        Assert.Equal("/a/b/", BasePaths.WebPath("a/b"));
    }
}

public class ProxyLinkTests
{
    [Fact]
    public void Marked_Secret_Matches_Upstream_Vector()
    {
        var secret = Convert.FromHexString("8561944064fc730cbfa4473562d8ec59");
        Assert.Equal("cIVhlEBk_HMMv6RHNWLY7Fk", ProxyLinks.MarkedSecret(secret));
    }

    [Fact]
    public void Root_Link_Uses_Plain_Hex_Secret()
    {
        var secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        Assert.Equal("000102030405060708090a0b0c0d0e0f", ProxyLinks.SecretForLink(secret, ""));
        Assert.Contains("secret=000102030405060708090a0b0c0d0e0f",
            ProxyLinks.Tme("proxy.example.com", "", secret));
        Assert.Contains("server=proxy.example.com&", ProxyLinks.Tme("proxy.example.com", "", secret));
    }

    [Fact]
    public void BasePath_Link_Encodes_Server_And_Marks_Secret()
    {
        var secret = Convert.FromHexString("8561944064fc730cbfa4473562d8ec59");
        var link = ProxyLinks.Tme("example.com", "phcf2vfe7zgbrslg", secret);
        Assert.Equal(
            "https://t.me/webproxy?server=example.com%2Fphcf2vfe7zgbrslg&secret=cIVhlEBk_HMMv6RHNWLY7Fk",
            link);
        Assert.Contains("server=example.com%2Fphcf2vfe7zgbrslg",
            ProxyLinks.Tg("example.com", "phcf2vfe7zgbrslg", secret));
    }
}

public class BasePathConfigTests : IDisposable
{
    [Fact]
    public void Env_BasePath_Sets_V2_Capability()
    {
        var opt = RelayOptions.Load(k => k switch
        {
            "TPROXY_SECRET_HEX" => "000102030405060708090a0b0c0d0e0f",
            "TPROXY_BASE_PATH" => "dobry-cola-super-app",
            _ => null,
        });
        Assert.Equal("dobry-cola-super-app", opt.BasePath);
        Assert.Equal("hHz99Xs93EN1j91G9gpNepXwGNNt5YdAFkEVk_LlqdQ",
            System.Buffers.Text.Base64Url.EncodeToString(opt.CapabilityBytes));
    }

    [Fact]
    public void Invalid_BasePath_Refuses_Startup()
    {
        Assert.Throws<InvalidOperationException>(() => RelayOptions.Load(k => k switch
        {
            "TPROXY_SECRET_HEX" => "000102030405060708090a0b0c0d0e0f",
            "TPROXY_BASE_PATH" => "a//b",
            _ => null,
        }));
    }

    [Fact]
    public void Bridge_Page_Prefixes_Carrier_Urls()
    {
        var html = BridgePageWrite("dobry-cola");
        Assert.Contains("var BASE='/dobry-cola/';", html);
        Assert.Contains("post(BASE+'api/v1/session'", html);
        Assert.Contains("location.host+BASE+'api/v1/ws'", html);
        Assert.DoesNotContain("history.replaceState(null,'','/')", html);
    }

    [Fact]
    public void Bridge_Page_Root_Keeps_Unprefixed_Urls()
    {
        var html = BridgePageWrite("");
        Assert.Contains("var BASE='/';", html);
    }

    private static string BridgePageWrite(string basePath)
    {
        using var stream = new MemoryStream();
        var ctx = new DefaultHttpContext { Response = { Body = stream } };
        BridgePage.Write(ctx, "bootstrap-token-value", "https", "proxy.example.com", basePath).Wait();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose() { }
}
