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

public readonly record struct Frame(FrameType Type, uint StreamId, byte[] Payload);

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

    public static byte[] Encode(FrameType type, uint streamId) => Encode(type, streamId, []);

    public static byte[] Encode(FrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(streamId, MaxStreamId);
        var result = new byte[HeaderSize + payload.Length];
        result[0] = (byte)type;
        result[1] = (byte)(streamId >> 16);
        result[2] = (byte)(streamId >> 8);
        result[3] = (byte)streamId;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    public static List<Frame> ParseAll(ReadOnlySpan<byte> input)
    {
        var frames = new List<Frame>(4);
        while (!input.IsEmpty)
        {
            if (frames.Count == MaxBatchFrames)
                throw new FrameException("frame batch contains too many frames");
            if (input.Length < HeaderSize)
                throw new FrameException("incomplete frame header");
            var length = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4, 4));
            if (length > MaxPayload)
                throw new FrameException("frame payload exceeds limit");
            var full = HeaderSize + (int)length;
            if (full > input.Length)
                throw new FrameException("incomplete frame body");
            var streamId = (uint)((input[1] << 16) | (input[2] << 8) | input[3]);
            var payload = input.Slice(HeaderSize, (int)length).ToArray();
            frames.Add(new Frame((FrameType)input[0], streamId, payload));
            input = input.Slice(full);
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
                       BinaryPrimitives.ReadUInt32BigEndian(f.Payload) != 0;
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
                && frames[0].Payload[0] == 1;
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
