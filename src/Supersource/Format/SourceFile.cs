using System;
using Supersource.Kinds;

namespace Supersource.Format;
/// <summary>Length-framed section identifiers. Unknown sections are preserved verbatim.</summary>
public enum SectionId : uint
{
    /// <summary>Source identity and root node ID.</summary>
    Identity = 1,

    /// <summary>Qualified kind names in file-slot order.</summary>
    KindNames = 2,

    /// <summary>Durable reference relation names, in canonical order.</summary>
    RelationNames = 3,

    /// <summary>Every node: kind, parent, payload, children, refs.</summary>
    Nodes = 4,

    /// <summary>Stored applied operation history; unknown readers can preserve it as a framed section.</summary>
    History = 5,

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
}

/// <summary>How a <see cref="SectionId.Checksum"/> was computed.</summary>
public enum DigestKind : byte
{
    /// <summary>Nothing recorded.</summary>
    None = 0,

    /// <summary>SHA-256 over the content bytes of every other section, in file order.</summary>
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
/// A source graph, its interned names, history and file metadata. Preserves unknown sections and
/// their order for rewriting.
/// </summary>
public struct SourceFile
{
    public Source Src;
    public KindTable Table;
    public KindBootstrap.Ids Boot;

    /// <summary>Recorded operation history. Empty when the file has no readable history section.</summary>
    public Timeline.Log History;

    /// <summary>Sections this build could not interpret, in file order.</summary>
    public UnknownSection[] UnknownSections;
    public int UnknownCount;

    /// <summary>Original section order. An empty list requests canonical order for a newly written file.</summary>
    public uint[] SectionOrder;
    public int SectionOrderCount;

    /// <summary>
    /// Owns the file mapping used by borrowed payloads. Keep open until those payloads are no
    /// longer used, or compact the source into owned storage first.
    /// </summary>
    public System.IO.MemoryMappedFiles.MemoryMappedFile Mapping;
    public System.IO.MemoryMappedFiles.MemoryMappedViewAccessor View;

    /// <summary>
    /// Digest recorded on load, not verified automatically. Save uses it to detect a changed stored
    /// digest; Verify recomputes content hashes.
    /// </summary>
    public Kinds.ContentHash Digest;
    public DigestKind DigestKind;

    /// <summary>
    /// Saved history position and source stamp. Appending requires a complete chain of logged
    /// operations from this state.
    /// </summary>
    public int SavedAt;
    public ulong SavedStamp;

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

    /// <summary>An empty source with the bootstrap installed. The normal way to start one.</summary>
    public static SourceFile Create(int nodeCapacity = 256)
    {
        SourceFile file = default;
        file.Table = KindBootstrap.CreateTable(out file.Boot);
        file.Src = Source.Create(nodeCapacity);
        file.UnknownSections = new UnknownSection[0];
        file.UnknownCount = 0;
        file.SectionOrder = new uint[0];
        file.SectionOrderCount = 0;
        file.History = Timeline.Log.Create();
        return file;
    }
}
