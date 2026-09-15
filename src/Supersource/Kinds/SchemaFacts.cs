using System;
using System.Runtime.InteropServices;

namespace Supersource.Kinds;
/// <summary>
/// Structural facts encoded in kind schemas. Adding a fact changes the schema vocabulary;
/// application validation rules are separate.
/// </summary>
[Flags]
public enum StructuralFact : ushort
{
    None = 0,

    /// <summary>Nodes of this kind are declarations — something binding can resolve a name to.</summary>
    Declaration = 1 << 0,

    /// <summary>Nodes of this kind open a scope. Read by binding, and by nothing else.</summary>
    OpensScope = 1 << 1,

    /// <summary>
    /// The interior has no declared structure available to a generic reader. Its bytes
    /// remain content that readers can preserve without interpreting.
    /// </summary>
    InteriorOpaque = 1 << 2,

    /// <summary>Sibling order is meaningful and should be preserved by comparison tools.</summary>
    ChildOrderSignificant = 1 << 3,

    /// <summary>
    /// Nodes of this kind annotate their parent. Readers can distinguish annotations from
    /// ordinary members; applications decide their presentation and semantic significance.
    /// </summary>
    Annotation = 1 << 4,
}

/// <summary>
/// How to interpret a node's payload bytes without knowing the kind's software. Closed:
/// a format this list cannot describe is <see cref="PayloadFormat.Opaque"/>. Its bytes are
/// preserved without requiring an interpreter.
/// </summary>
public enum PayloadFormat : byte
{
    /// <summary>Nodes of this kind carry no payload.</summary>
    None = 0,

    /// <summary>Bytes with no declared interpretation; generic readers preserve them unchanged.</summary>
    Opaque = 1,

    /// <summary>UTF-8 text. Labels, names, prose.</summary>
    Utf8 = 2,

    /// <summary>Little-endian signed 64-bit integer.</summary>
    Int64 = 3,

    /// <summary>Little-endian IEEE 754 binary64.</summary>
    Float64 = 4,

    /// <summary>A single byte, 0 or 1.</summary>
    Bool = 5,

    /// <summary>
    /// A composite payload with a layout defined by its kind. Stride metadata may additionally
    /// describe fixed-size elements.
    /// </summary>
    Record = 6,
}

/// <summary>Optional fallback presentation hints for readers without a domain renderer.</summary>
public enum DisplayShape : byte
{
    Default = 0,

    /// <summary>Draws on its own line(s) and contains other things. A body, a module.</summary>
    Block = 1,

    /// <summary>Draws inline within a line. An operator, a use.</summary>
    Inline = 2,

    /// <summary>Draws as an atom with no visible interior. A literal, an opaque leaf.</summary>
    Leaf = 3,
}

/// <summary>
/// Fixed-size elements following Header bytes of a Record payload. Unit = 0 means no array layout
/// is declared. Element meaning remains domain-specific.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PayloadStride
{
    /// <summary>Bytes before the array starts.</summary>
    public ushort Header;

    /// <summary>Bytes per element, or zero for "not an array".</summary>
    public ushort Unit;

    public bool IsArray
    {
        get { return Unit != 0; }
    }

    public static PayloadStride Of(ushort header, ushort unit)
    {
        PayloadStride stride;
        stride.Header = header;
        stride.Unit = unit;
        return stride;
    }

    /// <summary>
    /// Returns the number of complete elements after the header. Truncated headers and absent
    /// strides yield zero.
    /// </summary>
    public int ElementCount(int length)
    {
        if (Unit == 0 || length <= Header)
        {
            return 0;
        }
        return (length - Header) / Unit;
    }
}

/// <summary>
/// The format and optional stride of data carried by a bound reference. None declares no additional
/// data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RelationSchema
{
    public PayloadFormat Payload;
    public PayloadStride Stride;
}

/// <summary>
/// One kind's complete schema: every fact needed to compute scope, resolve references,
/// diff and merge without understanding content.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KindSchema
{
    /// <summary>The interned handle this schema describes. Index of this schema in KindTable.Schemas.</summary>
    public KindId Kind;

    public StructuralFact Facts;
    public PayloadFormat Payload;
    public DisplayShape Shape;

    /// <summary>
    /// The array shape of a <see cref="PayloadFormat.Record"/> payload, when it has one.
    /// Zero for every other format, and for a record nobody has described this way.
    /// </summary>
    public PayloadStride Stride;

    /// <summary>Packed RGBA8. Zero means "no opinion", which is not the same as transparent black.</summary>
    public uint DisplayColor;

    /// <summary>Run into KindTable.RelationArena: the reference relations nodes of this kind use.</summary>
    public int RelationOffset;
    public int RelationCount;

    public bool IsDeclaration
    {
        get { return (Facts & StructuralFact.Declaration) != 0; }
    }

    public bool OpensScope
    {
        get { return (Facts & StructuralFact.OpensScope) != 0; }
    }

    public bool InteriorOpaque
    {
        get { return (Facts & StructuralFact.InteriorOpaque) != 0; }
    }

    public bool ChildOrderSignificant
    {
        get { return (Facts & StructuralFact.ChildOrderSignificant) != 0; }
    }
}
