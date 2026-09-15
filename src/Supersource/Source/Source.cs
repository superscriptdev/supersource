using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supersource;

/// <summary>Capacities of a node’s arena runs, stored separately from traversal data.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RunCapacity
{
    public int Child;
    public int Ref;
    public int Payload;
}

/// <summary>
/// An owned byte array or a borrowed mapped region. Exactly one of Array and Base is set. Editing
/// mapped payloads copies them into owned storage.
/// </summary>
public unsafe struct BlobSegment
{
    /// <summary>Bytes this source allocated and may write.</summary>
    public byte[] Array;

    /// <summary>Bytes of a mapping this source may only read. Valid while the mapping is open.</summary>
    public byte* Base;

    /// <summary>How much of an owned segment has been handed out. Meaningless for a mapped one.</summary>
    public int Used;

    /// <summary>How many bytes the segment covers.</summary>
    public long Length;

    public bool IsMapped
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Array == null; }
    }
}

public struct Source
{
    /// <summary>
    /// Incremented by SourceBuild mutators, including calls that may leave content unchanged. Use
    /// with source identity and Stamp when a cache can outlive one source instance.
    /// </summary>
    public int Revision;

    /// <summary>
    /// A mutation-sequence stamp used to distinguish states with equal revision counts. Not a
    /// content hash: different construction sequences may produce equal content with different
    /// stamps.
    /// </summary>
    public ulong Stamp;

    public SourceId Identity;

    /// <summary>Nodes indexed by durable slot ID, including retained deleted slots.</summary>
    public Node[] Nodes;

    /// <summary>
    /// Number of allocated IDs, including deleted nodes. Allocation increases it; deletion does
    /// not.
    /// </summary>
    public long NodeCount;

    /// <summary>Cold, parallel to <see cref="Nodes"/>: how much room each run has.</summary>
    public RunCapacity[] Capacities;

    /// <summary>Per-node cache revisions, also incremented when a descendant changes. Not serialized.</summary>
    public int[] NodeRevision;

    /// <summary>Ordered child runs containing source-local node indices.</summary>
    public long[] Children;
    public int ChildArenaUsed;

    /// <summary>Reference runs.</summary>
    public Ref[] Refs;
    public int RefArenaUsed;

    /// <summary>
    /// Payload segments. A payload never straddles segments, so it can be exposed as a contiguous
    /// span. The address encodes a segment index and offset using BlobOffsetBits.
    /// </summary>
    public BlobSegment[] Blob;
    public int BlobSegments;

    /// <summary>
    /// Bytes carried by references, including unbound labels and bound edge data. Offsets are local
    /// to this source.
    /// </summary>
    public byte[] Labels;
    public int LabelsUsed;

    /// <summary>Root node, or None before one is assigned.</summary>
    public NodeId Root;

    public static Source Create(int nodeCapacity = 256)
    {
        if (nodeCapacity < 4)
        {
            nodeCapacity = 4;
        }

        Source src = default;
        src.Identity = SourceId.None;
        src.Nodes = new Node[nodeCapacity];
        src.Capacities = new RunCapacity[nodeCapacity];
        src.NodeRevision = new int[nodeCapacity];
        src.NodeCount = 0;
        src.Children = new long[nodeCapacity * 2];
        src.ChildArenaUsed = 0;
        src.Refs = new Ref[nodeCapacity / 2];
        src.RefArenaUsed = 0;
        src.Blob = new BlobSegment[4];
        src.Blob[0].Array = new byte[nodeCapacity * 8];
        src.Blob[0].Length = src.Blob[0].Array.Length;
        src.BlobSegments = 1;
        src.Labels = new byte[64];
        src.LabelsUsed = 0;
        src.Root = NodeId.None;
        return src;
    }

    /// <summary>True if the id names a slot that currently holds a node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLive(NodeId id)
    {
        return id.Index >= 0
            && id.Index < NodeCount
            && (Nodes[id.Index].Flags & NodeFlags.Live) != 0;
    }

    /// <summary>Returns whether the slot was allocated, including deleted nodes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WasAllocated(NodeId id)
    {
        return id.Index >= 0 && id.Index < NodeCount;
    }

    /// <summary>Number of allocated nodes currently marked deleted. Undo may restore them.</summary>
    public int DeletedNodeCount()
    {
        int dead = 0;
        for (long i = 0; i < NodeCount; i++)
        {
            if ((Nodes[i].Flags & NodeFlags.Live) == 0)
            {
                dead++;
            }
        }
        return dead;
    }

    // Runs are bump-allocated; relocating one leaves its old storage for compaction.

    public static int ReserveChildren(ref Source src, int count)
    {
        EnsureChildCapacity(ref src, src.ChildArenaUsed + count);
        int offset = src.ChildArenaUsed;
        src.ChildArenaUsed += count;
        return offset;
    }

    public static int ReserveRefs(ref Source src, int count)
    {
        EnsureRefCapacity(ref src, src.RefArenaUsed + count);
        int offset = src.RefArenaUsed;
        src.RefArenaUsed += count;
        return offset;
    }

    /// <summary>Default size of an owned payload segment. Larger payloads get a segment sized to fit.</summary>
    public const int BlobSegmentBytes = 1 << 20;

    /// <summary>
    /// Number of address bits reserved for the offset within a segment. Mapped regions can exceed
    /// the size of a managed array.
    /// </summary>
    public const int BlobOffsetBits = 40;

    /// <summary>Which segment an address names.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SegmentOf(long address)
    {
        return (int)(address >> BlobOffsetBits);
    }

    /// <summary>How far into that segment it names.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long OffsetOf(long address)
    {
        return address & ((1L << BlobOffsetBits) - 1);
    }

    /// <summary>Returns a span over owned or mapped payload bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ReadOnlySpan<byte> Bytes(in Source src, long address, int length)
    {
        ref BlobSegment segment = ref src.Blob[SegmentOf(address)];
        long offset = OffsetOf(address);

        return segment.Array != null
            ? new ReadOnlySpan<byte>(segment.Array, (int)offset, length)
            : new ReadOnlySpan<byte>(segment.Base + offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long BlobAddress(int segment, long offset)
    {
        return ((long)segment << BlobOffsetBits) | (offset & ((1L << BlobOffsetBits) - 1));
    }

    /// <summary>
    /// Registers borrowed mapped memory without copying. The caller must keep the mapping open
    /// while the source uses it.
    /// </summary>
    public static unsafe int AddMappedSegment(ref Source src, byte* mapped, long length)
    {
        if (src.BlobSegments == src.Blob.Length)
        {
            Array.Resize(ref src.Blob, src.Blob.Length * 2);
        }

        int at = src.BlobSegments;
        src.Blob[at] = default;
        src.Blob[at].Base = mapped;
        src.Blob[at].Length = length;
        src.BlobSegments++;
        return at;
    }

    /// <summary>
    /// Reserves contiguous bytes within one owned segment. If the current segment cannot fit them,
    /// allocates another.
    /// </summary>
    public static long ReserveBlob(ref Source src, int count)
    {
        int last = src.BlobSegments - 1;

        // A mapped segment is never written to, so it is never the one with room.
        if (last < 0 || src.Blob[last].IsMapped
            || src.Blob[last].Used + count > src.Blob[last].Array.Length)
        {
            if (src.BlobSegments == src.Blob.Length)
            {
                Array.Resize(ref src.Blob, src.Blob.Length * 2);
            }

            // A payload bigger than a segment gets one of its own rather than being split.
            int size = count > BlobSegmentBytes ? count : BlobSegmentBytes;
            last = src.BlobSegments;
            src.Blob[last] = default;
            src.Blob[last].Array = new byte[size];
            src.Blob[last].Length = size;
            src.BlobSegments++;
        }

        int offset = src.Blob[last].Used;
        src.Blob[last].Used += count;
        return BlobAddress(last, offset);
    }

    /// <summary>Room for a reference label. A plain bump: labels are short and few.</summary>
    public static int ReserveLabel(ref Source src, int count)
    {
        int needed = src.LabelsUsed + count;
        if (src.Labels.Length < needed)
        {
            int size = src.Labels.Length < 16 ? 16 : src.Labels.Length;
            while (size < needed)
            {
                size *= 2;
            }
            Array.Resize(ref src.Labels, size);
        }

        int offset = src.LabelsUsed;
        src.LabelsUsed += count;
        return offset;
    }

    /// <summary>Resizes the parallel node, capacity and revision arrays together.</summary>
    public static void EnsureNodeCapacity(ref Source src, long needed)
    {
        if (src.Nodes.Length >= needed)
        {
            return;
        }

        int size = src.Nodes.Length < 4 ? 4 : src.Nodes.Length;
        while (size < needed)
        {
            size *= 2;
        }

        Array.Resize(ref src.Nodes, size);
        Array.Resize(ref src.Capacities, size);
        Array.Resize(ref src.NodeRevision, size);
    }

    static void EnsureChildCapacity(ref Source src, int needed)
    {
        if (src.Children.Length >= needed)
        {
            return;
        }

        int size = src.Children.Length < 4 ? 4 : src.Children.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref src.Children, size);
    }

    static void EnsureRefCapacity(ref Source src, int needed)
    {
        if (src.Refs.Length >= needed)
        {
            return;
        }

        int size = src.Refs.Length < 4 ? 4 : src.Refs.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref src.Refs, size);
    }

}
