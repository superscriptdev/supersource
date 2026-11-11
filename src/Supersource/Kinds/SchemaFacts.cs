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

    // 1 << 0 and 1 << 1 are retired (declaration and opens-scope): which kinds a name can resolve
    // to, and where names are visible, is a language's knowledge of its own kinds. Files before
    // major 5 may carry the bits, and a reader drops them.

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

/// <summary>The five ways a payload can be read without its kind's software.</summary>
public enum PayloadForm : byte
{
    /// <summary>No bytes.</summary>
    None = 0,

    /// <summary>Bytes with no declared reading. Preserved exactly and never interpreted.</summary>
    Opaque = 1,

    /// <summary>UTF-8 text.</summary>
    Text = 2,

    /// <summary>Exactly one element.</summary>
    One = 3,

    /// <summary>An optional fixed-size header, then any number of elements.</summary>
    Many = 4,
}

/// <summary>A number a field holds. Little-endian.</summary>
public enum PayloadScalar : byte
{
    I8 = 1,
    I16 = 2,
    I32 = 3,
    I64 = 4,
    U8 = 5,
    U16 = 6,
    U32 = 7,
    U64 = 8,
    F16 = 9,
    F32 = 10,
    F64 = 11,

    /// <summary>One byte, 0 or 1.</summary>
    Bool = 12,
}

/// <summary>Sizes and names of <see cref="PayloadScalar"/>.</summary>
public static class PayloadScalars
{
    public static int Size(PayloadScalar scalar)
    {
        switch (scalar)
        {
            case PayloadScalar.I8:
            case PayloadScalar.U8:
            case PayloadScalar.Bool:
                return 1;
            case PayloadScalar.I16:
            case PayloadScalar.U16:
            case PayloadScalar.F16:
                return 2;
            case PayloadScalar.I32:
            case PayloadScalar.U32:
            case PayloadScalar.F32:
                return 4;
            case PayloadScalar.I64:
            case PayloadScalar.U64:
            case PayloadScalar.F64:
                return 8;
            default:
                return 0;
        }
    }

    public static string Name(PayloadScalar scalar)
    {
        switch (scalar)
        {
            case PayloadScalar.I8: return "i8";
            case PayloadScalar.I16: return "i16";
            case PayloadScalar.I32: return "i32";
            case PayloadScalar.I64: return "i64";
            case PayloadScalar.U8: return "u8";
            case PayloadScalar.U16: return "u16";
            case PayloadScalar.U32: return "u32";
            case PayloadScalar.U64: return "u64";
            case PayloadScalar.F16: return "f16";
            case PayloadScalar.F32: return "f32";
            case PayloadScalar.F64: return "f64";
            case PayloadScalar.Bool: return "bool";
            default: return "?";
        }
    }

    /// <summary>The scalar a name spells, such as `f32`, or false.</summary>
    public static bool TryParse(string name, out PayloadScalar scalar)
    {
        for (PayloadScalar s = PayloadScalar.I8; s <= PayloadScalar.Bool; s++)
        {
            if (Name(s) == name)
            {
                scalar = s;
                return true;
            }
        }
        scalar = default;
        return false;
    }
}

/// <summary>
/// A named scalar at an offset inside an element or a header, optionally repeated a fixed number
/// of times: `position f32×3 @0`. What lets a stranger read a vertex, a row or a pixel.
/// </summary>
public struct PayloadField : IEquatable<PayloadField>
{
    public string Name;
    public PayloadScalar Type;

    /// <summary>Bytes from the start of the element.</summary>
    public ushort Offset;

    /// <summary>How many scalars in a row. One for a plain field.</summary>
    public ushort Repeat;

    public static PayloadField Of(string name, PayloadScalar type, ushort offset, ushort repeat = 1)
    {
        PayloadField field;
        field.Name = name ?? string.Empty;
        field.Type = type;
        field.Offset = offset;
        field.Repeat = repeat < 1 ? (ushort)1 : repeat;
        return field;
    }

    /// <summary>The bytes this field covers.</summary>
    public int Size
    {
        get { return PayloadScalars.Size(Type) * Repeat; }
    }

    public bool Equals(PayloadField other)
    {
        return Name == other.Name && Type == other.Type && Offset == other.Offset && Repeat == other.Repeat;
    }

    public override bool Equals(object obj)
    {
        return obj is PayloadField other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Name, Type, Offset, Repeat);
    }
}

/// <summary>
/// The fixed-size unit a <see cref="PayloadForm.One"/> or <see cref="PayloadForm.Many"/> payload is
/// made of, and the shape of a header. Either just sized — a reader can find element *n* but not
/// look inside it — or described by fields. There are no nested elements and no variable-length
/// fields: those make a type system, which is a language's.
/// </summary>
public struct PayloadElement : IEquatable<PayloadElement>
{
    /// <summary>Bytes per element. Zero only for an absent header.</summary>
    public ushort Size;

    /// <summary>What is inside, or empty when the element is only sized.</summary>
    public PayloadField[] Fields;

    public bool IsDescribed
    {
        get { return Fields != null && Fields.Length > 0; }
    }

    /// <summary>An element a reader can count but not look inside.</summary>
    public static PayloadElement Sized(ushort size)
    {
        PayloadElement element;
        element.Size = size;
        element.Fields = Array.Empty<PayloadField>();
        return element;
    }

    /// <summary>An element that is one scalar.</summary>
    public static PayloadElement Of(PayloadScalar scalar)
    {
        return Of((ushort)PayloadScalars.Size(scalar), PayloadField.Of(string.Empty, scalar, 0));
    }

    /// <summary>An element of named fields. Size may exceed what the fields cover, for padding.</summary>
    public static PayloadElement Of(ushort size, params PayloadField[] fields)
    {
        PayloadElement element;
        element.Size = size;
        element.Fields = fields ?? Array.Empty<PayloadField>();
        return element;
    }

    /// <summary>Whether this is exactly one scalar, unnamed, at offset zero.</summary>
    public bool Is(PayloadScalar scalar)
    {
        return Fields != null && Fields.Length == 1 && Fields[0].Type == scalar && Fields[0].Offset == 0
            && Fields[0].Repeat == 1 && Size == PayloadScalars.Size(scalar)
            && string.IsNullOrEmpty(Fields[0].Name);
    }

    public bool Equals(PayloadElement other)
    {
        if (Size != other.Size)
        {
            return false;
        }
        int mine = Fields == null ? 0 : Fields.Length;
        int theirs = other.Fields == null ? 0 : other.Fields.Length;
        if (mine != theirs)
        {
            return false;
        }
        for (int i = 0; i < mine; i++)
        {
            if (!Fields[i].Equals(other.Fields[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object obj)
    {
        return obj is PayloadElement other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Size, Fields == null ? 0 : Fields.Length);
    }
}

/// <summary>
/// How a kind's payload bytes, or a relation's edge data, read without the kind's software. Stated
/// once per kind in its schema; a node stores only its bytes.
/// </summary>
public struct PayloadFormat : IEquatable<PayloadFormat>
{
    public PayloadForm Form;

    /// <summary>For One and Many: what each element is.</summary>
    public PayloadElement Element;

    /// <summary>For Many and Text: what comes before the elements or the text. Size zero for none.</summary>
    public PayloadElement Header;

    public static readonly PayloadFormat None = Of(PayloadForm.None);
    public static readonly PayloadFormat Opaque = Of(PayloadForm.Opaque);
    public static readonly PayloadFormat Text = Of(PayloadForm.Text);

    static PayloadFormat Of(PayloadForm form)
    {
        PayloadFormat format = default;
        format.Form = form;
        return format;
    }

    /// <summary>Exactly one scalar: a number, or a boolean.</summary>
    public static PayloadFormat One(PayloadScalar scalar)
    {
        return One(PayloadElement.Of(scalar));
    }

    /// <summary>Exactly one element.</summary>
    public static PayloadFormat One(PayloadElement element)
    {
        PayloadFormat format = Of(PayloadForm.One);
        format.Element = element;
        return format;
    }

    /// <summary>UTF-8 text after a fixed header: an address and then a name, say.</summary>
    public static PayloadFormat TextAfter(PayloadElement header)
    {
        PayloadFormat format = Of(PayloadForm.Text);
        format.Header = header.Fields == null ? PayloadElement.Sized(header.Size) : header;
        return format;
    }

    /// <summary>Any number of one scalar.</summary>
    public static PayloadFormat Many(PayloadScalar scalar)
    {
        return Many(PayloadElement.Of(scalar));
    }

    /// <summary>Any number of elements, after a header when one is given.</summary>
    public static PayloadFormat Many(PayloadElement element, PayloadElement header = default)
    {
        PayloadFormat format = Of(PayloadForm.Many);
        format.Element = element;
        format.Header = header.Fields == null ? PayloadElement.Sized(header.Size) : header;
        return format;
    }

    /// <summary>The length every payload of this format has, or -1 when it varies.</summary>
    public int FixedLength
    {
        get
        {
            switch (Form)
            {
                case PayloadForm.None: return 0;
                case PayloadForm.One: return Element.Size;
                default: return -1;
            }
        }
    }

    /// <summary>Whether a payload is made of fixed-size elements: One or Many.</summary>
    public bool HasElements
    {
        get { return (Form == PayloadForm.One || Form == PayloadForm.Many) && Element.Size > 0; }
    }

    /// <summary>Bytes before the first element.</summary>
    public int ElementsStart
    {
        get { return Form == PayloadForm.Many ? Header.Size : 0; }
    }

    /// <summary>Bytes before the elements or the text: the header's size, or zero.</summary>
    public int HeaderSize
    {
        get { return Form == PayloadForm.Many || Form == PayloadForm.Text ? Header.Size : 0; }
    }

    /// <summary>Complete elements in a payload of this many bytes. Zero when it has none.</summary>
    public int ElementCount(int length)
    {
        if (!HasElements || length <= ElementsStart)
        {
            return 0;
        }
        return (length - ElementsStart) / Element.Size;
    }

    /// <summary>Whether this is exactly one scalar of the given type.</summary>
    public bool Is(PayloadScalar scalar)
    {
        return Form == PayloadForm.One && Element.Is(scalar);
    }

    public bool Equals(PayloadFormat other)
    {
        return Form == other.Form && Element.Equals(other.Element) && Header.Equals(other.Header);
    }

    public override bool Equals(object obj)
    {
        return obj is PayloadFormat other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Form, Element.GetHashCode(), Header.GetHashCode());
    }

    public static bool operator ==(PayloadFormat a, PayloadFormat b) { return a.Equals(b); }
    public static bool operator !=(PayloadFormat a, PayloadFormat b) { return !a.Equals(b); }

    /// <summary>`none`, `text`, `one i64`, `many {position f32×3 @0, …} 32` and so on.</summary>
    public override string ToString()
    {
        switch (Form)
        {
            case PayloadForm.None: return "none";
            case PayloadForm.Opaque: return "opaque";
            case PayloadForm.Text:
                return Header.Size > 0 ? "text after " + Describe(Header) : "text";
            case PayloadForm.One: return "one " + Describe(Element);
            case PayloadForm.Many:
                return Header.Size > 0
                    ? "many " + Describe(Element) + " after " + Describe(Header)
                    : "many " + Describe(Element);
            default: return "?";
        }
    }

    static string Describe(PayloadElement element)
    {
        if (element.Fields != null && element.Fields.Length == 1 && element.Is(element.Fields[0].Type))
        {
            return PayloadScalars.Name(element.Fields[0].Type);
        }
        if (!element.IsDescribed)
        {
            return element.Size + " bytes";
        }

        System.Text.StringBuilder text = new System.Text.StringBuilder("{");
        for (int i = 0; i < element.Fields.Length; i++)
        {
            PayloadField field = element.Fields[i];
            if (i > 0)
            {
                text.Append(", ");
            }
            text.Append(field.Name).Append(' ').Append(PayloadScalars.Name(field.Type));
            if (field.Repeat > 1)
            {
                text.Append('×').Append(field.Repeat);
            }
            text.Append(" @").Append(field.Offset);
        }
        return text.Append("} ").Append(element.Size).ToString();
    }
}

/// <summary>What edge data a reference of this relation carries. None for most relations.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RelationSchema
{
    public PayloadFormat Payload;
}

/// <summary>
/// One kind's schema: what a reader needs to walk, check and write back its nodes without the
/// kind's software.
/// </summary>
public struct KindSchema
{
    /// <summary>The interned handle this schema describes. Index of this schema in KindTable.Schemas.</summary>
    public KindId Kind;

    public StructuralFact Facts;
    public PayloadFormat Payload;

    /// <summary>Run into KindTable.RelationArena: the reference relations nodes of this kind use.</summary>
    public int RelationOffset;
    public int RelationCount;

    public bool InteriorOpaque
    {
        get { return (Facts & StructuralFact.InteriorOpaque) != 0; }
    }

    public bool ChildOrderSignificant
    {
        get { return (Facts & StructuralFact.ChildOrderSignificant) != 0; }
    }

    public bool Annotates
    {
        get { return (Facts & StructuralFact.Annotation) != 0; }
    }
}
