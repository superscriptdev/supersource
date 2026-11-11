using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Format;

/// <summary>
/// The content hash of a source: a SHA-256 over its state rather than its bytes, so two files
/// that read as the same graph have the same hash however they were saved — appended to or
/// written in full, in this format version or an older one. A pin names a version of a source
/// by this hash. The checksum stays over bytes, because it answers whether a file changed.
///
/// What is hashed, in order:
///
/// <code>
/// "supersource state 1"
/// source id (16) · root (svar)
/// every live node, in id order:
///   id (uvar) · kind name · payload (uvar length, bytes)
///   children (uvar count, uvar ids)
///   references (uvar count; per edge: relation name, svar target or -1, uvar length, data)
/// every kind a live node has, ordered by name: name · u8 has a schema (1 or 0), and when it has:
///   facts (u16) · format · uvar count · the relation names it uses, ordered by name
/// every relation an edge has, ordered by name: name · format
///
/// a name    library (16) · uvar length · utf8
/// a format  as the schema section writes it
/// </code>
///
/// Names rather than handles, and sorted, so the hash does not depend on interning order.
/// Unknown sections are not part of it: what this build cannot read is not part of what it can
/// say a source holds. The encoding is fixed: changing it changes every pin.
/// </summary>
public static class SourceHash
{
    static readonly byte[] Tag = Encoding.ASCII.GetBytes("supersource state 1");

    public static ContentHash Of(in SourceFile file)
    {
        return Of(in file.Src, in file.Table);
    }

    /// <summary>The content hash of the source a file holds. Reads it; does not keep it open.</summary>
    public static ContentHash OfFile(string path)
    {
        SourceFile file = SourceRead.FromBytes(File.ReadAllBytes(path));
        return Of(in file);
    }

    public static ContentHash Of(in Source src, in KindTable table)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ByteWriter w = ByteWriter.Over(Stream.Null);
        w.Hash = digest;
        w.Hashing = true;

        w.Bytes(Tag);
        w.U64(src.Identity.Hi);
        w.U64(src.Identity.Lo);
        w.SVar(src.Root.Index);

        byte[] window = null;
        bool[] kinds = new bool[table.KindCount];
        bool[] relations = new bool[table.RelationCount];

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            KindId kind = src.Nodes[i].Kind;
            Mark(kinds, kind.Value);

            w.UVar((ulong)i);
            Name(ref w, KindTable.NameOf(in table, kind));

            long length = SourceWalk.PayloadLength(in src, node);
            w.UVar((ulong)length);
            if (SourceWalk.IsContiguous(in src, node))
            {
                w.Bytes(SourceWalk.Payload(in src, node));
            }
            else
            {
                // A payload of any size is hashed in windows, the same bytes in the same order.
                window ??= new byte[SourceWalk.Window];
                for (long at = 0; at < length; at += window.Length)
                {
                    int count = (int)Math.Min(window.Length, length - at);
                    SourceWalk.ReadPayload(in src, node, at, new Span<byte>(window, 0, count));
                    w.Bytes(new ReadOnlySpan<byte>(window, 0, count));
                }
            }

            ReadOnlySpan<long> children = SourceWalk.Children(in src, node);
            w.UVar((ulong)children.Length);
            for (int c = 0; c < children.Length; c++)
            {
                w.UVar((ulong)children[c]);
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
            w.UVar((ulong)refs.Length);
            for (int r = 0; r < refs.Length; r++)
            {
                Mark(relations, refs[r].Relation.Value);
                Name(ref w, KindTable.RelationNameOf(in table, refs[r].Relation));
                w.SVar(refs[r].Target.Index);
                ReadOnlySpan<byte> data = SourceWalk.EdgeData(in src, in refs[r]);
                w.UVar((ulong)data.Length);
                w.Bytes(data);
            }
        }

        int[] kindOrder = Sorted(kinds, table.KindNames);
        for (int k = 0; k < kindOrder.Length; k++)
        {
            KindId kind = new KindId(kindOrder[k]);
            Name(ref w, KindTable.NameOf(in table, kind));
            bool has = KindTable.HasSchema(in table, kind);
            w.U8(has ? (byte)1 : (byte)0);
            if (!has)
            {
                continue;
            }

            KindSchema schema = KindTable.SchemaOf(in table, kind);
            w.U16((ushort)schema.Facts);
            SchemaSection.WriteFormat(ref w, in schema.Payload);

            ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in table, kind);
            QualifiedName[] names = new QualifiedName[used.Length];
            for (int r = 0; r < used.Length; r++)
            {
                names[r] = KindTable.RelationNameOf(in table, used[r]);
            }
            Array.Sort(names, Compare);
            w.UVar((ulong)names.Length);
            for (int r = 0; r < names.Length; r++)
            {
                Name(ref w, names[r]);
            }
        }

        int[] relationOrder = Sorted(relations, table.RelationNames);
        for (int r = 0; r < relationOrder.Length; r++)
        {
            RelationId relation = new RelationId(relationOrder[r]);
            Name(ref w, KindTable.RelationNameOf(in table, relation));
            RelationSchema carries = KindTable.RelationSchemaOf(in table, relation);
            SchemaSection.WriteFormat(ref w, in carries.Payload);
        }

        w.Flush();
        Span<byte> result = stackalloc byte[32];
        digest.GetHashAndReset(result);
        return ContentHash.FromDigest(result);
    }

    static void Mark(bool[] used, int handle)
    {
        if (handle >= 0 && handle < used.Length)
        {
            used[handle] = true;
        }
    }

    static void Name(ref ByteWriter w, QualifiedName name)
    {
        w.U64(name.Library.Hi);
        w.U64(name.Library.Lo);
        byte[] text = Encoding.UTF8.GetBytes(name.Name ?? string.Empty);
        w.UVar((ulong)text.Length);
        w.Bytes(text);
    }

    static int[] Sorted(bool[] used, QualifiedName[] names)
    {
        int count = 0;
        for (int i = 0; i < used.Length; i++)
        {
            if (used[i]) count++;
        }

        int[] order = new int[count];
        int at = 0;
        for (int i = 0; i < used.Length; i++)
        {
            if (used[i]) order[at++] = i;
        }

        Array.Sort(order, (a, b) => Compare(names[a], names[b]));
        return order;
    }

    static int Compare(QualifiedName a, QualifiedName b)
    {
        int byLibrary = a.Library.Hi.CompareTo(b.Library.Hi);
        if (byLibrary != 0) return byLibrary;
        byLibrary = a.Library.Lo.CompareTo(b.Library.Lo);
        if (byLibrary != 0) return byLibrary;
        return string.CompareOrdinal(a.Name, b.Name);
    }
}
