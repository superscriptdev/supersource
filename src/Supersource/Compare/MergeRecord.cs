using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Compare;

/// <summary>Interned handles for the merge library, valid for one KindTable.</summary>
public struct MergeIds
{
    public KindId Merge, MergeNote;
    public RelationId Decided;
}

/// <summary>Kinds and relations used to store merge decisions as nodes in the resulting source.</summary>
public static class MergeKinds
{
    /// <summary>ASCII "Superscript_merg". On-disk identity; the stale prefix is kept on purpose.</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F6D657267UL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Library, name);
    }

    /// <summary>Container for merge notes. No container is written when there are no decisions.</summary>
    public static readonly QualifiedName Merge = N("merge");

    /// <summary>A decision encoded by MergeRecord, with a Decided edge naming its subject.</summary>
    public static readonly QualifiedName MergeNote = N("merge-note");

    /// <summary>merge-note -> the node it decided.</summary>
    public static readonly QualifiedName Decided = N("decided");

    public static MergeIds Install(ref KindTable table)
    {
        MergeIds ids;
        ids.Decided = KindTable.InternRelation(ref table, Decided);
        ids.Merge = KindTable.Intern(ref table, Merge);
        ids.MergeNote = KindTable.Intern(ref table, MergeNote);

        // Retain the order in which the merge produced its notes.
        KindTable.DefineSchema(
            ref table, ids.Merge,
            StructuralFact.ChildOrderSignificant,
            PayloadFormat.None,
            DisplayShape.Block,
            0xCC9944FF,
            default);

        KindTable.DefineSchema(
            ref table, ids.MergeNote,
            StructuralFact.None,
            PayloadFormat.Record,
            DisplayShape.Leaf,
            0xCCAA66FF,
            new RelationId[] { ids.Decided });

        return ids;
    }
}

/// <summary>
/// Encodes merge decisions as nodes. Recording is explicit and separate from Merge, allowing
/// previews without adding content. Payload fields are little-endian.
///
/// <code>
/// merge
/// └── merge-note   payload: facet(u8) resolution(u8) detailLen(u16) detail(utf8)
///     └── refs: decided -> the node this is about
/// </code>
/// </summary>
public static class MergeRecord
{
    const int Fixed = 1 + 1 + 2;

    /// <summary>Writes one note per decision under parent. Returns None when there are no decisions.</summary>
    public static NodeId Write(
        ref Source src,
        in MergeIds ids,
        NodeId parent,
        MergeNote[] notes,
        int count)
    {
        if (count <= 0)
        {
            return NodeId.None;
        }

        NodeId merge = SourceBuild.AddChild(ref src, parent, ids.Merge);

        for (int i = 0; i < count; i++)
        {
            NodeId note = SourceBuild.AddChild(ref src, merge, ids.MergeNote);
            SourceBuild.SetPayload(ref src, note, Encode(in notes[i]));

            // Keep the subject reference even if that node is later deleted.
            if (notes[i].Node.IsSome)
            {
                SourceBuild.AddRef(ref src, note, ids.Decided, notes[i].Node);
            }
        }

        return merge;
    }

    /// <summary>
    /// Read the notes a merge node holds. Returns how many were written into
    /// <paramref name="into"/>.
    /// </summary>
    public static int Read(
        in Source src,
        in MergeIds ids,
        NodeId merge,
        ref MergeNote[] into)
    {
        if (into == null)
        {
            into = new MergeNote[16];
        }

        if (!src.IsLive(merge) || src.Nodes[merge.Index].Kind != ids.Merge)
        {
            return 0;
        }

        int found = 0;
        ReadOnlySpan<long> children = SourceWalk.Children(in src, merge);

        for (int i = 0; i < children.Length; i++)
        {
            NodeId note = new NodeId(children[i]);
            if (!src.IsLive(note) || src.Nodes[note.Index].Kind != ids.MergeNote)
            {
                continue;
            }

            if (!Decode(SourceWalk.Payload(in src, note), out MergeNote read))
            {
                continue;
            }

            read.Node = SourceWalk.Target(in src, note, ids.Decided);

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = read;
            found++;
        }

        return found;
    }

    /// <summary>Every merge recorded in this source, oldest first by slot.</summary>
    public static int Merges(in Source src, in MergeIds ids, ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[8];
        }

        int found = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != ids.Merge)
            {
                continue;
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 8 : into.Length * 2);
            }
            into[found] = node;
            found++;
        }
        return found;
    }

    public static byte[] Encode(in MergeNote note)
    {
        byte[] detail = Encoding.UTF8.GetBytes(note.Detail ?? string.Empty);
        byte[] buffer = new byte[Fixed + detail.Length];

        buffer[0] = (byte)note.Facet;
        buffer[1] = (byte)note.How;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, 2, 2), (ushort)detail.Length);
        Array.Copy(detail, 0, buffer, Fixed, detail.Length);
        return buffer;
    }

    /// <summary>Decodes a merge note, returning false for truncated input.</summary>
    public static bool Decode(ReadOnlySpan<byte> payload, out MergeNote note)
    {
        note = default;

        if (payload.Length < Fixed)
        {
            return false;
        }

        note.Facet = (Facet)payload[0];
        note.How = (Decision)payload[1];

        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (Fixed + length > payload.Length)
        {
            return false;
        }

        note.Detail = length == 0 ? string.Empty : Encoding.UTF8.GetString(payload.Slice(Fixed, length));
        return true;
    }
}
