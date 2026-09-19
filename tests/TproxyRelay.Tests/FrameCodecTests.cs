using Xunit;
using TproxyRelay;

namespace TproxyRelay.Tests;

public class FrameCodecTests
{
    [Fact]
    public void EncodeParseAll_Roundtrip()
    {
        var open = FrameCodec.Encode(FrameType.Open, 7);
        var data = FrameCodec.Encode(FrameType.Data, 7, [1, 2, 3, 4, 5]);
        var win = FrameCodec.EncodeWindow(7, 12345);
        var body = open.Concat(data).Concat(win).ToArray();

        var frames = FrameCodec.ParseAll(body);

        Assert.Equal(3, frames.Count);
        Assert.Equal(FrameType.Open, frames[0].Type);
        Assert.Equal(7u, frames[0].StreamId);
        Assert.Equal(FrameType.Data, frames[1].Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, frames[1].Payload.ToArray());
        Assert.Equal(FrameType.Window, frames[2].Type);
        Assert.Equal(12345u, BitConverter.ToUInt32(
            BitConverter.IsLittleEndian
                ? frames[2].Payload.ToArray().Reverse().ToArray()
                : frames[2].Payload.ToArray()));
    }

    [Fact]
    public void ParseAll_IsZeroCopy_SlicesInputBody()
    {
        var body = FrameCodec.Encode(FrameType.Data, 1, new byte[64]);
        var frames = FrameCodec.ParseAll(body);
        // payload memory must reference the same buffer, not a copy
        Assert.True(frames[0].Payload.Span.Overlaps(body.AsSpan(FrameCodec.HeaderSize)));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void ParseAll_RejectsTruncatedHeader(byte[] bad) =>
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(bad));

    [Fact]
    public void ParseAll_RejectsTruncatedBody()
    {
        var frame = FrameCodec.Encode(FrameType.Data, 1, new byte[16]);
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(frame[..^1]));
    }

    [Fact]
    public void ParseAll_RejectsOversizeLength()
    {
        var bad = new byte[8];
        bad[0] = (byte)FrameType.Data;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(4), FrameCodec.MaxPayload + 1);
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(bad));
    }

    [Fact]
    public void ParseAll_RejectsEmpty()
    {
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(new byte[0]));
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(Array.Empty<byte>()));
    }

    [Fact]
    public void FrameBuf_RentData_WritesValidFrameAndReturns()
    {
        var payload = new byte[100];
        Random.Shared.NextBytes(payload);
        var fb = FrameBuf.RentData(42, payload);
        try
        {
            Assert.True(fb.Pooled);
            Assert.Equal(FrameCodec.HeaderSize + 100, fb.Length);
            var parsed = FrameCodec.ParseAll(fb.Span.ToArray());
            Assert.Equal(FrameType.Data, parsed[0].Type);
            Assert.Equal(42u, parsed[0].StreamId);
            Assert.Equal(payload, parsed[0].Payload.ToArray());
        }
        finally { fb.Return(); }
    }

    [Fact]
    public void IsValidHello_AcceptsCanonicalHello()
    {
        var hello = FrameCodec.Encode(FrameType.Hello, 0, [1]);
        Assert.True(FrameCodec.IsValidHello(hello));
    }

    [Fact]
    public void IsValidHello_RejectsEverythingElse()
    {
        Assert.False(FrameCodec.IsValidHello(FrameCodec.Encode(FrameType.Welcome, 0, [1])));
        Assert.False(FrameCodec.IsValidHello(
            FrameCodec.Encode(FrameType.Hello, 0, [1])
                .Concat(FrameCodec.Encode(FrameType.Hello, 0, [1])).ToArray()));
        Assert.False(FrameCodec.IsValidHello(FrameCodec.Encode(FrameType.Hello, 0, [2])));
        Assert.False(FrameCodec.IsValidHello(FrameCodec.Encode(FrameType.Hello, 3, [1])));
        Assert.False(FrameCodec.IsValidHello(new byte[64]));
    }

    [Fact]
    public void IsValidClientFrame_Rules()
    {
        Assert.True(FrameCodec.IsValidClientFrame(new Frame(FrameType.Open, 1, ReadOnlyMemory<byte>.Empty)));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Open, 1, new byte[1].AsMemory())));

        Assert.True(FrameCodec.IsValidClientFrame(new Frame(FrameType.Data, 1, new byte[1].AsMemory())));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Data, 1, ReadOnlyMemory<byte>.Empty)));

        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Close, 1, new byte[1].AsMemory())));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Ping, 1, ReadOnlyMemory<byte>.Empty)));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Window, 1, ReadOnlyMemory<byte>.Empty)));

        // stream zero is reserved: only small Pong is valid there
        Assert.True(FrameCodec.IsValidClientFrame(new Frame(FrameType.Pong, 0, new byte[1].AsMemory())));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Pong, 0, new byte[65].AsMemory())));
        Assert.False(FrameCodec.IsValidClientFrame(new Frame(FrameType.Open, 0, ReadOnlyMemory<byte>.Empty)));
    }
}
