using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Relate;

/// <summary>Optional relations for annotations, spans, embeddings and provenance between content nodes.</summary>
public static class RelateKinds
{
    /// <summary>ASCII "Superscript_reln". On-disk identity; stale name kept on purpose.</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F72656C6EUL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Library, name);
    }

    /// <summary>
    /// Ranges in the parent’s payload, encoded by SpanCodec. References on the span describe what
    /// the ranges relate to.
    /// </summary>
    public static readonly QualifiedName Span = N("span");

    /// <summary>A named tag declaration referenced through Tagged.</summary>
    public static readonly QualifiedName Tag = N("tag");

    /// <summary>
    /// Show another node here. The target is an <see cref="Embeds"/> edge. An embedded
    /// function is the function, not a copy, so it cannot go stale.
    /// </summary>
    public static readonly QualifiedName Embed = N("embed");

    /// <summary>
    /// Bytes an importer could not model, kept verbatim. An importer that silently drops
    /// what it did not understand is data loss on save.
    /// </summary>
    public static readonly QualifiedName Original = N("original");

    /// <summary>Annotation → the node it describes.</summary>
    public static readonly QualifiedName About = N("about");

    /// <summary>Anything -> a <see cref="Tag"/>. May repeat.</summary>
    public static readonly QualifiedName Tagged = N("tagged");

    /// <summary>embed -> the node it shows.</summary>
    public static readonly QualifiedName Embeds = N("embeds");

    /// <summary>Cross-reference → its target. Applications choose its rendered label.</summary>
    public static readonly QualifiedName RefersTo = N("refers-to");

    /// <summary>Imported structure -> the bytes it came from.</summary>
    public static readonly QualifiedName ImportedFrom = N("imported-from");
}

/// <summary>Interned handles for the relation library, valid for one KindTable.</summary>
public struct RelateIds
{
    public KindId Span, Tag, Embed, Original;
    public RelationId About, Tagged, Embeds, RefersTo, ImportedFrom;
}

/// <summary>
/// Installs the shared relation handles and kind schemas before a caller defines schemas that use
/// them.
/// </summary>
public static class RelateLibrary
{
    public static RelateIds Install(ref KindTable table)
    {
        RelateIds ids;

        ids.About = KindTable.InternRelation(ref table, RelateKinds.About);
        ids.Tagged = KindTable.InternRelation(ref table, RelateKinds.Tagged);
        ids.Embeds = KindTable.InternRelation(ref table, RelateKinds.Embeds);
        ids.RefersTo = KindTable.InternRelation(ref table, RelateKinds.RefersTo);
        ids.ImportedFrom = KindTable.InternRelation(ref table, RelateKinds.ImportedFrom);

        ids.Span = KindTable.Intern(ref table, RelateKinds.Span);
        ids.Tag = KindTable.Intern(ref table, RelateKinds.Tag);
        ids.Embed = KindTable.Intern(ref table, RelateKinds.Embed);
        ids.Original = KindTable.Intern(ref table, RelateKinds.Original);

        // Range order is not semantic. The payload declares a three-byte header and ten-byte
        // elements so generic readers can count and diff ranges.
        Define(ref table, ids.Span,
            StructuralFact.None,
            PayloadFormat.Record, DisplayShape.Inline, 0,
            new RelationId[] { ids.Tagged, ids.RefersTo, ids.About },
            PayloadStride.Of(SpanCodec.HeaderBytes, SpanCodec.EntryBytes));

        // A declaration, so that renaming it is one edit and `ss refs` answers "everything
        // tagged this" without an index living beside the sources.
        Define(ref table, ids.Tag,
            StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Leaf, 0,
            new RelationId[] { ids.About, ids.Tagged });

        // The embedding’s target is carried by its reference.
        Define(ref table, ids.Embed,
            StructuralFact.None,
            PayloadFormat.None, DisplayShape.Block, 0,
            new RelationId[] { ids.Embeds });

        // The original bytes remain opaque but can still be preserved and referenced.
        Define(ref table, ids.Original,
            StructuralFact.InteriorOpaque,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0,
            new RelationId[] { ids.ImportedFrom });

        return ids;
    }

    static void Define(
        ref KindTable table,
        KindId kind,
        StructuralFact facts,
        PayloadFormat payload,
        DisplayShape shape,
        uint color,
        RelationId[] uses,
        PayloadStride stride = default)
    {
        KindTable.DefineSchema(ref table, kind, facts, payload, shape, color,
            uses == null ? default : new ReadOnlySpan<RelationId>(uses), stride);
    }
}

/// <summary>One range of a text payload, and which of the span's edges it means.</summary>
public struct SpanRange
{
    /// <summary>Byte offset into the parent's UTF-8 payload. Bytes, never characters.</summary>
    public int Offset;

    /// <summary>Byte length. Zero is legal and marks a point rather than a range.</summary>
    public int Length;

    /// <summary>
    /// Index into the span node’s reference array, or NoEdge. Multiple ranges may share one
    /// reference.
    /// </summary>
    public ushort Edge;

    public const ushort NoEdge = 0xFFFF;
}

/// <summary>
/// Little-endian span payload. Offsets and lengths count bytes, not characters. Truncated payloads
/// return only complete ranges; readers convert text positions at their own boundary.
///
/// <code>
/// u8   version   = 1
/// u16  count     number of ranges
/// then count times:
///   i32  offset  byte offset into the parent's payload
///   i32  length  byte length
///   u16  edge    index into this node's refs, or 0xFFFF
/// </code>
/// </summary>
public static class SpanCodec
{
    public const byte Version = 1;

    /// <summary>Sizes shared by the span codec and its schema stride declaration.</summary>
    public const ushort HeaderBytes = 1 + 2;
    public const ushort EntryBytes = 4 + 4 + 2;

    const int Header = HeaderBytes;
    const int Entry = EntryBytes;

    /// <summary>How many ranges the payload claims, bounded by how many it can actually hold.</summary>
    public static int Count(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Header || payload[0] != Version)
        {
            return 0;
        }

        int claimed = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2));
        int fits = (payload.Length - Header) / Entry;
        return claimed < fits ? claimed : fits;
    }

    /// <summary>Reads complete ranges into a reusable buffer, growing it as needed. Returns the number read.</summary>
    public static int Read(ReadOnlySpan<byte> payload, ref SpanRange[] into)
    {
        int count = Count(payload);

        if (into == null)
        {
            into = new SpanRange[count < 4 ? 4 : count];
        }
        else if (into.Length < count)
        {
            Array.Resize(ref into, count);
        }

        for (int i = 0; i < count; i++)
        {
            int at = Header + (i * Entry);
            into[i].Offset = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(at, 4));
            into[i].Length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(at + 4, 4));
            into[i].Edge = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 8, 2));
        }

        return count;
    }

    public static byte[] Write(ReadOnlySpan<SpanRange> ranges)
    {
        byte[] buffer = new byte[Header + (ranges.Length * Entry)];

        buffer[0] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(
            new Span<byte>(buffer, 1, 2), (ushort)ranges.Length);

        for (int i = 0; i < ranges.Length; i++)
        {
            int at = Header + (i * Entry);
            BinaryPrimitives.WriteInt32LittleEndian(
                new Span<byte>(buffer, at, 4), ranges[i].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(
                new Span<byte>(buffer, at + 4, 4), ranges[i].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(
                new Span<byte>(buffer, at + 8, 2), ranges[i].Edge);
        }

        return buffer;
    }

    /// <summary>
    /// Adjusts a range after replacing removed bytes at at with inserted bytes. Ranges after the
    /// edit shift; ranges before it stay fixed; containing ranges resize; overlapping ranges clamp
    /// to surviving bytes. Insertion at the start shifts the range, while insertion at the end
    /// extends it.
    /// </summary>
    public static SpanRange Shifted(SpanRange range, int at, int removed, int inserted)
    {
        int start = Moved(range.Offset, at, removed, inserted);
        int end = Moved(range.Offset + range.Length, at, removed, inserted);

        range.Offset = start;
        range.Length = end > start ? end - start : 0;
        return range;
    }

    /// <summary>
    /// Offsets at or after the removed region move with the following text. Offsets inside it
    /// collapse to the edit start. Test the end boundary first so insertion-boundary offsets move
    /// too.
    /// </summary>
    static int Moved(int offset, int at, int removed, int inserted)
    {
        int over = at + removed;
        if (offset >= over)
        {
            return offset + (inserted - removed);
        }
        return offset <= at ? offset : at;
    }

    public static int At(ReadOnlySpan<SpanRange> ranges, int offset)
    {
        int found = -1;
        for (int i = 0; i < ranges.Length; i++)
        {
            int start = ranges[i].Offset;
            int end = start + ranges[i].Length;
            if (offset >= start && (offset < end || (ranges[i].Length == 0 && offset == start)))
            {
                found = i;
            }
        }
        return found;
    }
}

