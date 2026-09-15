using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Supersource.Format;
/// <summary>
/// Little-endian write cursor over a growable buffer. Everything in the format is
/// little-endian; a format that varies by host byte order is not one format.
/// </summary>
public struct ByteWriter
{
    public byte[] Buffer;
    public int Length;

    /// <summary>Where full buffers go, or null when the buffer IS the output.</summary>
    Stream sink;

    /// <summary>Bytes already handed to the sink. Add <see cref="Length"/> for the total.</summary>
    public long Flushed;

    /// <summary>Optional digest fed on flush. Flush at the boundaries of the content to hash.</summary>
    public System.Security.Cryptography.IncrementalHash Hash;

    /// <summary>Whether what is being written now belongs in the digest.</summary>
    public bool Hashing;

    public static ByteWriter Create(int capacity = 1024)
    {
        ByteWriter w = default;
        w.Buffer = new byte[capacity < 16 ? 16 : capacity];
        w.Length = 0;
        return w;
    }

    /// <summary>Creates a streaming writer with a reusable output buffer.</summary>
    public static ByteWriter Over(Stream sink, int window = 1 << 16)
    {
        ByteWriter w = default;
        w.Buffer = new byte[window < 16 ? 16 : window];
        w.Length = 0;
        w.sink = sink;
        return w;
    }

    /// <summary>Total bytes written, whether they have reached the sink yet or not.</summary>
    public long Total
    {
        get { return Flushed + Length; }
    }

    /// <summary>Push what is buffered to the sink. Nothing to do without one.</summary>
    public void Flush()
    {
        if (sink == null || Length == 0)
        {
            return;
        }

        if (Hash != null && Hashing)
        {
            Hash.AppendData(Buffer, 0, Length);
        }

        sink.Write(Buffer, 0, Length);
        Flushed += Length;
        Length = 0;
    }

    public byte[] ToArray()
    {
        byte[] result = new byte[Length];
        Array.Copy(Buffer, result, Length);
        return result;
    }

    public void U8(byte value)
    {
        Ensure(1);
        Buffer[Length] = value;
        Length += 1;
    }

    public void U16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(Buffer, Length, 2), value);
        Length += 2;
    }

    public void U32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(Buffer, Length, 4), value);
        Length += 4;
    }

    public void I32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(Buffer, Length, 4), value);
        Length += 4;
    }

    public void U64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(Buffer, Length, 8), value);
        Length += 8;
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(new Span<byte>(Buffer, Length, value.Length));
        Length += value.Length;
    }

    /// <summary>Length-prefixed UTF-8.</summary>
    public void Str(string value)
    {
        if (value == null)
        {
            U32(0);
            return;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        U32((uint)utf8.Length);
        Bytes(utf8);
    }

    /// <summary>
    /// Writes minimal unsigned LEB128: seven value bits per byte with the high bit indicating
    /// continuation.
    /// </summary>
    public void UVar(ulong value)
    {
        while (value >= 0x80)
        {
            U8((byte)(value | 0x80));
            value >>= 7;
        }
        U8((byte)value);
    }

    /// <summary>Writes a zigzag-encoded signed varint; -1 maps to 1 and 1 maps to 2.</summary>
    public void SVar(long value)
    {
        UVar((ulong)((value << 1) ^ (value >> 63)));
    }

    /// <summary>
    /// Reserves four bytes for later patching. Buffered writers only; streaming callers must patch
    /// lengths by seeking the output stream.
    /// </summary>
    public int Reserve32()
    {
        int at = Length;
        U32(0);
        return at;
    }

    public void Patch32(int at, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(Buffer, at, 4), value);
    }

    public int Reserve64()
    {
        int at = Length;
        U64(0);
        return at;
    }

    public void Patch64(int at, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(Buffer, at, 8), value);
    }

    void Ensure(int extra)
    {
        int needed = Length + extra;
        if (Buffer.Length >= needed)
        {
            return;
        }

        // Flush before growing; only a write larger than the window requires a larger buffer.
        if (sink != null)
        {
            Flush();
            if (Buffer.Length >= extra)
            {
                return;
            }
            needed = extra;
        }

        int size = Buffer.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref Buffer, size);
    }
}

/// <summary>Bounds-checked little-endian reader. Truncated reads throw FormatCorruptException.</summary>
public struct ByteReader
{
    /// <summary>Input bytes, or the current window for a streaming reader.</summary>
    public byte[] Buffer;
    public int At;
    public int End;

    /// <summary>Where the rest comes from, or null when it is all already here.</summary>
    Stream source;

    /// <summary>Bytes of the section the stream has not handed over yet.</summary>
    long pending;

    /// <summary>Where this reader's first byte sits in the file it came from.</summary>
    public long Origin;

    /// <summary>Window position of the first byte, and how many have slid past behind it.</summary>
    int start;
    long discarded;

    /// <summary>Absolute file position of the next byte, used to address mapped payloads.</summary>
    public long Position
    {
        get { return Origin + discarded + (At - start); }
    }

    public static ByteReader Over(byte[] buffer)
    {
        ByteReader r = default;
        r.Buffer = buffer;
        r.At = 0;
        r.End = buffer.Length;
        return r;
    }

    public static ByteReader Over(byte[] buffer, int at, int length)
    {
        ByteReader r = default;
        r.Buffer = buffer;
        r.At = at;
        r.End = at + length;
        r.start = at;
        return r;
    }

    /// <summary>
    /// Creates a windowed reader for one section. The buffer grows only when an individual read
    /// exceeds its capacity.
    /// </summary>
    public static ByteReader Over(Stream source, long length, int window, long origin = 0)
    {
        ByteReader r = default;
        r.Buffer = new byte[window < 1024 ? 1024 : window];
        r.At = 0;
        r.End = 0;
        r.source = source;
        r.pending = length;
        r.Origin = origin;
        return r;
    }

    /// <summary>Skips bytes without decoding them, seeking the underlying stream when necessary.</summary>
    public void Skip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new FormatCorruptException(
                "skipping " + count + " bytes with only " + Remaining + " left");
        }

        int inWindow = End - At;
        if (count <= inWindow)
        {
            At += (int)count;
            return;
        }

        count -= inWindow;
        discarded += inWindow + (At - start);
        start = 0;
        At = 0;
        End = 0;

        source.Position += count;
        pending -= count;
        discarded += count;
    }

    /// <summary>Bytes of this section still to come, in the window and behind it.</summary>
    public long Remaining
    {
        get { return (End - At) + pending; }
    }

    public byte U8()
    {
        Need(1);
        byte value = Buffer[At];
        At += 1;
        return value;
    }

    public ushort U16()
    {
        Need(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(Buffer, At, 2));
        At += 2;
        return value;
    }

    public uint U32()
    {
        Need(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(Buffer, At, 4));
        At += 4;
        return value;
    }

    public int I32()
    {
        Need(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(Buffer, At, 4));
        At += 4;
        return value;
    }

    public ulong U64()
    {
        Need(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(Buffer, At, 8));
        At += 8;
        return value;
    }

    /// <summary>Reads minimal unsigned LEB128. Rejects overflow, truncation and padded encodings.</summary>
    public ulong UVar()
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            if (shift > 63)
            {
                throw new FormatCorruptException("a variable-width integer runs past 64 bits");
            }

            byte b = U8();
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                // Reject padded encodings so read/write preserves the canonical representation.
                if (shift > 0 && b == 0)
                {
                    throw new FormatCorruptException("a variable-width integer is padded, not canonical");
                }
                return value;
            }

            shift += 7;
        }
    }

    public long SVar()
    {
        ulong raw = UVar();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    /// <summary>
    /// Reads a varint count and checks that the remaining section can hold that many minimum-sized
    /// entries before allocating.
    /// </summary>
    public int UVarCount(int minimumBytesEach, string what)
    {
        int count = UVarInt();
        if (minimumBytesEach > 0 && (long)count * minimumBytesEach > Remaining)
        {
            throw new FormatCorruptException(
                what + " claims " + count + " entries, and only " + Remaining + " bytes remain");
        }
        return count;
    }

    /// <summary>A <see cref="UVar"/> that has to fit in an int, which every count and length here does.</summary>
    public int UVarInt()
    {
        ulong value = UVar();
        if (value > int.MaxValue)
        {
            throw new FormatCorruptException("a length or count of " + value + " is absurd");
        }
        return (int)value;
    }

    public int SVarInt()
    {
        long value = SVar();
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new FormatCorruptException("an index of " + value + " is out of range");
        }
        return (int)value;
    }

    public int Count(int minimumBytesEach, string what)
    {
        uint raw = U32();

        if (raw > int.MaxValue)
        {
            throw new FormatCorruptException(
                "at byte " + At + ": " + what + " claims " + raw + " entries");
        }

        int count = (int)raw;
        if ((long)count * minimumBytesEach > Remaining)
        {
            throw new FormatCorruptException(
                "at byte " + At + ": " + what + " claims " + count +
                " entries, which cannot fit in the " + Remaining + " bytes that remain");
        }

        return count;
    }

    public ReadOnlySpan<byte> Bytes(int length)
    {
        Need(length);
        ReadOnlySpan<byte> value = new ReadOnlySpan<byte>(Buffer, At, length);
        At += length;
        return value;
    }

    public byte[] BytesCopy(int length)
    {
        Need(length);
        byte[] value = new byte[length];
        Array.Copy(Buffer, At, value, 0, length);
        At += length;
        return value;
    }

    public string Str()
    {
        int length = Count(1, "string");
        Need(length);
        string value = Encoding.UTF8.GetString(Buffer, At, length);
        At += length;
        return value;
    }

    /// <summary>
    /// Ensures the next count bytes are available contiguously, refilling the stream window if
    /// needed.
    /// </summary>
    void Need(int count)
    {
        // Use wide arithmetic for bounds checks so a large length cannot overflow past the section
        // end.
        if (count < 0)
        {
            throw new FormatCorruptException("a negative read of " + count + " bytes");
        }

        if ((long)At + count <= End)
        {
            return;
        }

        if (source == null || count > Remaining)
        {
            throw new FormatCorruptException(
                "truncated at byte " + At + ": wanted " + count + ", " + Remaining + " left");
        }

        Refill(count);
    }

    /// <summary>
    /// Slide what is left to the front and read more in behind it, growing the window if one
    /// read wants more than it holds.
    /// </summary>
    void Refill(int count)
    {
        int left = End - At;

        if (count > Buffer.Length)
        {
            int size = Buffer.Length;
            while (size < count)
            {
                size *= 2;
            }

            byte[] grown = new byte[size];
            Array.Copy(Buffer, At, grown, 0, left);
            Buffer = grown;
        }
        else if (At > 0)
        {
            Array.Copy(Buffer, At, Buffer, 0, left);
        }

        discarded += At - start;
        start = 0;
        At = 0;
        End = left;

        while (End < count)
        {
            int want = Buffer.Length - End;
            if (want > pending)
            {
                want = (int)pending;
            }

            int got = source.Read(Buffer, End, want);
            if (got <= 0)
            {
                throw new FormatCorruptException(
                    "the stream ended with " + pending + " bytes of the section still to come");
            }

            End += got;
            pending -= got;
        }
    }
}

/// <summary>
/// Invalid framing or unsupported major format version. Unknown compatible sections are preserved
/// instead.
/// </summary>
public class FormatCorruptException : Exception
{
    public FormatCorruptException(string message) : base(message)
    {
    }
}
