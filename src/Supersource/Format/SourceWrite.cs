using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Supersource.Kinds;
using Supersource.Timeline;

namespace Supersource.Format;
/// <summary>
/// Serializes names in first-use order, schemas once per kind, and live nodes in id order,
/// preserving child and reference order. Arena offsets and capacities are rebuilt on load. A full
/// write always writes the current format; unknown sections keep their bytes and relative order.
/// </summary>
public static class SourceWrite
{
    /// <summary>Signature with CR, LF and SUB bytes to detect common text-transfer damage.</summary>
    public static readonly byte[] Magic = new byte[] { 0x53, 0x53, 0x53, 0x52, 0x43, 0x0D, 0x0A, 0x1A };

    /// <summary>
    /// Current major format version. Version 5 keeps the format's own metadata in sections — the
    /// schemas are a section rather than nodes in the graph — and writes each thing once: live
    /// nodes only, no stored parent or flags, no payload length a schema already fixes, and the
    /// next id in the identity section. Readers also support the earlier major versions.
    /// </summary>
    public const ushort FormatMajor = 5;
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

    /// <summary>
    /// Write a source to a file, without ever holding all of it at once.
    ///
    /// Written to a `.writing` sibling and renamed over the destination, the same name
    /// <see cref="Replace"/> uses for a write in progress. A reader may hold the destination
    /// mapped — `SourceRead.FromFile` borrows payload bytes from a mapping that stays open until
    /// `SourceFile.Close` — and Windows refuses to truncate a file with a mapped section open,
    /// whatever the share mode says. The rename swaps the directory entry instead, and leaves
    /// the old content in place when the write fails.
    /// </summary>
    public static void ToFile(in SourceFile file, string path)
    {
        string beside = path + ".writing";
        try
        {
            using (FileStream sink = new FileStream(
                beside, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, WindowBytes))
            {
                ToStream(in file, sink);
            }

            File.Move(beside, path, true);
        }
        finally
        {
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
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
        file.SavedAt = file.Log.At;
        file.SavedStamp = file.Src.Stamp;
        file.SavedTableRevision = file.Table.Revision;
    }

    /// <summary>
    /// Appends logged edits when the log has advanced from the saved position, stamps form an
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

        // A changed table changes the names or schemas a file holds, and only a full write can say so.
        if (file.Table.Revision != file.SavedTableRevision)
        {
            return false;
        }

        // An append keeps every section already in the file, so a file in an older format, or
        // one still carrying a retired section, is written in full, which is what brings it to
        // this one.
        if (file.Major != FormatMajor)
        {
            return false;
        }
        for (int i = 0; i < file.SectionOrderCount; i++)
        {
            if (file.SectionOrder[i] == (uint)SectionId.RetiredHistory)
            {
                return false;
            }
        }

        int from = file.SavedAt;
        int to = file.Log.At;
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
            if (file.Log.Entries[i].StampBefore != stamp)
            {
                return false;
            }
            stamp = file.Log.Entries[i].StampAfter;
        }

        if (stamp != file.Src.Stamp)
        {
            return false;
        }

        int count = JournalCodec.Collect(in file.Log, from, to, out Operation[] ops);
        for (int i = 0; i < count; i++)
        {
            int kind = JournalCodec.NamesKind(ops[i].Kind) ? ops[i].NodeKind.Value : -1;
            if (kind >= 0 && (kind >= file.KindSlot.Length || file.KindSlot[kind] < 0))
            {
                return false;
            }

            int relation = JournalCodec.NamesRelation(ops[i].Kind) ? ops[i].Relation.Value : -1;
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
                JournalCodec.Write(ref w, ops, count, file.KindSlot, file.RelationSlot);
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

        // First-use order, so the tables depend only on the graph and the source's own declarations.
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
                case SectionId.Schemas:
                    w.Bytes(SchemaSection.Write(in file.Table, kindOrder, relationOrder, relationSlot));
                    break;
                case SectionId.Nodes:
                    WriteNodes(ref w, in file, kindSlot, relationSlot);
                    break;
                case SectionId.Checksum:
                    // Written blank and filled in below, once there is something to say.
                    w.U8((byte)DigestKind.Sha256);
                    w.Bytes(new byte[32]);
                    break;
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
            // The checksum last, once every section it covers has been hashed.
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
        // The format's own sections in their canonical order, then the sections this build kept
        // without reading, in the order the file had them. Journals are not among either: a full
        // write folds their operations into the graph.
        uint[] order = new uint[6 + file.UnknownCount];
        order[0] = (uint)SectionId.Identity;
        order[1] = (uint)SectionId.KindNames;
        order[2] = (uint)SectionId.RelationNames;
        order[3] = (uint)SectionId.Schemas;
        order[4] = (uint)SectionId.Nodes;
        order[5] = (uint)SectionId.Checksum;
        for (int i = 0; i < file.UnknownCount; i++)
        {
            order[6 + i] = file.UnknownSections[i].Id;
        }
        return order;
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
        w.SVar(file.Src.Root.Index);

        // Every id below this was handed out once and is never handed out again.
        w.UVar((ulong)file.Src.NodeCount);
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
    /// Writes live nodes in id order, as runs of consecutive ids. Each node is written once and
    /// says only what nothing else does: its kind, its payload, its children and its references.
    /// A parent is read off its children, everything written is live, and a payload's length is
    /// left out when the kind's schema fixes it and the payload agrees.
    ///
    /// <code>
    /// uvar runCount
    /// per run: uvar gap (ids skipped since the previous run) · uvar length
    /// per node:
    ///   uvar (kindSlot + 1) &lt;&lt; 1 | explicitLength
    ///   [uvar payloadLength] payload
    ///   uvar childCount · svar child − previous, starting from the node's own id
    ///   uvar refCount · per ref: uvar (relationSlot + 1) &lt;&lt; 1 | explicitLength ·
    ///                            uvar target + 1 · [uvar length] edge data
    /// </code>
    /// </summary>
    static void WriteNodes(ref ByteWriter w, in SourceFile file, int[] kindSlot, int[] relationSlot)
    {
        int runs = 0;
        bool inRun = false;
        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            bool live = file.Src.Nodes[i].IsLive;
            if (live && !inRun)
            {
                runs++;
            }
            inRun = live;
        }

        w.UVar((ulong)runs);

        long end = 0;
        long at = 0;
        while (at < file.Src.NodeCount)
        {
            if (!file.Src.Nodes[at].IsLive)
            {
                at++;
                continue;
            }

            long start = at;
            while (at < file.Src.NodeCount && file.Src.Nodes[at].IsLive)
            {
                at++;
            }

            w.UVar((ulong)(start - end));
            w.UVar((ulong)(at - start));
            for (long i = start; i < at; i++)
            {
                WriteNode(ref w, in file, i, kindSlot, relationSlot);
            }
            end = at;
        }
    }

    /// <summary>
    /// A payload's bytes, in one span when it fits one and in pieces when it does not: a payload
    /// may be larger than an array, and the writer streams.
    /// </summary>
    static void WritePayload(ref ByteWriter w, in Source src, NodeId node, long length)
    {
        if (length <= Window && !src.Blob[Source.SegmentOf(src.Nodes[node.Index].PayloadOffset)].IsChunked)
        {
            w.Bytes(SourceWalk.Payload(in src, node));
            return;
        }
        var window = new byte[(int)Math.Min(length, Window)];
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(window.Length, length - done);
            SourceWalk.ReadPayload(in src, node, done, window.AsSpan(0, n));
            w.Bytes(window.AsSpan(0, n));
            done += n;
        }
    }

    const int Window = 1 << 20;

    static void WriteNode(ref ByteWriter w, in SourceFile file, long i, int[] kindSlot, int[] relationSlot)
    {
        KindId kind = file.Src.Nodes[i].Kind;
        long payloadLength = SourceWalk.PayloadLength(in file.Src, new NodeId(i));
        int kindAt = MapKind(kindSlot, kind);
        int fixedLength = kindAt >= 0 && KindTable.HasSchema(in file.Table, kind)
            ? KindTable.SchemaOf(in file.Table, kind).Payload.FixedLength
            : -1;
        bool explicitLength = fixedLength < 0 || fixedLength != payloadLength;

        w.UVar(((ulong)(kindAt + 1) << 1) | (explicitLength ? 1UL : 0UL));
        if (explicitLength)
        {
            w.UVar((ulong)payloadLength);
        }
        WritePayload(ref w, in file.Src, new NodeId(i), payloadLength);

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
            ReadOnlySpan<byte> data = SourceWalk.EdgeData(in file.Src, in edge);
            // A relation the file cannot name has no schema a reader could find, so its length is
            // always written.
            int slot = MapRelation(relationSlot, edge.Relation);
            int fixedData = slot >= 0
                ? KindTable.RelationSchemaOf(in file.Table, edge.Relation).Payload.FixedLength
                : -1;
            bool explicitData = fixedData != data.Length;

            w.UVar(((ulong)(slot + 1) << 1) | (explicitData ? 1UL : 0UL));

            // Biased by one so that zero is `none` and no value is ambiguous.
            w.UVar((ulong)(edge.Target.Index + 1));
            if (explicitData)
            {
                w.UVar((ulong)data.Length);
            }
            w.Bytes(data);
        }
    }

    /// <summary>
    /// The kinds and relations a file names, in first-use order: what its live nodes and their
    /// references use, what this source declares itself — a kind library's kinds, with no node
    /// needed to carry them — and the relations those kinds' schemas list. Produces handle-to-slot
    /// maps.
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
            if (!file.Src.Nodes[i].IsLive)
            {
                continue;
            }

            kindCount = Name(kindSlot, kinds, kindCount, file.Src.Nodes[i].Kind.Value);

            int refOffset = file.Src.Nodes[i].RefOffset;
            int refCount = file.Src.Nodes[i].RefCount;
            for (int r = 0; r < refCount; r++)
            {
                relationCount = Name(relationSlot, relations, relationCount,
                    file.Src.Refs[refOffset + r].Relation.Value);
            }
        }

        SourceId self = file.Src.Identity;
        if (!self.IsNone)
        {
            for (int k = 0; k < file.Table.KindCount; k++)
            {
                if (file.Table.KindNames[k].Library == self)
                {
                    kindCount = Name(kindSlot, kinds, kindCount, k);
                }
            }
            for (int r = 0; r < file.Table.RelationCount; r++)
            {
                if (file.Table.RelationNames[r].Library == self)
                {
                    relationCount = Name(relationSlot, relations, relationCount, r);
                }
            }
        }

        // A schema names the relations its kind uses by slot, so they need slots too.
        for (int k = 0; k < kindCount; k++)
        {
            ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in file.Table, new KindId(kinds[k]));
            for (int r = 0; r < used.Length; r++)
            {
                relationCount = Name(relationSlot, relations, relationCount, used[r].Value);
            }
        }

        kindOrder = new int[kindCount];
        Array.Copy(kinds, kindOrder, kindCount);
        relationOrder = new int[relationCount];
        Array.Copy(relations, relationOrder, relationCount);
    }

    /// <summary>Gives a handle the next slot if it has none yet. Answers the new count.</summary>
    static int Name(int[] slot, int[] order, int count, int handle)
    {
        if (handle < 0 || handle >= slot.Length || slot[handle] >= 0)
        {
            return count;
        }
        slot[handle] = count;
        order[count] = handle;
        return count + 1;
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
