using System;
using Supersource.Kinds;
using Supersource.Timeline;

namespace Supersource.Compare;
/// <summary>
/// Finds one changed payload region by trimming equal prefixes and suffixes. Uses schema
/// information to align text and fixed-size array edits; it does not identify separate changes
/// within that region.
/// </summary>
public static class PayloadDiff
{
    /// <summary>
    /// How a payload should be compared, from what its schema says about it.
    /// </summary>
    public enum Shape : byte
    {
        /// <summary>One value. A changed number is a new number, not three changed bytes.</summary>
        Atom = 0,

        /// <summary>A sequence of bytes with no declared element size.</summary>
        Bytes = 1,

        /// <summary>UTF-8: a sequence of bytes whose boundaries are code points.</summary>
        Text = 2,

        /// <summary>A declared array: a header, then fixed-size elements.</summary>
        Array = 3,
    }

    public static Shape ShapeOf(in KindSchema schema)
    {
        switch (schema.Payload)
        {
            case PayloadFormat.Int64:
            case PayloadFormat.Float64:
            case PayloadFormat.Bool:
                return Shape.Atom;

            case PayloadFormat.Utf8:
                return Shape.Text;

            case PayloadFormat.Record:
                return schema.Stride.IsArray ? Shape.Array : Shape.Bytes;

            default:
                return Shape.Bytes;
        }
    }

    /// <summary>
    /// Returns one changed region as an offset and before/after lengths, or false for equal
    /// payloads. Aligns to code-point or element boundaries when the declared shape is valid.
    /// </summary>
    public static bool Region(
        Shape shape,
        in PayloadStride stride,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        out int at,
        out int removed,
        out int inserted)
    {
        at = 0;
        removed = before.Length;
        inserted = after.Length;

        if (before.SequenceEqual(after))
        {
            return false;
        }

        if (shape == Shape.Atom)
        {
            return true;   // whole value, and the caller will say so
        }

        int unit = 1;
        int header = 0;
        if (shape == Shape.Array)
        {
            unit = stride.Unit;
            header = stride.Header;

            // For malformed array layouts, fall back to bytes rather than trusting the stride.
            if (unit <= 0
                || before.Length < header || after.Length < header
                || (before.Length - header) % unit != 0
                || (after.Length - header) % unit != 0)
            {
                unit = 1;
                header = 0;
            }
        }

        int shortest = before.Length < after.Length ? before.Length : after.Length;

        int prefix = 0;
        while (prefix < shortest && before[prefix] == after[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < shortest - prefix
            && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        if (shape == Shape.Text)
        {
            prefix = BackToCodePoint(before, prefix);
            suffix = SuffixToCodePoint(before, prefix, suffix);
        }
        else if (unit > 1)
        {
            // Handle changes inside the header before aligning the prefix to an element boundary.
            prefix = prefix < header ? 0 : header + Floor(prefix - header, unit);
            suffix = Floor(suffix, unit);
        }

        at = prefix;
        removed = before.Length - prefix - suffix;
        inserted = after.Length - prefix - suffix;
        return true;
    }

    /// <summary>
    /// Returns an operation converting one payload to the other, or false when equal. Whole-payload
    /// changes use ModifyValue; narrower changes use Splice.
    /// </summary>
    public static bool Between(
        in KindSchema schema,
        NodeId node,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        out Operation op)
    {
        op = default;

        Shape shape = ShapeOf(in schema);
        if (!Region(shape, in schema.Stride, before, after, out int at, out int removed, out int inserted))
        {
            return false;
        }

        if (at == 0 && removed == before.Length)
        {
            op.Kind = OpKind.ModifyValue;
            op.Node = node;
            op.Value = after.ToArray();
            return true;
        }

        op.Kind = OpKind.Splice;
        op.Node = node;
        op.Index = at;
        op.Count = removed;
        op.Value = after.Slice(at, inserted).ToArray();
        return true;
    }

    static int Floor(int value, int unit)
    {
        return value - (value % unit);
    }

    /// <summary>Back up to the start of the code point this offset is inside, if it is inside one.</summary>
    static int BackToCodePoint(ReadOnlySpan<byte> bytes, int at)
    {
        while (at > 0 && at < bytes.Length && (bytes[at] & 0xC0) == 0x80)
        {
            at--;
        }
        return at;
    }

    /// <summary>
    /// Shrink a shared suffix until it starts on a code point boundary, so the range removed
    /// begins and ends between characters.
    /// </summary>
    static int SuffixToCodePoint(ReadOnlySpan<byte> bytes, int prefix, int suffix)
    {
        while (suffix > 0)
        {
            int start = bytes.Length - suffix;
            if (start <= prefix)
            {
                return 0;
            }
            if ((bytes[start] & 0xC0) != 0x80)
            {
                return suffix;
            }
            suffix--;
        }
        return 0;
    }
}
