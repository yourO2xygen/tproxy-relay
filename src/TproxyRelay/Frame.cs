using System.Buffers;
using System.Buffers.Binary;

namespace TproxyRelay;

public enum FrameType : byte
{
    Open = 0x01,
    Data = 0x02,
    Close = 0x03,
    Window = 0x04,
    Ping = 0x05,
    Pong = 0x06,
    Hello = 0x10,
    Welcome = 0x11,
    Bye = 0x1F
}

/// <summary>A downlink frame: pooled buffer for DATA, plain array for control frames.</summary>
public readonly record struct FrameBuf
{
    public required byte[] Buffer { get; init; }
    public required int Length { get; init; }
    public bool Pooled { get; init; }
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public static FrameBuf Buy(byte[] exactSizedArray) => new() { Buffer = exactSizedArray, Length = exactSizedArray.Length, Pooled = false };

    /// <summary>Rents a buffer for a DATA frame: the 64KiB chunks dominate downlink
    /// allocations, so they must not hit the large object heap per chunk.</summary>
    public static FrameBuf RentData(uint streamId, ReadOnlySpan<byte> payload)
    {
        var buf = ArrayPool<byte>.Shared.Rent(FrameCodec.HeaderSize + payload.Length);
        FrameCodec.WriteHeader(buf, FrameType.Data, streamId, payload.Length);
        payload.CopyTo(buf.AsSpan(FrameCodec.HeaderSize));
        return new FrameBuf { Buffer = buf, Length = FrameCodec.HeaderSize + payload.Length, Pooled = true };
    }

    public void Return()
    {
        if (Pooled)
            ArrayPool<byte>.Shared.Return(Buffer);
    }
}

public readonly record struct Frame(FrameType Type, uint StreamId, ReadOnlyMemory<byte> Payload);

public sealed class FrameException(string message) : Exception(message);

public sealed class BudgetException(string message) : Exception(message);

public static class FrameCodec
{
    public const int HeaderSize = 8;
    public const int MaxPayload = 1024 * 1024;
    public const int DataChunk = 64 * 1024;
    public const int MaxBatchFrames = 4096;
    public const uint MaxStreamId = 0xFFFFFF;
    public const uint InitialStreamWindow = 4 * 1024 * 1024;

    internal static void WriteHeader(byte[] buf, FrameType type, uint streamId, int payloadLength)
    {
        buf[0] = (byte)type;
        buf[1] = (byte)(streamId >> 16);
        buf[2] = (byte)(streamId >> 8);
        buf[3] = (byte)streamId;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), (uint)payloadLength);
    }

    public static byte[] Encode(FrameType type, uint streamId) => Encode(type, streamId, []);

    public static byte[] Encode(FrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(streamId, MaxStreamId);
        var result = new byte[HeaderSize + payload.Length];
        WriteHeader(result, type, streamId, payload.Length);
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    /// <summary>Zero-copy parse: frame payloads are slices of the input body.</summary>
    public static List<Frame> ParseAll(ReadOnlyMemory<byte> input)
    {
        var frames = new List<Frame>(4);
        var span = input.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            if (frames.Count == MaxBatchFrames)
                throw new FrameException("frame batch contains too many frames");
            if (span.Length - offset < HeaderSize)
                throw new FrameException("incomplete frame header");
            var length = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
            if (length > MaxPayload)
                throw new FrameException("frame payload exceeds limit");
            var full = HeaderSize + (int)length;
            if (full > span.Length - offset)
                throw new FrameException("incomplete frame body");
            var streamId = (uint)((span[offset + 1] << 16) | (span[offset + 2] << 8) | span[offset + 3]);
            frames.Add(new Frame((FrameType)span[offset], streamId, input.Slice(offset + HeaderSize, (int)length)));
            offset += full;
        }
        if (frames.Count == 0)
            throw new FrameException("empty frame batch");
        return frames;
    }

    public static bool IsValidClientFrame(in Frame f)
    {
        if (f.StreamId == 0)
            return f.Type == FrameType.Pong && f.Payload.Length <= 64;
        switch (f.Type)
        {
            case FrameType.Open or FrameType.Close:
                return f.Payload.Length == 0;
            case FrameType.Data:
                return f.Payload.Length > 0;
            case FrameType.Window:
                return f.Payload.Length == 4 &&
                       BinaryPrimitives.ReadUInt32BigEndian(f.Payload.Span) != 0;
            default:
                return false;
        }
    }

    public static bool IsValidHello(byte[] body)
    {
        try
        {
            var frames = ParseAll(body);
            return frames.Count == 1
                && frames[0].Type == FrameType.Hello
                && frames[0].StreamId == 0
                && frames[0].Payload.Length == 1
                && frames[0].Payload.Span[0] == 1;
        }
        catch (FrameException)
        {
            return false;
        }
    }

    public static byte[] EncodeWindow(uint streamId, uint amount)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, amount);
        return Encode(FrameType.Window, streamId, payload);
    }
}
