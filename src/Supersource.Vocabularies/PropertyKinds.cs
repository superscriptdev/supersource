using System;
using System.Text;

namespace Supersource.Kinds;
/// <summary>A primitive property value and its format. None represents a tag with no value.</summary>
public readonly struct PropertyValue
{
    public readonly PayloadFormat Format;
    public readonly long Whole;
    public readonly double Real;
    public readonly bool Truth;
    public readonly string Text;

    PropertyValue(PayloadFormat format, long whole, double real, bool truth, string text)
    {
        Format = format; Whole = whole; Real = real; Truth = truth; Text = text;
    }

    /// <summary>A tag: a property that says only that it applies.</summary>
    public static PropertyValue None
    {
        get { return new PropertyValue(PayloadFormat.None, 0L, 0d, false, null); }
    }

    public static PropertyValue Of(long value)
    {
        return new PropertyValue(PayloadFormat.Int64, value, 0d, false, null);
    }

    public static PropertyValue Of(double value)
    {
        return new PropertyValue(PayloadFormat.Float64, 0L, value, false, null);
    }

    public static PropertyValue Of(bool value)
    {
        return new PropertyValue(PayloadFormat.Bool, 0L, 0d, value, null);
    }

    public static PropertyValue Of(string value)
    {
        return new PropertyValue(PayloadFormat.Utf8, 0L, 0d, false, value ?? string.Empty);
    }

    public bool IsBare
    {
        get { return Format == PayloadFormat.None; }
    }
}

/// <summary>
/// Optional valued tags. A property references a tag declaration and stores a payload-format
/// byte followed by the encoded primitive value. Applications define each tag's meaning,
/// including whether it affects display, execution or another behavior.
/// </summary>
public static class PropertyKinds
{
    /// <summary>ASCII "Superscript_prop".</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F70726F70UL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Library, name);
    }

    /// <summary>A tag declaration whose UTF-8 payload is its name.</summary>
    public static readonly QualifiedName Tag = N("tag");

    /// <summary>A tag applied to the node it is a child of, with a value. Payload is published below.</summary>
    public static readonly QualifiedName Property = N("property");

    /// <summary>Which word this property is using. Exactly one per property.</summary>
    public static readonly QualifiedName Tagged = N("tagged");

    public struct Ids
    {
        public KindId Tag;
        public KindId Property;
        public RelationId Tagged;
    }

    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.Tagged = KindTable.InternRelation(ref table, Tagged);

        ids.Tag = KindTable.Intern(ref table, Tag);
        ids.Property = KindTable.Intern(ref table, Property);

        // A tag is a named declaration.
        KindTable.DefineSchema(ref table, ids.Tag,
            StructuralFact.Declaration | StructuralFact.Annotation,
            PayloadFormat.Utf8, DisplayShape.Leaf,
            0, default);

        // The record stores a format byte followed by the primitive value.
        KindTable.DefineSchema(ref table, ids.Property,
            StructuralFact.Annotation, PayloadFormat.Record, DisplayShape.Leaf,
            0, new RelationId[] { ids.Tagged });

        return ids;
    }

    /// <summary>Encodes a format byte followed by the value in that format.</summary>
    public static byte[] Encode(in PropertyValue value)
    {
        switch (value.Format)
        {
            case PayloadFormat.Int64: return Framed(PayloadFormat.Int64, PayloadCodec.WriteInt64(value.Whole));
            case PayloadFormat.Float64: return Framed(PayloadFormat.Float64, PayloadCodec.WriteFloat64(value.Real));
            case PayloadFormat.Bool: return Framed(PayloadFormat.Bool, PayloadCodec.WriteBool(value.Truth));
            case PayloadFormat.Utf8: return Framed(PayloadFormat.Utf8, PayloadCodec.WriteUtf8(value.Text));
            default: return new byte[] { (byte)PayloadFormat.None };
        }
    }

    static byte[] Framed(PayloadFormat format, byte[] value)
    {
        byte[] bytes = new byte[1 + value.Length];
        bytes[0] = (byte)format;
        Array.Copy(value, 0, bytes, 1, value.Length);
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out PropertyValue value)
    {
        value = PropertyValue.None;
        if (payload.Length == 0) { return false; }

        ReadOnlySpan<byte> rest = payload.Slice(1);

        switch ((PayloadFormat)payload[0])
        {
            case PayloadFormat.None: value = PropertyValue.None; return true;
            case PayloadFormat.Int64: value = PropertyValue.Of(PayloadCodec.ReadInt64(rest)); return true;
            case PayloadFormat.Float64: value = PropertyValue.Of(PayloadCodec.ReadFloat64(rest)); return true;
            case PayloadFormat.Bool: value = PropertyValue.Of(PayloadCodec.ReadBool(rest)); return true;
            case PayloadFormat.Utf8: value = PropertyValue.Of(PayloadCodec.ReadUtf8(rest)); return true;

            // Unknown value formats remain in the source but cannot be decoded here.
            default: value = PropertyValue.None; return false;
        }
    }

    /// <summary>Declare a word of vocabulary under <paramref name="parent"/>.</summary>
    public static NodeId DeclareTag(ref Source src, in Ids ids, NodeId parent, string word)
    {
        NodeId node = SourceBuild.AddChild(ref src, parent, ids.Tag);
        SourceBuild.SetPayload(ref src, node, Encoding.UTF8.GetBytes(word ?? string.Empty));
        return node;
    }

    /// <summary>The word a tag node holds, or the empty string.</summary>
    public static string WordOf(in Source src, NodeId tag)
    {
        if (tag.IsNone || !src.IsLive(tag)) { return string.Empty; }
        return PayloadCodec.ReadUtf8(SourceWalk.Payload(in src, tag));
    }

    /// <summary>
    /// Sets the property identified by owner and tag, replacing an existing value rather than
    /// adding a duplicate.
    /// </summary>
    public static NodeId Set(ref Source src, in Ids ids, NodeId owner, NodeId tag, in PropertyValue value)
    {
        NodeId found = Find(in src, in ids, owner, tag);

        if (found.IsNone)
        {
            found = SourceBuild.AddChild(ref src, owner, ids.Property);
            SourceBuild.AddRef(ref src, found, ids.Tagged, tag);
        }

        SourceBuild.SetPayload(ref src, found, Encode(in value));
        return found;
    }

    /// <summary>What <paramref name="owner"/> says under <paramref name="tag"/>, if anything.</summary>
    public static bool TryRead(in Source src, in Ids ids, NodeId owner, NodeId tag, out PropertyValue value)
    {
        value = PropertyValue.None;

        NodeId found = Find(in src, in ids, owner, tag);
        if (found.IsNone) { return false; }

        // A property with no payload at all is a bare tag, not an absence: the word applies.
        ReadOnlySpan<byte> payload = SourceWalk.Payload(in src, found);
        if (payload.Length == 0) { return true; }

        TryDecode(payload, out value);
        return true;
    }

    /// <summary>Stop saying it. Answers whether there was anything to stop saying.</summary>
    public static bool Clear(ref Source src, in Ids ids, NodeId owner, NodeId tag)
    {
        NodeId found = Find(in src, in ids, owner, tag);
        if (found.IsNone) { return false; }

        SourceBuild.Delete(ref src, found);
        return true;
    }

    /// <summary>Reads the kind’s Annotation schema fact.</summary>
    public static bool IsAnnotation(in KindTable table, KindId kind)
    {
        return (KindTable.SchemaOf(in table, kind).Facts & StructuralFact.Annotation) != 0;
    }

    /// <summary>Finds a property by tag identity, so tags with identical names remain distinct.</summary>
    public static NodeId Find(in Source src, in Ids ids, NodeId owner, NodeId tag)
    {
        if (owner.IsNone || !src.IsLive(owner)) { return NodeId.None; }
        if (tag.IsNone || !src.IsLive(tag)) { return NodeId.None; }

        int count = SourceWalk.ChildCount(in src, owner);

        for (int i = 0; i < count; i++)
        {
            NodeId child = SourceWalk.ChildAt(in src, owner, i);
            if (child.IsNone || !src.IsLive(child)) { continue; }
            if (!src.Nodes[child.Index].Kind.Equals(ids.Property)) { continue; }

            NodeId said = SourceWalk.Target(in src, child, ids.Tagged);
            if (said.Equals(tag)) { return child; }
        }

        return NodeId.None;
    }
}
