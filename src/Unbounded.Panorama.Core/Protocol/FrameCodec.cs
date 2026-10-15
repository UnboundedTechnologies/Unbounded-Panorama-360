using System.Buffers.Binary;

namespace Unbounded.Panorama.Protocol;

/// <summary>
/// Wire envelope: ASCII "TRYX", uint32 little-endian payload length, protobuf payload.
/// There is no trailer or frame checksum.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 8;
    public const int MaxPayloadSize = 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "TRYX"u8;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload {payload.Length} exceeds {MaxPayloadSize} bytes.");

        var frame = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }
}

/// <summary>
/// Reassembles frames from arbitrary IN chunks. Bytes before the next "TRYX" are discarded,
/// and a "TRYX" with an oversized length is skipped.
/// </summary>
public sealed class FrameReassembler
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _count;

    public long DiscardedBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_count + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>Returns the next complete payload, or null when more bytes are needed.</summary>
    public byte[]? TryTake()
    {
        while (true)
        {
            var window = _buffer.AsSpan(0, _count);
            var start = window.IndexOf(FrameCodec.Magic);
            if (start < 0)
            {
                // Keep up to 3 trailing bytes that could be the start of a split magic.
                var keep = Math.Min(_count, FrameCodec.Magic.Length - 1);
                Discard(_count - keep);
                return null;
            }
            if (start > 0)
            {
                Discard(start);
                continue;
            }
            if (_count < FrameCodec.HeaderSize)
                return null;

            var length = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(4));
            if (length > FrameCodec.MaxPayloadSize)
            {
                Discard(1);
                continue;
            }
            if (_count < FrameCodec.HeaderSize + (int)length)
                return null;

            var payload = _buffer.AsSpan(FrameCodec.HeaderSize, (int)length).ToArray();
            Consume(FrameCodec.HeaderSize + (int)length);
            return payload;
        }
    }

    private void Discard(int n)
    {
        DiscardedBytes += n;
        Consume(n);
    }

    private void Consume(int n)
    {
        _buffer.AsSpan(n, _count - n).CopyTo(_buffer);
        _count -= n;
    }
}
