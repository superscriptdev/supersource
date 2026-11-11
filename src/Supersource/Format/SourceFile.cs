using System;
using Supersource.Kinds;

namespace Supersource.Format;
/// <summary>Length-framed section identifiers. Unknown sections are preserved verbatim.</summary>
public enum SectionId : uint
{
    /// <summary>Source identity, root node ID and next id.</summary>
    Identity = 1,

    /// <summary>Qualified kind names in file-slot order.</summary>
    KindNames = 2,

    /// <summary>Qualified relation names in file-slot order.</summary>
    RelationNames = 3,

    /// <summary>The live nodes, as runs of consecutive ids: kind, payload, children, references.</summary>
    Nodes = 4,

    /// <summary>
    /// Retired. Once held every operation ever applied to the source. Readers skip it and writers
    /// drop it: nothing needs it to reconstruct the graph, and a file holds a state rather than the
    /// edits that produced it. The id is never reused.
    /// </summary>
    RetiredHistory = 5,

    /// <summary>
    /// SHA-256 digest of non-journal section contents, excluding this section and framing headers.
    /// Journals carry separate digests.
    /// </summary>
    Checksum = 6,

    /// <summary>
    /// Operations appended after the saved graph, replayed in file order. Multiple journals are
    /// permitted, each with its own content digest.
    /// </summary>
    Journal = 7,

    /// <summary>
    /// The embedded schemas: what each kind the file names declares, and what each relation's
    /// edges carry. A file before major 5 holds them as nodes in its graph instead.
    /// </summary>
    Schemas = 8,
}

/// <summary>How a <see cref="SectionId.Checksum"/> was computed.</summary>
public enum DigestKind : byte
{
    /// <summary>Nothing recorded.</summary>
    None = 0,

    /// <summary>SHA-256 over the content bytes of every other section but the journals, in file order.</summary>
    Sha256 = 1,
}

/// <summary>Section identifier, file offset and byte length obtained without decoding content.</summary>
public struct SectionSpan
{
    public uint Id;

    /// <summary>Offset of the section's bytes, past its own header.</summary>
    public long At;

    public long Length;
}

/// <summary>Borrowed mapped bytes. The mapping must remain open while payloads reference them.</summary>
public unsafe struct MappedFile
{
    public byte* Base;
    public long Length;
}

/// <summary>Unrecognized section bytes retained for rewriting.</summary>
public struct UnknownSection
{
    public uint Id;
    public byte[] Bytes;
}

/// <summary>
/// A source graph, its interned names, the operations applied since it was opened, and file
/// metadata. Preserves unknown sections and their order for rewriting.
/// </summary>
public struct SourceFile
{
    public Source Src;
    public KindTable Table;

    /// <summary>
    /// The operations applied since the source was opened or created. Empty after a read, since a
    /// file stores no edits; a save appends the entries since the previous save as a journal.
    /// </summary>
    public Timeline.Log Log;

    /// <summary>
    /// The major format version the source was read from, or the current one for a source created
    /// in memory. Only a file already in the current version can be appended to.
    /// </summary>
    public ushort Major;

    /// <summary>Sections this build could not interpret, in file order.</summary>
    public UnknownSection[] UnknownSections;
    public int UnknownCount;

    /// <summary>
    /// The file's section ids in file order, as read or last saved: what an append adds a journal
    /// to and counts. A full write lays sections out in the canonical order regardless.
    /// </summary>
    public uint[] SectionOrder;
    public int SectionOrderCount;

    /// <summary>
    /// Owns the file mapping used by borrowed payloads. Keep open until those payloads are no
    /// longer used, or compact the source into owned storage first.
    /// </summary>
    public System.IO.MemoryMappedFiles.MemoryMappedFile Mapping;
    public System.IO.MemoryMappedFiles.MemoryMappedViewAccessor View;

    /// <summary>
    /// Checksum recorded on load, journals folded in, not verified automatically. Save uses it to
    /// detect a file changed since; SourceRead.Verify recomputes it from the bytes.
    /// </summary>
    public Kinds.ContentHash Digest;
    public DigestKind DigestKind;

    /// <summary>
    /// The log position and source stamp at the last save. Appending requires a complete chain of
    /// logged operations from this state.
    /// </summary>
    public int SavedAt;
    public ulong SavedStamp;

    /// <summary>The kind table's revision at the last save, so a changed table forces a full write.</summary>
    public int SavedTableRevision;

    /// <summary>
    /// Handle-to-file-slot maps, with -1 for absent names. Journals must use the original file’s
    /// slots rather than recomputing first-use order after edits.
    /// </summary>
    public int[] KindSlot;
    public int[] RelationSlot;

    /// <summary>
    /// Closes the mapping. Borrowed payload spans become invalid; compact into owned storage first
    /// if the source must remain usable.
    /// </summary>
    public static void Close(ref SourceFile file)
    {
        if (file.View != null)
        {
            file.View.SafeMemoryMappedViewHandle.ReleasePointer();
            file.View.Dispose();
            file.View = null;
        }

        if (file.Mapping != null)
        {
            file.Mapping.Dispose();
            file.Mapping = null;
        }
    }

    /// <summary>An empty source and an empty kind table. The normal way to start one.</summary>
    public static SourceFile Create(int nodeCapacity = 256)
    {
        SourceFile file = default;
        file.Table = KindTable.Create(64);
        file.Src = Source.Create(nodeCapacity);
        file.UnknownSections = new UnknownSection[0];
        file.UnknownCount = 0;
        file.SectionOrder = new uint[0];
        file.SectionOrderCount = 0;
        file.Log = Timeline.Log.Create();
        file.Major = SourceWrite.FormatMajor;
        return file;
    }

    /// <summary>
    /// An independent in-memory copy: graph, kind table, log and preserved unknown sections.
    /// Node IDs, kind handles and relation handles keep their meaning, so a <see cref="NodeId"/> or
    /// <see cref="Kinds.KindId"/> read from the original addresses the same thing in the copy.
    ///
    /// The copy owns its payload bytes and holds no mapping, so it stays usable after the original
    /// is closed, and runs are sized to their used lengths as <see cref="SourceCompact"/> does.
    /// Because that relocates the arenas, the copy carries no saved-digest or incremental-append
    /// state: saving it writes a complete file. It is a copy of this source, not a new one —
    /// <see cref="Source.Identity"/> is retained, and giving the copy a separate identity is the
    /// caller's explicit act.
    /// </summary>
    public static SourceFile Clone(in SourceFile file)
    {
        SourceFile into = default;
        into.Src = SourceCompact.Compact(in file.Src);
        into.Table = KindTable.Clone(in file.Table);
        into.Major = file.Major;
        into.Log = Timeline.Log.Clone(in file.Log);

        into.UnknownCount = file.UnknownCount;
        into.UnknownSections = new UnknownSection[file.UnknownCount < 1 ? 0 : file.UnknownCount];
        for (int i = 0; i < file.UnknownCount; i++)
        {
            into.UnknownSections[i].Id = file.UnknownSections[i].Id;
            into.UnknownSections[i].Bytes = file.UnknownSections[i].Bytes == null
                ? null
                : (byte[])file.UnknownSections[i].Bytes.Clone();
        }

        into.SectionOrderCount = file.SectionOrderCount;
        into.SectionOrder = new uint[file.SectionOrderCount < 1 ? 0 : file.SectionOrderCount];
        for (int i = 0; i < file.SectionOrderCount; i++)
        {
            into.SectionOrder[i] = file.SectionOrder[i];
        }

        return into;
    }
}
