using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Supersource.Kinds;

namespace Supersource.Format;
/// <summary>
/// Serializes names in first-use order and nodes in slot order, preserving child and reference
/// order. Arena offsets and capacities are rebuilt on load. Unknown sections retain their bytes and
/// position.
/// </summary>
public static class SourceWrite
{
    /// <summary>Signature with CR, LF and SUB bytes to detect common text-transfer damage.</summary>
    public static readonly byte[] Magic = new byte[] { 0x53, 0x53, 0x53, 0x52, 0x43, 0x0D, 0x0A, 0x1A };

    /// <summary>
    /// Current major format version. Version 4 adds journals, so the node section describes the
    /// state before replay. Readers also support the earlier major versions.
    /// </summary>
    public const ushort FormatMajor = 4;
    /// <summary>Current minor format version. Compatible additions use new length-framed sections.</summary>
    public const ushort FormatMinor = 0;

    /// <summary>
    /// Serializes to a byte array. Use <see cref="ToStream(in SourceFile, Stream)"/> for sources
    /// too large for an array.
    /// </summary>
    public static byte[] ToBytes(in SourceFile file)
    {
        using (MemoryStream into = new MemoryStream())
        {
            ToStream(in file, into);
            return into.ToArray();
        }
    }

    /// <summary>Write a source to a file, without ever holding all of it at once.</summary>
    public static void ToFile(in SourceFile file, string path)
    {
        using (FileStream sink = new FileStream(
            path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, WindowBytes))
        {
            ToStream(in file, sink);
        }
    }

    /// <summary>How much of the output is buffered before it is handed to the sink.</summary>
    public const int WindowBytes = 1 << 16;

    /// <summary>Digest and name-slot mappings returned by a write for subsequent incremental saves.</summary>
    public struct Written
    {
        public Kinds.ContentHash Digest;
        public uint[] Order;
        public int[] KindSlot;
        public int[] RelationSlot;
    }

    /// <summary>
    /// Saves in place, refusing when a known stored digest has changed. Appends a journal when
    /// possible; otherwise writes a sibling temporary file and replaces the destination. Files
    /// without a digest cannot be checked for stale saves. Does not provide concurrent-writer
    /// isolation or crash durability.
    /// </summary>
    public static bool Replace(ref SourceFile file, string path, out string refusal)
    {
        refusal = null;

        if (file.DigestKind != DigestKind.None && File.Exists(path))
        {
            ContentHash now = SourceRead.DigestOf(path);
            if (!now.IsNone && !now.Equals(file.Digest))
            {
                refusal = "the file has been written since it was read";
                return false;
            }
        }

        // Use the append path only when logged edits cover the change.
        if (Append(ref file, path))
        {
            return true;
        }

        string beside = path + ".writing";
        try
        {
            Written written;
            using (FileStream sink = new FileStream(
                beside, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, WindowBytes))
            {
                ToStream(in file, sink, out written);
            }

            File.Move(beside, path, true);
            Saved(ref file, in written);
            return true;
        }
        finally
        {
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }
    }

    /// <summary>What a source knows about itself once it is on disk.</summary>
    static void Saved(ref SourceFile file, in Written written)
    {
        file.Digest = written.Digest;
        file.DigestKind = written.Digest.IsNone ? DigestKind.None : DigestKind.Sha256;
        file.SectionOrder = written.Order;
        file.SectionOrderCount = written.Order.Length;
        file.KindSlot = written.KindSlot;
        file.RelationSlot = written.RelationSlot;
        file.SavedAt = file.History.At;
        file.SavedStamp = file.Src.Stamp;
    }

    /// <summary>
    /// Appends logged edits when history has advanced from the saved position, stamps form an
    /// unbroken chain, and all operation names already have file slots. Returns false to request a
    /// full rewrite otherwise.
    /// </summary>
    static bool Append(ref SourceFile file, string path)
    {
        if (file.DigestKind == DigestKind.None
            || file.SectionOrderCount == 0
            || file.KindSlot == null
            || !File.Exists(path))
        {
            return false;
        }

        int from = file.SavedAt;
        int to = file.History.At;
        if (to < from)
        {
            return false;
        }

        // With no new entries, a changed stamp requires a full rewrite.
        if (to == from)
        {
            return file.Src.Stamp == file.SavedStamp;
        }

        ulong stamp = file.SavedStamp;
        for (int i = from; i < to; i++)
        {
            if (file.History.Entries[i].StampBefore != stamp)
            {
                return false;
            }
            stamp = file.History.Entries[i].StampAfter;
        }

        if (stamp != file.Src.Stamp)
        {
            return false;
        }

        for (int i = from; i < to; i++)
        {
            int kind = file.History.Entries[i].Op.NodeKind.Value;
            if (kind >= 0 && (kind >= file.KindSlot.Length || file.KindSlot[kind] < 0))
            {
                return false;
            }

            int relation = file.History.Entries[i].Op.Relation.Value;
            if (relation >= 0 && (relation >= file.RelationSlot.Length || file.RelationSlot[relation] < 0))
            {
                return false;
            }
        }

        using (FileStream sink = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WindowBytes))
        {
            long contentAt;
            long end;

            sink.Position = sink.Length;
            {
                ByteWriter w = ByteWriter.Over(sink, WindowBytes);
                w.U32((uint)SectionId.Journal);
                w.U64(0);
                w.Flush();

                contentAt = sink.Position;
                HistoryCodec.WriteJournal(ref w, in file.History, from, to, file.KindSlot, file.RelationSlot);
                w.Flush();
                end = sink.Position;
            }

            Span<byte> patch = stackalloc byte[8];
            sink.Position = contentAt - 8;
            BinaryPrimitives.WriteUInt64LittleEndian(patch, (ulong)(end - contentAt));
            sink.Write(patch);

            // Update the section count after appending the journal.
            sink.Position = Magic.Length + 2 + 2;
            BinaryPrimitives.WriteUInt32LittleEndian(patch.Slice(0, 4), (uint)(file.SectionOrderCount + 1));
            sink.Write(patch.Slice(0, 4));
        }

        if (file.SectionOrderCount == file.SectionOrder.Length)
        {
            Array.Resize(ref file.SectionOrder, file.SectionOrderCount + 4);
        }
        file.SectionOrder[file.SectionOrderCount] = (uint)SectionId.Journal;
        file.SectionOrderCount++;

        file.Digest = SourceRead.DigestOf(path);
        file.SavedAt = to;
        file.SavedStamp = file.Src.Stamp;
        return true;
    }

    /// <summary>Writes to a seekable stream, patching section lengths after their content has been streamed.</summary>
    public static void ToStream(in SourceFile file, Stream sink)
    {
        Written written;
        ToStream(in file, sink, out written);
    }

    /// <summary>As above, saying what it wrote — see <see cref="Written"/>.</summary>
    public static void ToStream(in SourceFile file, Stream sink, out Written written)
    {
        written = default;
        if (!sink.CanSeek)
        {
            throw new InvalidOperationException(
                "a section's length is written before its content, so writing needs a stream that can seek");
        }

        // First use order, so the tables are a function of the graph and nothing else.
        int[] kindOrder;
        int[] kindSlot;
        int[] relationOrder;
        int[] relationSlot;
        CollectNames(in file, out kindOrder, out kindSlot, out relationOrder, out relationSlot);

        // Hash section contents, excluding framing headers whose lengths are patched after
        // writing.
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        ByteWriter w = ByteWriter.Over(sink, WindowBytes);
        w.Hash = digest;
        w.Bytes(Magic);
        w.U16(FormatMajor);
        w.U16(FormatMinor);

        uint[] order = SectionOrder(in file);
        w.U32((uint)order.Length);

        written.Order = order;
        written.KindSlot = kindSlot;
        written.RelationSlot = relationSlot;

        long digestAt = -1;
        int nextUnknown = 0;

        // One buffer for every section's length, patched in place after each is written.
        Span<byte> length = stackalloc byte[8];

        for (int i = 0; i < order.Length; i++)
        {
            uint id = order[i];

            w.U32(id);
            w.Flush();

            long lengthAt = sink.Position;
            w.U64(0);
            w.Flush();
            long contentAt = sink.Position;

            // Its own bytes are the one thing a digest cannot cover.
            w.Hashing = (SectionId)id != SectionId.Checksum;

            switch ((SectionId)id)
            {
                case SectionId.Identity:
                    w.Bytes(WriteIdentity(in file));
                    break;
                case SectionId.KindNames:
                    w.Bytes(WriteKindNames(in file, kindOrder));
                    break;
                case SectionId.RelationNames:
                    w.Bytes(WriteRelationNames(in file, relationOrder));
                    break;
                case SectionId.Nodes:
                    WriteNodes(ref w, in file, kindSlot, relationSlot);
                    break;
                case SectionId.Checksum:
                    // Written blank and filled in below, once there is something to say.
                    w.U8((byte)DigestKind.Sha256);
                    w.Bytes(new byte[32]);
                    break;
                case SectionId.History:
                {
                    // Preserve unsupported history bytes instead of writing an empty history over
                    // them.
                    byte[] kept = Preserved(in file, id);
                    if (kept != null)
                    {
                        w.Bytes(kept);
                    }
                    else
                    {
                        HistoryCodec.Write(ref w, in file, kindSlot, relationSlot);
                    }
                    break;
                }
                default:
                    w.Bytes(NextUnknown(in file, id, ref nextUnknown));
                    break;
            }

            w.Flush();
            w.Hashing = false;

            long end = sink.Position;

            if ((SectionId)id == SectionId.Checksum)
            {
                digestAt = contentAt + 1;
            }

            sink.Position = lengthAt;
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)(end - contentAt));
            sink.Write(length);

            sink.Position = end;
        }

        if (digestAt >= 0)
        {
            // Write the accumulated content digest last.
            long end = sink.Position;
            byte[] computed = digest.GetCurrentHash();
            sink.Position = digestAt;
            sink.Write(computed);
            sink.Position = end;

            ByteReader over = ByteReader.Over(computed);
            written.Digest = new Kinds.ContentHash(over.U64(), over.U64(), over.U64(), over.U64());
        }
    }

    static uint[] SectionOrder(in SourceFile file)
    {
        bool history = file.History.At > 0;

        if (file.SectionOrderCount > 0)
        {
            // Omit journals from full rewrites: their operations are already reflected in the
            // graph and history.
            int kept = 0;
            bool hasHistory = false;
            for (int i = 0; i < file.SectionOrderCount; i++)
            {
                hasHistory = hasHistory || file.SectionOrder[i] == (uint)SectionId.History;
                if (file.SectionOrder[i] != (uint)SectionId.Journal)
                {
                    kept++;
                }
            }

            if (kept < file.SectionOrderCount)
            {
                uint[] folded = new uint[kept];
                int at = 0;
                for (int i = 0; i < file.SectionOrderCount; i++)
                {
                    if (file.SectionOrder[i] != (uint)SectionId.Journal)
                    {
                        folded[at] = file.SectionOrder[i];
                        at++;
                    }
                }

                uint[] grown = new uint[kept + (history && !hasHistory ? 1 : 0)];
                Array.Copy(folded, grown, kept);
                if (grown.Length > kept)
                {
                    grown[kept] = (uint)SectionId.History;
                }
                return grown;
            }

            // Add a history section if edits created one. Preserve the absence of a checksum;
            // explicit upgrade can request the current section layout.
            int extra = history && !hasHistory ? 1 : 0;
            uint[] preserved = new uint[file.SectionOrderCount + extra];
            Array.Copy(file.SectionOrder, preserved, file.SectionOrderCount);

            if (extra > 0)
            {
                preserved[file.SectionOrderCount] = (uint)SectionId.History;
            }
            return preserved;
        }

        int known = history ? 5 : 4;
        uint[] canonical = new uint[known + 1 + file.UnknownCount];
        canonical[0] = (uint)SectionId.Identity;
        canonical[1] = (uint)SectionId.KindNames;
        canonical[2] = (uint)SectionId.RelationNames;
        canonical[3] = (uint)SectionId.Nodes;
        if (history)
        {
            canonical[4] = (uint)SectionId.History;
        }

        // New files include a checksum as their final section.
        canonical[known] = (uint)SectionId.Checksum;

        for (int i = 0; i < file.UnknownCount; i++)
        {
            canonical[known + 1 + i] = file.UnknownSections[i].Id;
        }
        return canonical;
    }

    /// <summary>A section this build kept rather than interpreted, or null.</summary>
    static byte[] Preserved(in SourceFile file, uint id)
    {
        for (int i = 0; i < file.UnknownCount; i++)
        {
            if (file.UnknownSections[i].Id == id)
            {
                return file.UnknownSections[i].Bytes;
            }
        }
        return null;
    }

    static byte[] NextUnknown(in SourceFile file, uint id, ref int cursor)
    {
        for (int i = cursor; i < file.UnknownCount; i++)
        {
            if (file.UnknownSections[i].Id == id)
            {
                cursor = i + 1;
                return file.UnknownSections[i].Bytes;
            }
        }
        return new byte[0];
    }

    static byte[] WriteIdentity(in SourceFile file)
    {
        ByteWriter w = ByteWriter.Create(32);
        w.U64(file.Src.Identity.Hi);
        w.U64(file.Src.Identity.Lo);
        // Encode the root as a signed varint.
        w.SVar(file.Src.Root.Index);
        return w.ToArray();
    }

    static byte[] WriteKindNames(in SourceFile file, int[] order)
    {
        QualifiedName[] names = new QualifiedName[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            names[i] = KindTable.NameOf(in file.Table, new KindId(order[i]));
        }
        return WriteNames(names);
    }

    static byte[] WriteRelationNames(in SourceFile file, int[] order)
    {
        QualifiedName[] names = new QualifiedName[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            names[i] = KindTable.RelationNameOf(in file.Table, new RelationId(order[i]));
        }
        return WriteNames(names);
    }

    /// <summary>
    /// Encodes each library ID once, then names referencing library slots. Library slots follow
    /// first-use order.
    /// </summary>
    static byte[] WriteNames(QualifiedName[] names)
    {
        SourceId[] libraries = new SourceId[names.Length];
        int[] libraryOf = new int[names.Length];
        int libraryCount = 0;

        for (int i = 0; i < names.Length; i++)
        {
            libraryOf[i] = -1;
            for (int l = 0; l < libraryCount; l++)
            {
                if (libraries[l] == names[i].Library)
                {
                    libraryOf[i] = l;
                    break;
                }
            }

            if (libraryOf[i] < 0)
            {
                libraries[libraryCount] = names[i].Library;
                libraryOf[i] = libraryCount;
                libraryCount++;
            }
        }

        ByteWriter w = ByteWriter.Create(512);

        w.UVar((ulong)libraryCount);
        for (int l = 0; l < libraryCount; l++)
        {
            w.U64(libraries[l].Hi);
            w.U64(libraries[l].Lo);
        }

        w.UVar((ulong)names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            w.UVar((ulong)libraryOf[i]);

            byte[] text = System.Text.Encoding.UTF8.GetBytes(names[i].Name ?? string.Empty);
            w.UVar((ulong)text.Length);
            w.Bytes(text);
        }

        return w.ToArray();
    }

    /// <summary>
    /// Writes nodes in slot order. Parent and child indices are relative to the owner node;
    /// reference targets remain absolute so -1 can represent None. Counts and indices use varints.
    /// </summary>
    static void WriteNodes(ref ByteWriter w, in SourceFile file, int[] kindSlot, int[] relationSlot)
    {
        w.UVar((ulong)file.Src.NodeCount);

        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            // Serialize deleted slots to preserve IDs and undo data.
            w.U8((byte)file.Src.Nodes[i].Flags);
            w.SVar(MapKind(kindSlot, file.Src.Nodes[i].Kind));
            w.SVar((long)file.Src.Nodes[i].Parent.Index - i);

            ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, new NodeId(i));
            w.UVar((ulong)payload.Length);
            w.Bytes(payload);

            int childOffset = file.Src.Nodes[i].ChildOffset;
            int childCount = file.Src.Nodes[i].ChildCount;
            w.UVar((ulong)childCount);

            long previous = i;
            for (int c = 0; c < childCount; c++)
            {
                long child = file.Src.Children[childOffset + c];
                w.SVar(child - previous);
                previous = child;
            }

            int refOffset = file.Src.Nodes[i].RefOffset;
            int refCount = file.Src.Nodes[i].RefCount;
            w.UVar((ulong)refCount);
            for (int r = 0; r < refCount; r++)
            {
                Ref edge = file.Src.Refs[refOffset + r];
                w.SVar(MapRelation(relationSlot, edge.Relation));

                // Biased by one so that zero is `none` and no value is ambiguous.
                w.UVar((ulong)(edge.Target.Index + 1));

                ReadOnlySpan<byte> label = SourceWalk.EdgeBytes(in file.Src, in edge);
                w.UVar((ulong)label.Length);
                w.Bytes(label);
            }
        }

    }

    /// <summary>
    /// Collects kind and relation names in first-use order, producing handle-to-file-slot maps.
    /// Embedded schema payloads are already graph content.
    /// </summary>
    static void CollectNames(
        in SourceFile file,
        out int[] kindOrder,
        out int[] kindSlot,
        out int[] relationOrder,
        out int[] relationSlot)
    {
        kindSlot = new int[file.Table.KindCount];
        relationSlot = new int[file.Table.RelationCount];
        for (int i = 0; i < kindSlot.Length; i++)
        {
            kindSlot[i] = -1;
        }
        for (int i = 0; i < relationSlot.Length; i++)
        {
            relationSlot[i] = -1;
        }

        int[] kinds = new int[file.Table.KindCount];
        int kindCount = 0;
        int[] relations = new int[file.Table.RelationCount];
        int relationCount = 0;

        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            int kind = file.Src.Nodes[i].Kind.Value;
            if (kind >= 0 && kind < kindSlot.Length && kindSlot[kind] < 0)
            {
                kindSlot[kind] = kindCount;
                kinds[kindCount] = kind;
                kindCount++;
            }

            int refOffset = file.Src.Nodes[i].RefOffset;
            int refCount = file.Src.Nodes[i].RefCount;
            for (int r = 0; r < refCount; r++)
            {
                int relation = file.Src.Refs[refOffset + r].Relation.Value;
                if (relation >= 0 && relation < relationSlot.Length && relationSlot[relation] < 0)
                {
                    relationSlot[relation] = relationCount;
                    relations[relationCount] = relation;
                    relationCount++;
                }
            }
        }

        // Include names used only by applied history, such as a disconnected edge’s relation.
        for (int i = 0; i < file.History.At; i++)
        {
            int kind = file.History.Entries[i].Op.NodeKind.Value;
            if (kind >= 0 && kind < kindSlot.Length && kindSlot[kind] < 0)
            {
                kindSlot[kind] = kindCount;
                kinds[kindCount] = kind;
                kindCount++;
            }

            int relation = file.History.Entries[i].Op.Relation.Value;
            if (relation >= 0 && relation < relationSlot.Length && relationSlot[relation] < 0)
            {
                relationSlot[relation] = relationCount;
                relations[relationCount] = relation;
                relationCount++;
            }
        }

        kindOrder = new int[kindCount];
        Array.Copy(kinds, kindOrder, kindCount);
        relationOrder = new int[relationCount];
        Array.Copy(relations, relationOrder, relationCount);
    }

    static int MapKind(int[] kindSlot, KindId kind)
    {
        return kind.Value >= 0 && kind.Value < kindSlot.Length ? kindSlot[kind.Value] : -1;
    }

    static int MapRelation(int[] relationSlot, RelationId relation)
    {
        return relation.Value >= 0 && relation.Value < relationSlot.Length ? relationSlot[relation.Value] : -1;
    }
}
