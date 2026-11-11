using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using Supersource.Kinds;

namespace Supersource.Format;
/// <summary>
/// Decodes framing, names, embedded schemas, nodes and journals, and interprets no payload beyond
/// the length its format fixes. Structural validation and rules are separate operations.
/// </summary>
public static class SourceRead
{
    /// <summary>
    /// Reads section headers without decoding their contents. Grows into as needed and returns the
    /// number of spans written.
    /// </summary>
    public static int Sections(byte[] bytes, ref SectionSpan[] into)
    {
        using (MemoryStream over = new MemoryStream(bytes, false))
        {
            return Sections(over, ref into);
        }
    }

    /// <summary>Walks length-framed section headers on a seekable stream without reading section contents.</summary>
    public static int Sections(Stream stream, ref SectionSpan[] into)
    {
        stream.Position = 0;
        ByteReader head = ByteReader.Over(Exactly(stream, SourceWrite.Magic.Length + 2 + 2 + 4));

        ReadOnlySpan<byte> magic = head.Bytes(SourceWrite.Magic.Length);
        for (int i = 0; i < SourceWrite.Magic.Length; i++)
        {
            if (magic[i] != SourceWrite.Magic[i])
            {
                throw new FormatCorruptException("not a Supersource file: bad magic");
            }
        }

        head.U16();
        head.U16();

        uint claimed = head.U32();
        long room = stream.Length - stream.Position;
        if (claimed > room / 12)
        {
            throw new FormatCorruptException(
                "the section table claims " + claimed + " sections, and only " + room + " bytes remain");
        }

        int count = (int)claimed;
        if (into == null || into.Length < count)
        {
            into = new SectionSpan[count < 4 ? 4 : count];
        }

        long at = stream.Position;
        for (int i = 0; i < count; i++)
        {
            stream.Position = at;
            ByteReader header = ByteReader.Over(Exactly(stream, 12));

            into[i].Id = header.U32();

            ulong length = header.U64();
            into[i].At = at + 12;

            if (length > (ulong)(stream.Length - into[i].At))
            {
                throw new FormatCorruptException(
                    "section " + into[i].Id + " claims " + length + " bytes and the file is shorter");
            }

            into[i].Length = (long)length;
            at = into[i].At + into[i].Length;
        }

        return count;
    }

    /// <summary>Decodes a source and reports FormatCorruptException as false with a problem description.</summary>
    public static bool TryFromBytes(byte[] bytes, out SourceFile file, out string problem)
    {
        try
        {
            file = FromBytes(bytes);
            problem = null;
            return true;
        }
        catch (FormatCorruptException corrupt)
        {
            file = default;
            problem = corrupt.Message;
            return false;
        }
    }

    /// <summary>
    /// Read a source, throwing <see cref="FormatCorruptException"/> on a malformed one.
    /// <see cref="TryFromBytes"/> is the form to prefer where the bytes are not yours.
    /// </summary>
    public static SourceFile FromBytes(byte[] bytes)
    {
        using (MemoryStream over = new MemoryStream(bytes, false))
        {
            return FromStream(over);
        }
    }

    /// <summary>
    /// Memory-maps a file and borrows its payload bytes. Call SourceFile.Close when finished, or
    /// compact into owned storage before closing the mapping.
    /// </summary>
    public static unsafe SourceFile FromFile(string path)
    {
        using (FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, WindowBytes, FileOptions.RandomAccess))
        {
            if (stream.Length == 0)
            {
                throw new FormatCorruptException("the file is empty");
            }

            // Allow other readers and replacement of the file while this mapping remains open.
            MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);

            MemoryMappedViewAccessor view = null;
            try
            {
                view = mapping.CreateViewAccessor(0, stream.Length, MemoryMappedFileAccess.Read);

                byte* mapped = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref mapped);

                MappedFile file = default;
                file.Base = mapped;
                file.Length = stream.Length;

                stream.Position = 0;
                SourceFile read = FromStream(stream, in file);

                read.Mapping = mapping;
                read.View = view;
                return read;
            }
            catch
            {
                if (view != null)
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                    view.Dispose();
                }
                mapping.Dispose();
                throw;
            }
        }
    }

    /// <summary>How much of a section is resident while it is being read.</summary>
    public const int WindowBytes = 1 << 16;

    /// <summary>
    /// Decodes a seekable stream, reading sections in dependency order. Unknown sections are
    /// retained in full for preservation; known large sections use a bounded window.
    /// </summary>
    public static SourceFile FromStream(Stream stream)
    {
        MappedFile none = default;
        return FromStream(stream, in none);
    }

    /// <summary>
    /// Reads framing while borrowing payload bytes from a mapping of the whole file. The caller
    /// must keep the mapping open until those payloads are no longer used.
    /// </summary>
    public static unsafe SourceFile FromStream(Stream stream, in MappedFile mapping)
    {
        if (!stream.CanSeek)
        {
            throw new FormatCorruptException(
                "a source is read in dependency order, which needs a stream that can seek");
        }

        byte[] head = Exactly(stream, SourceWrite.Magic.Length + 2 + 2 + 4);
        ByteReader r = ByteReader.Over(head);

        ReadOnlySpan<byte> magic = r.Bytes(SourceWrite.Magic.Length);
        for (int i = 0; i < SourceWrite.Magic.Length; i++)
        {
            if (magic[i] != SourceWrite.Magic[i])
            {
                throw new FormatCorruptException("not a Supersource file: bad magic");
            }
        }

        ushort major = r.U16();
        r.U16();   // minor: a newer minor only ever adds sections, which skip on their own

        if (major > SourceWrite.FormatMajor)
        {
            // Reject newer major versions whose known sections may have incompatible layouts.
            throw new FormatCorruptException(
                "source is format major " + major + "; this build reads " + SourceWrite.FormatMajor);
        }

        SourceFile file = SourceFile.Create(16);
        file.Major = major;

        uint claimed = r.U32();
        long room = stream.Length - stream.Position;

        // A section header is an id and a 64-bit length: twelve bytes before any content.
        if (claimed > room / 12)
        {
            throw new FormatCorruptException(
                "the section table claims " + claimed + " sections, and only " + room + " bytes remain");
        }

        int sectionCount = (int)claimed;
        file.SectionOrder = new uint[sectionCount];
        file.SectionOrderCount = 0;
        file.UnknownSections = new UnknownSection[sectionCount];
        file.UnknownCount = 0;

        // Names are needed before nodes, and sections may arrive in any order, so the node
        // section is located first and read last.
        QualifiedName[] kindNames = new QualifiedName[0];
        QualifiedName[] relationNames = new QualifiedName[0];
        long nodesAt = -1;
        long nodesLength = 0;
        long schemasAt = -1;
        long schemasLength = 0;
        long nextId = -1;

        // Journals are applied in file order, after everything else. There may be any
        // number of them — one per save that appended rather than rewrote.
        long[] journalAt = new long[4];
        long[] journalLength = new long[4];
        int journalCount = 0;

        long at = stream.Position;
        for (int i = 0; i < sectionCount; i++)
        {
            stream.Position = at;
            ByteReader header = ByteReader.Over(Exactly(stream, 12));

            uint id = header.U32();
            ulong length64 = header.U64();

            long content = at + 12;
            if (length64 > (ulong)(stream.Length - content))
            {
                throw new FormatCorruptException(
                    "section " + id + " claims " + length64 + " bytes and the file is shorter");
            }

            long length = (long)length64;
            at = content + length;

            file.SectionOrder[file.SectionOrderCount] = id;
            file.SectionOrderCount++;

            switch ((SectionId)id)
            {
                case SectionId.Identity:
                    nextId = ReadIdentity(Small(stream, content, length, "identity"), ref file, major);
                    break;
                case SectionId.Schemas:
                    // After the names it refers to, before the nodes whose lengths it may fix.
                    schemasAt = content;
                    schemasLength = length;
                    break;
                case SectionId.KindNames:
                    kindNames = ReadNames(Small(stream, content, length, "kind names"), major);
                    break;
                case SectionId.RelationNames:
                    relationNames = ReadNames(Small(stream, content, length, "relation names"), major);
                    break;
                case SectionId.Nodes:
                    nodesAt = content;
                    nodesLength = length;
                    break;
                case SectionId.RetiredHistory:
                    // Neither decoded nor preserved, so the next save drops it. Keeping it would
                    // keep every edit ever made to the source, including everything removed.
                    break;
                case SectionId.Journal:
                    if (journalCount == journalAt.Length)
                    {
                        Array.Resize(ref journalAt, journalCount * 2);
                        Array.Resize(ref journalLength, journalCount * 2);
                    }
                    journalAt[journalCount] = content;
                    journalLength[journalCount] = length;
                    journalCount++;
                    break;
                case SectionId.Checksum:
                    ReadDigest(Small(stream, content, length, "checksum"), ref file);
                    break;
                default:
                    Preserve(ref file, id, Whole(stream, content, length, "section " + id));
                    break;
            }
        }

        // Intern file names into this table’s local handles.
        KindId[] kinds = new KindId[kindNames.Length];
        for (int i = 0; i < kindNames.Length; i++)
        {
            kinds[i] = KindTable.Intern(ref file.Table, kindNames[i]);
        }

        RelationId[] relations = new RelationId[relationNames.Length];
        for (int i = 0; i < relationNames.Length; i++)
        {
            relations[i] = KindTable.InternRelation(ref file.Table, relationNames[i]);
        }

        if (schemasAt >= 0)
        {
            SchemaSection.Read(Small(stream, schemasAt, schemasLength, "schemas"), ref file.Table, kinds, relations);
        }

        if (nodesAt >= 0)
        {
            stream.Position = nodesAt;
            ByteReader nodes = ByteReader.Over(stream, nodesLength, WindowBytes, nodesAt);

            if (major >= 5)
            {
                ReadNodesV5(nodes, ref file, kinds, relations, nextId, in mapping);
            }
            else if (major >= 2)
            {
                ReadNodes(nodes, ref file, kinds, relations, in mapping);
            }
            else
            {
                ReadNodesV1(nodes, ref file, kinds, relations, in mapping);
            }
        }

        if (major < 5)
        {
            // An older file kept its schemas as nodes and its deleted nodes with their content.
            // Read into the model this build has: schemas in the table, the deleted gone.
            Clear(ref file.Src);
            LegacySchemaNodes.Convert(ref file);
            LegacyExternals.Convert(ref file);
        }

        // Retain the file’s original name-slot mapping for later journal appends.
        file.KindSlot = new int[file.Table.KindCount];
        file.RelationSlot = new int[file.Table.RelationCount];
        for (int i = 0; i < file.KindSlot.Length; i++)
        {
            file.KindSlot[i] = -1;
        }
        for (int i = 0; i < file.RelationSlot.Length; i++)
        {
            file.RelationSlot[i] = -1;
        }
        for (int i = 0; i < kinds.Length; i++)
        {
            file.KindSlot[kinds[i].Value] = i;
        }
        for (int i = 0; i < relations.Length; i++)
        {
            file.RelationSlot[relations[i].Value] = i;
        }

        ContentHash[] journalDigest = new ContentHash[journalCount];
        for (int i = 0; i < journalCount; i++)
        {
            // Replay operations after the saved graph. The journal’s trailing digest is verified
            // separately by Verify.
            stream.Position = journalAt[i];
            long entries = journalLength[i] - JournalCodec.DigestBytes;
            if (entries < 0)
            {
                throw new FormatCorruptException("a journal section is too short to carry its own digest");
            }

            ByteReader journal = ByteReader.Over(stream, entries, WindowBytes, journalAt[i]);
            if (!JournalCodec.Replay(journal, ref file.Src, kinds, relations))
            {
                throw new FormatCorruptException(
                    "a journal is a version this build does not know, and skipping it would " +
                    "give back a source missing the edits it records");
            }

            journalDigest[i] = Read32(Whole(stream, journalAt[i] + entries,
                JournalCodec.DigestBytes, "journal digest"));
        }

        file.Digest = Fold(file.Digest, journalDigest, journalCount);

        // A session starts from the file as it is: an empty log, nothing to undo and nothing
        // unsaved.
        file.SavedAt = 0;
        file.SavedStamp = file.Src.Stamp;
        file.SavedTableRevision = file.Table.Revision;


        return file;
    }

    /// <summary>
    /// Borrows payload bytes from a mapping when available; otherwise copies them. Mapped payloads
    /// have zero owned capacity and relocate on first edit.
    /// </summary>
    static void ReadPayload(ref Source src, ref ByteReader r, long node, long length, int mapped)
    {
        if (mapped >= 0)
        {
            src.Nodes[node].PayloadOffset = Source.BlobAddress(mapped, r.Position);
            src.Nodes[node].PayloadLength = length;
            src.Capacities[node].Payload = 0;
            r.Skip(length);
            return;
        }

        long offset = Source.ReserveBlob(ref src, length);
        // In pieces: a payload need not fit the reader's window, nor one span, nor one array.
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(length - done, 1 << 20);
            Source.Write(ref src, offset, done, r.Bytes(n));
            done += n;
        }

        src.Nodes[node].PayloadOffset = offset;
        src.Nodes[node].PayloadLength = length;
        src.Capacities[node].Payload = length;
    }

    /// <summary>What the file says about itself. Recorded, not checked — see <see cref="Verify"/>.</summary>
    static void ReadDigest(ByteReader r, ref SourceFile file)
    {
        DigestKind kind = (DigestKind)r.U8();
        if (kind != DigestKind.Sha256 || r.Remaining < 32)
        {
            // A digest this build cannot compute is not a corrupt file; it is a file that
            // knows something we do not. Nothing is claimed about it.
            return;
        }

        file.DigestKind = kind;
        file.Digest = new ContentHash(r.U64(), r.U64(), r.U64(), r.U64());
    }

    /// <summary>
    /// Combines the stored checksum with journal digests in order. Returns the checksum unchanged
    /// when there are no journals.
    /// </summary>
    static ContentHash Fold(ContentHash checksum, ContentHash[] journals, int count)
    {
        if (count == 0 || checksum.IsNone)
        {
            return checksum;
        }

        Span<byte> bytes = stackalloc byte[32];
        using IncrementalHash folded = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        checksum.WriteTo(bytes);
        folded.AppendData(bytes);
        for (int i = 0; i < count; i++)
        {
            journals[i].WriteTo(bytes);
            folded.AppendData(bytes);
        }

        Span<byte> digest = stackalloc byte[32];
        folded.GetHashAndReset(digest);
        return Read32(digest);
    }

    static ContentHash Read32(ReadOnlySpan<byte> digest)
    {
        ByteReader r = ByteReader.Over(digest.ToArray());
        return new ContentHash(r.U64(), r.U64(), r.U64(), r.U64());
    }

    /// <summary>
    /// Recomputes the file's checksum, journals folded in, and compares it with the one it carries.
    /// An absent checksum is reported through recorded.IsNone.
    /// </summary>
    public static bool Verify(string path, out ContentHash recorded, out ContentHash actual)
    {
        recorded = default;
        actual = default;

        using (FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, WindowBytes, FileOptions.SequentialScan))
        {
            SectionSpan[] spans = null;
            int count = Sections(stream, ref spans);

            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] window = new byte[WindowBytes];

            ContentHash checksum = default;
            ContentHash[] said = new ContentHash[count];
            ContentHash[] found = new ContentHash[count];
            int journals = 0;

            for (int i = 0; i < count; i++)
            {
                if (spans[i].Id == (uint)SectionId.Checksum)
                {
                    ByteReader r = ByteReader.Over(Whole(stream, spans[i].At, spans[i].Length, "checksum"));
                    SourceFile carrying = default;
                    ReadDigest(r, ref carrying);
                    checksum = carrying.Digest;
                    continue;
                }

                // Hash each journal separately, matching the digest it stores.
                bool journal = spans[i].Id == (uint)SectionId.Journal;
                long left = spans[i].Length;
                IncrementalHash into = digest;
                IncrementalHash own = null;

                if (journal)
                {
                    if (left < JournalCodec.DigestBytes)
                    {
                        throw new FormatCorruptException("a journal section is too short to carry its own digest");
                    }

                    own = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    into = own;
                    left -= JournalCodec.DigestBytes;

                    said[journals] = Read32(
                        Whole(stream, spans[i].At + left, JournalCodec.DigestBytes, "journal digest"));
                }

                stream.Position = spans[i].At;
                while (left > 0)
                {
                    int want = left > window.Length ? window.Length : (int)left;
                    int got = stream.Read(window, 0, want);
                    if (got <= 0)
                    {
                        throw new FormatCorruptException("the file ends inside section " + spans[i].Id);
                    }

                    into.AppendData(window, 0, got);
                    left -= got;
                }

                if (journal)
                {
                    found[journals] = Read32(own.GetCurrentHash());
                    own.Dispose();
                    journals++;
                }
            }

            recorded = Fold(checksum, said, journals);
            actual = Fold(Read32(digest.GetCurrentHash()), found, journals);
        }

        return !recorded.IsNone && recorded.Equals(actual);
    }

    /// <summary>The digest a file carries, without reading anything else.</summary>
    public static ContentHash DigestOf(string path)
    {
        using (FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
        {
            SectionSpan[] spans = null;
            int count = Sections(stream, ref spans);

            ContentHash checksum = default;
            ContentHash[] journals = new ContentHash[count];
            int journalCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (spans[i].Id == (uint)SectionId.Checksum)
                {
                    SourceFile carrying = default;
                    ReadDigest(ByteReader.Over(Whole(stream, spans[i].At, spans[i].Length, "checksum")),
                        ref carrying);
                    checksum = carrying.Digest;
                }
                else if (spans[i].Id == (uint)SectionId.Journal
                    && spans[i].Length >= JournalCodec.DigestBytes)
                {
                    journals[journalCount] = Read32(Whole(stream,
                        spans[i].At + spans[i].Length - JournalCodec.DigestBytes,
                        JournalCodec.DigestBytes, "journal digest"));
                    journalCount++;
                }
            }

            return Fold(checksum, journals, journalCount);
        }
    }

    /// <summary>
    /// Reads a section into a managed buffer after checking its length against the file and array
    /// limits.
    /// </summary>
    static ByteReader Small(Stream stream, long at, long length, string what)
    {
        const int Sane = 1 << 26;
        if (length > Sane)
        {
            throw new FormatCorruptException(
                what + " claims " + length + " bytes, which is not a " + what + " section");
        }

        return ByteReader.Over(Whole(stream, at, length, what));
    }

    /// <summary>Every byte of a section, for the things that have to be held rather than walked.</summary>
    static byte[] Whole(Stream stream, long at, long length, string what)
    {
        if (length > int.MaxValue)
        {
            throw new FormatCorruptException(
                what + " is " + length + " bytes, larger than this build can hold in one piece");
        }

        stream.Position = at;
        return Exactly(stream, (int)length);
    }

    /// <summary>Exactly this many bytes, or the file was shorter than it said it was.</summary>
    static byte[] Exactly(Stream stream, int count)
    {
        byte[] bytes = new byte[count];
        int got = 0;
        while (got < count)
        {
            int read = stream.Read(bytes, got, count - got);
            if (read <= 0)
            {
                throw new FormatCorruptException(
                    "the file ends " + (count - got) + " bytes early");
            }
            got += read;
        }
        return bytes;
    }

    /// <summary>The identity and the root, and from version 5 the next id, which it answers; -1 before.</summary>
    static long ReadIdentity(ByteReader r, ref SourceFile file, ushort major)
    {
        ulong hi = r.U64();
        ulong lo = r.U64();
        file.Src.Identity = new SourceId(hi, lo);

        // Versions 1 and 2 encode the root in four bytes; later versions use a signed varint.
        file.Src.Root = new NodeId(major >= 3 ? r.SVar() : r.I32());

        return major >= 5 ? (long)r.UVar() : -1;
    }

    /// <summary>
    /// Empties every slot that does not hold a live node. Files before version 5 wrote a deleted
    /// node with everything it held; a source keeps nothing of one.
    /// </summary>
    static void Clear(ref Source src)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            if (src.Nodes[i].IsLive)
            {
                continue;
            }
            src.Nodes[i] = default;
            src.Nodes[i].Parent = NodeId.None;
            src.Capacities[i] = default;
        }
    }

    static QualifiedName[] ReadNames(ByteReader r, ushort major)
    {
        if (major < 2)
        {
            return ReadNamesV1(r);
        }

        // The libraries once, then the names pointing at them.
        int libraryCount = r.UVarCount(16, "library table");
        SourceId[] libraries = new SourceId[libraryCount];
        for (int i = 0; i < libraryCount; i++)
        {
            ulong hi = r.U64();
            ulong lo = r.U64();
            libraries[i] = new SourceId(hi, lo);
        }

        int count = r.UVarCount(2, "name table");
        QualifiedName[] names = new QualifiedName[count];
        for (int i = 0; i < count; i++)
        {
            int library = r.UVarInt();
            if (library >= libraryCount)
            {
                throw new FormatCorruptException(
                    "name " + i + " names library " + library +
                    ", and the table holds " + libraryCount);
            }

            int length = r.UVarCount(1, "name");
            string text = length == 0
                ? string.Empty
                : System.Text.Encoding.UTF8.GetString(r.Bytes(length));

            names[i] = new QualifiedName(libraries[library], text);
        }
        return names;
    }

    /// <summary>Version 1: every name carried its library's 128 bits beside it. Frozen.</summary>
    static QualifiedName[] ReadNamesV1(ByteReader r)
    {
        // A legacy name needs at least 16 identity bytes and a four-byte length.
        int count = r.Count(8 + 8 + 4, "name table");

        QualifiedName[] names = new QualifiedName[count];
        for (int i = 0; i < count; i++)
        {
            ulong hi = r.U64();
            ulong lo = r.U64();
            names[i] = new QualifiedName(new SourceId(hi, lo), r.Str());
        }
        return names;
    }

    /// <summary>
    /// Every live node, version 5: runs of consecutive ids, each node written once. A parent is
    /// read off its parent's child list, so it is set after every node is in place; a payload whose
    /// length is not written has the length its kind's schema fixes. See <see cref="SourceWrite"/>.
    /// </summary>
    static unsafe void ReadNodesV5(
        ByteReader r, ref SourceFile file, KindId[] kinds, RelationId[] relations, long nextId, in MappedFile mapping)
    {
        if (nextId < 0)
        {
            throw new FormatCorruptException("a version 5 source has no next id in its identity section");
        }
        if (nextId > int.MaxValue)
        {
            throw new FormatCorruptException("the source claims " + nextId + " ids, more than this build can hold");
        }

        int count = (int)nextId;
        Source src = Source.Create(count < 4 ? 4 : count);
        src.Identity = file.Src.Identity;
        src.Root = file.Src.Root;

        int mapped = mapping.Base == null
            ? -1
            : Source.AddMappedSegment(ref src, mapping.Base, mapping.Length);

        Source.EnsureNodeCapacity(ref src, count);
        src.NodeCount = count;
        for (int i = 0; i < count; i++)
        {
            src.Nodes[i] = default;
            src.Nodes[i].Parent = NodeId.None;
            src.Capacities[i] = default;
            src.NodeRevision[i] = 0;
        }

        // Fixed lengths by file slot, so a node whose length is not written can be read.
        int[] kindLength = new int[kinds.Length];
        for (int k = 0; k < kinds.Length; k++)
        {
            kindLength[k] = KindTable.HasSchema(in file.Table, kinds[k])
                ? KindTable.SchemaOf(in file.Table, kinds[k]).Payload.FixedLength
                : -1;
        }
        int[] relationLength = new int[relations.Length];
        for (int e = 0; e < relations.Length; e++)
        {
            relationLength[e] = KindTable.RelationSchemaOf(in file.Table, relations[e]).Payload.FixedLength;
        }

        int runs = r.UVarCount(2, "node runs");
        long at = 0;
        for (int run = 0; run < runs; run++)
        {
            at += (long)r.UVar();
            long length = (long)r.UVar();
            if (at + length > count)
            {
                throw new FormatCorruptException(
                    "a node run reaches id " + (at + length) + ", past the next id " + count);
            }

            for (long i = at; i < at + length; i++)
            {
                ReadNodeV5(ref r, ref src, i, count, kinds, relations, kindLength, relationLength, mapped);
            }
            at += length;
        }

        // Containment from the child lists: one parent each, and only a live node can be one.
        for (int i = 0; i < count; i++)
        {
            int offset = src.Nodes[i].ChildOffset;
            for (int c = 0; c < src.Nodes[i].ChildCount; c++)
            {
                long child = src.Children[offset + c];
                if (!src.Nodes[child].IsLive)
                {
                    throw new FormatCorruptException("node " + i + " contains " + child + ", which the file does not hold");
                }
                if (src.Nodes[child].Parent.IsSome)
                {
                    throw new FormatCorruptException("node " + child + " is contained twice");
                }
                src.Nodes[child].Parent = new NodeId(i);
            }
        }

        file.Src = src;
    }

    static unsafe void ReadNodeV5(
        ref ByteReader r, ref Source src, long i, int count, KindId[] kinds, RelationId[] relations,
        int[] kindLength, int[] relationLength, int mapped)
    {
        src.Nodes[i].Flags = NodeFlags.Live;

        ulong kindField = r.UVar();
        int kindSlot = (int)(kindField >> 1) - 1;
        src.Nodes[i].Kind = kindSlot >= 0 && kindSlot < kinds.Length ? kinds[kindSlot] : KindId.Unknown;

        long payloadLength;
        if ((kindField & 1) != 0)
        {
            payloadLength = r.UVarLength("payload");
        }
        else if (kindSlot >= 0 && kindSlot < kindLength.Length && kindLength[kindSlot] >= 0)
        {
            payloadLength = kindLength[kindSlot];
        }
        else
        {
            throw new FormatCorruptException("node " + i + " leaves its payload length to a schema that does not fix one");
        }
        if (payloadLength > 0)
        {
            ReadPayload(ref src, ref r, i, payloadLength, mapped);
        }

        int childCount = r.UVarCount(1, "child run");
        if (childCount > 0)
        {
            int offset = Source.ReserveChildren(ref src, childCount);
            long previous = i;
            for (int c = 0; c < childCount; c++)
            {
                long slot = r.SVar() + previous;
                if (slot < 0 || slot >= count)
                {
                    throw new FormatCorruptException(
                        "node " + i + " names child " + slot + ", but the next id is " + count);
                }
                src.Children[offset + c] = slot;
                previous = slot;
            }
            src.Nodes[i].ChildOffset = offset;
            src.Nodes[i].ChildCount = childCount;
            src.Capacities[i].Child = childCount;
        }

        // A relation and a target: two bytes even when both are empty.
        int refCount = r.UVarCount(2, "reference run");
        if (refCount > 0)
        {
            int offset = Source.ReserveRefs(ref src, refCount);
            for (int e = 0; e < refCount; e++)
            {
                ulong relationField = r.UVar();
                int relationSlot = (int)(relationField >> 1) - 1;

                // Biased by one on the way out, so zero is `none`.
                long target = (long)r.UVar() - 1;
                if (target >= count)
                {
                    throw new FormatCorruptException(
                        "node " + i + " references " + target + ", but the next id is " + count);
                }

                int dataLength;
                if ((relationField & 1) != 0)
                {
                    dataLength = r.UVarCount(1, "edge data");
                }
                else if (relationSlot >= 0 && relationSlot < relationLength.Length && relationLength[relationSlot] >= 0)
                {
                    dataLength = relationLength[relationSlot];
                }
                else
                {
                    throw new FormatCorruptException("a reference leaves its data length to a relation that does not fix one");
                }

                Ref edge = default;
                edge.Relation = relationSlot >= 0 && relationSlot < relations.Length
                    ? relations[relationSlot]
                    : RelationId.Unknown;
                edge.Target = new NodeId(target);

                if (dataLength > 0)
                {
                    int dataAt = Source.ReserveEdgeData(ref src, dataLength);
                    r.Bytes(dataLength).CopyTo(new Span<byte>(src.EdgeData, dataAt, dataLength));
                    edge.DataOffset = dataAt;
                    edge.DataLength = dataLength;
                }

                src.Refs[offset + e] = edge;
            }
            src.Nodes[i].RefOffset = offset;
            src.Nodes[i].RefCount = refCount;
            src.Capacities[i].Ref = refCount;
        }
    }

    /// <summary>
    /// Every slot, versions 2 to 4: flags, kind, a parent and every run, deleted slots included,
    /// with variable-width numbers and indices relative to the node holding them. Frozen.
    /// </summary>
    static unsafe void ReadNodes(
        ByteReader r, ref SourceFile file, KindId[] kinds, RelationId[] relations, in MappedFile mapping)
    {
        // Flags, kind, parent, and three counts, each one byte at its smallest.
        const int SmallestNode = 1 + 1 + 1 + 1 + 1 + 1;
        int count = r.UVarCount(SmallestNode, "node section");

        Source src = Source.Create(count < 4 ? 4 : count);
        src.Identity = file.Src.Identity;
        src.Root = file.Src.Root;

        // The file itself, as a segment payloads may point into. Registered here because
        // this is where the source they belong to comes into existence.
        int mapped = mapping.Base == null
            ? -1
            : Source.AddMappedSegment(ref src, mapping.Base, mapping.Length);

        // Every slot exists before any link is restored, because a parent may appear after
        // its child in slot order and a reference may point anywhere.
        Source.EnsureNodeCapacity(ref src, count);
        src.NodeCount = count;

        for (int i = 0; i < count; i++)
        {
            src.Nodes[i] = default;
            src.Capacities[i] = default;
            src.NodeRevision[i] = 0;

            src.Nodes[i].Flags = (NodeFlags)r.U8();

            int kindSlot = r.SVarInt();
            src.Nodes[i].Kind = kindSlot >= 0 && kindSlot < kinds.Length
                ? kinds[kindSlot]
                : KindId.Unknown;

            long parent = (long)r.SVarInt() + i;
            if (parent < -1 || parent >= count)
            {
                throw new FormatCorruptException(
                    "node " + i + " names parent slot " + parent +
                    ", but the source has " + count + " nodes");
            }
            src.Nodes[i].Parent = new NodeId((int)parent);

            long payloadLength = r.UVarLength("payload");
            if (payloadLength > 0)
            {
                ReadPayload(ref src, ref r, i, payloadLength, mapped);
            }

            int childCount = r.UVarCount(1, "child run");
            if (childCount > 0)
            {
                int offset = Source.ReserveChildren(ref src, childCount);
                long previous = i;

                for (int c = 0; c < childCount; c++)
                {
                    long slot = (long)r.SVarInt() + previous;

                    // Containment indices must name allocated slots; unlike references, child
                    // links cannot dangle.
                    if (slot < 0 || slot >= count)
                    {
                        throw new FormatCorruptException(
                            "node " + i + " names child slot " + slot +
                            ", but the source has " + count + " nodes");
                    }

                    src.Children[offset + c] = (int)slot;
                    previous = slot;
                }
                src.Nodes[i].ChildOffset = offset;
                src.Nodes[i].ChildCount = childCount;
                src.Capacities[i].Child = childCount;
            }

            // A relation, a target and a label length: three bytes even when all are empty.
            int refCount = r.UVarCount(3, "reference run");
            if (refCount > 0)
            {
                int offset = Source.ReserveRefs(ref src, refCount);
                for (int e = 0; e < refCount; e++)
                {
                    int relationSlot = r.SVarInt();

                    // Biased by one on the way out, so zero is `none`.
                    long target = (long)r.UVarInt() - 1;
                    if (target >= count)
                    {
                        throw new FormatCorruptException(
                            "node " + i + " references slot " + target +
                            ", but the source has " + count + " nodes");
                    }

                    int labelLength = r.UVarCount(1, "reference label");

                    Ref edge = default;
                    edge.Relation = relationSlot >= 0 && relationSlot < relations.Length
                        ? relations[relationSlot]
                        : RelationId.Unknown;
                    edge.Target = new NodeId((int)target);

                    if (labelLength > 0)
                    {
                        int labelAt = Source.ReserveEdgeData(ref src, labelLength);
                        r.Bytes(labelLength).CopyTo(new Span<byte>(src.EdgeData, labelAt, labelLength));
                        edge.DataOffset = labelAt;
                        edge.DataLength = labelLength;
                    }

                    src.Refs[offset + e] = edge;
                }
                src.Nodes[i].RefOffset = offset;
                src.Nodes[i].RefCount = refCount;
                src.Capacities[i].Ref = refCount;
            }
        }

        file.Src = src;
    }

    /// <summary>Version 1: every index and count a fixed four bytes. Frozen.</summary>
    static unsafe void ReadNodesV1(
        ByteReader r, ref SourceFile file, KindId[] kinds, RelationId[] relations, in MappedFile mapping)
    {
        // Bound node allocation by the smallest possible node encoding.
        const int SmallestNode = 1 + 4 + 4 + 4 + 4 + 4;
        int count = r.Count(SmallestNode, "node section");

        Source src = Source.Create(count < 4 ? 4 : count);
        src.Identity = file.Src.Identity;
        src.Root = file.Src.Root;

        // The file itself, as a segment payloads may point into. Registered here because
        // this is where the source they belong to comes into existence.
        int mapped = mapping.Base == null
            ? -1
            : Source.AddMappedSegment(ref src, mapping.Base, mapping.Length);

        // Every slot exists before any link is restored, because a parent may appear after
        // its child in slot order and a reference may point anywhere.
        Source.EnsureNodeCapacity(ref src, count);
        src.NodeCount = count;

        for (int i = 0; i < count; i++)
        {
            src.Nodes[i] = default;
            src.Capacities[i] = default;
            src.NodeRevision[i] = 0;

            NodeFlags flags = (NodeFlags)r.U8();
            int kindSlot = r.I32();
            int parent = r.I32();

            src.Nodes[i].Flags = flags;
            src.Nodes[i].Kind = kindSlot >= 0 && kindSlot < kinds.Length
                ? kinds[kindSlot]
                : KindId.Unknown;
            if (parent < -1 || parent >= count)
            {
                throw new FormatCorruptException(
                    "node " + i + " names parent slot " + parent +
                    ", but the source has " + count + " nodes");
            }
            src.Nodes[i].Parent = new NodeId(parent);

            int payloadLength = r.Count(1, "payload");
            if (payloadLength > 0)
            {
                ReadPayload(ref src, ref r, i, payloadLength, mapped);
            }

            int childCount = r.Count(4, "child run");
            if (childCount > 0)
            {
                int offset = Source.ReserveChildren(ref src, childCount);
                for (int c = 0; c < childCount; c++)
                {
                    int slot = r.I32();

                    // Containment indices must name allocated slots; unlike references, child
                    // links cannot dangle.
                    if (slot < 0 || slot >= count)
                    {
                        throw new FormatCorruptException(
                            "node " + i + " names child slot " + slot +
                            ", but the source has " + count + " nodes");
                    }

                    src.Children[offset + c] = slot;
                }
                src.Nodes[i].ChildOffset = offset;
                src.Nodes[i].ChildCount = childCount;
                src.Capacities[i].Child = childCount;
            }

            // relation, target, and a label length: twelve bytes even when the label is empty.
            int refCount = r.Count(12, "reference run");
            if (refCount > 0)
            {
                int offset = Source.ReserveRefs(ref src, refCount);
                for (int e = 0; e < refCount; e++)
                {
                    int relationSlot = r.I32();
                    int target = r.I32();
                    int labelLength = r.Count(1, "reference label");

                    Ref edge = default;
                    edge.Relation = relationSlot >= 0 && relationSlot < relations.Length
                        ? relations[relationSlot]
                        : RelationId.Unknown;
                    edge.Target = new NodeId(target);

                    if (labelLength > 0)
                    {
                        int labelAt = Source.ReserveEdgeData(ref src, labelLength);
                        r.Bytes(labelLength).CopyTo(new Span<byte>(src.EdgeData, labelAt, labelLength));
                        edge.DataOffset = labelAt;
                        edge.DataLength = labelLength;
                    }

                    src.Refs[offset + e] = edge;
                }
                src.Nodes[i].RefOffset = offset;
                src.Nodes[i].RefCount = refCount;
                src.Capacities[i].Ref = refCount;
            }
        }

        file.Src = src;
    }

    /// <summary>Keep a section's bytes verbatim so writing the file back does not destroy it.</summary>
    static void Preserve(ref SourceFile file, uint id, byte[] bytes)
    {
        if (file.UnknownCount == file.UnknownSections.Length)
        {
            Array.Resize(ref file.UnknownSections, file.UnknownCount + 4);
        }

        file.UnknownSections[file.UnknownCount].Id = id;
        file.UnknownSections[file.UnknownCount].Bytes = bytes;
        file.UnknownCount++;
    }
}
