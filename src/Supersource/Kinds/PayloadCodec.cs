using System;
using System.Buffers.Binary;
using System.Text;

namespace Supersource.Kinds;
/// <summary>
/// Primitive payload codecs. Short numeric and Boolean inputs return zero or false; encoding and
/// decoding do not validate domain semantics.
/// </summary>
public static class PayloadCodec
{
    public static long ReadInt64(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(payload) : 0L;
    }

    public static double ReadFloat64(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(payload) : 0d;
    }

    public static bool ReadBool(ReadOnlySpan<byte> payload)
    {
        return payload.Length > 0 && payload[0] != 0;
    }

    public static string ReadUtf8(ReadOnlySpan<byte> payload)
    {
        return payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(payload);
    }

    public static byte[] WriteInt64(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] WriteFloat64(double value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] WriteBool(bool value)
    {
        return new byte[] { value ? (byte)1 : (byte)0 };
    }

    public static byte[] WriteUtf8(string value)
    {
        return value == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
    }
}
