using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supersource;

/// <summary>Capacities of a node’s arena runs, stored separately from traversal data.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RunCapacity
{
    /// <summary>Bytes reserved for the payload: 64-bit, since one payload may be larger than an array.</summary>
    public long Payload;

    public int Child;
    public int Ref;
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

    /// <summary>
    /// Owned bytes held as equal chunks, for one payload larger than an array can be. A chunked
    /// segment holds that payload alone, and is read and written in windows.
    /// </summary>
    public byte[][] Chunks;

    /// <summary>The size of every chunk but the last, when <see cref="Chunks"/> is set.</summary>
    public int ChunkBytes;

    /// <summary>How much of an owned segment has been handed out. Meaningless for a mapped one.</summary>
    public int Used;

    /// <summary>How many bytes the segment covers.</summary>
    public long Length;

    public bool IsMapped
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Array == null && Chunks == null; }
    }

    public bool IsChunked
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Chunks != null; }
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

    /// <summary>Nodes indexed by durable slot ID, including the empty slots of deleted nodes.</summary>
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
    /// Payload segments. A payload never straddles segments; one held in chunks is still one
    /// segment, but not one span (see <see cref="IsContiguous"/>). The address encodes a segment
    /// index and offset using BlobOffsetBits.
    /// </summary>
    public BlobSegment[] Blob;
    public int BlobSegments;

    /// <summary>
    /// Bytes carried by references, including unbound labels and bound edge data. Offsets are local
    /// to this source.
    /// </summary>
    public byte[] EdgeData;
    public int EdgeDataUsed;

    /// <summary>Root node, or None before one is assigned.</summary>
    public NodeId Root;

    /// <summary>
    /// The size of the chunks an owned payload is held in once it is larger than one chunk; zero
    /// means <see cref="DefaultChunkBytes"/>. Not stored: how a source holds its bytes in memory
    /// is its own, and the file is the same either way.
    /// </summary>
    public int ChunkBytes;

    /// <summary>One gibibyte: comfortably under the largest array, and few chunks for any payload.</summary>
    public const int DefaultChunkBytes = 1 << 30;

    public int ChunkLimit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return ChunkBytes > 0 ? ChunkBytes : DefaultChunkBytes; }
    }

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
        src.EdgeData = new byte[64];
        src.EdgeDataUsed = 0;
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

    /// <summary>Number of ids below the next id that hold no node. Undo may restore them.</summary>
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

        if (segment.Array != null) return new ReadOnlySpan<byte>(segment.Array, (int)offset, length);
        if (segment.Chunks == null) return new ReadOnlySpan<byte>(segment.Base + offset, length);

        // A span is one run of memory: inside one chunk it is that chunk's, across two it cannot be.
        int chunk = (int)(offset / segment.ChunkBytes);
        int within = (int)(offset % segment.ChunkBytes);
        if (within + (long)length > segment.Chunks[chunk].Length)
            throw new InvalidOperationException("these payload bytes cross a chunk; read them in windows with Source.Read");
        return new ReadOnlySpan<byte>(segment.Chunks[chunk], within, length);
    }

    /// <summary>Whether these payload bytes are one run of memory, which a span can cover.</summary>
    public static bool IsContiguous(in Source src, long address, long length)
    {
        if (length > int.MaxValue) return false;
        ref BlobSegment segment = ref src.Blob[SegmentOf(address)];
        if (segment.Chunks == null) return true;
        long offset = OffsetOf(address);
        int within = (int)(offset % segment.ChunkBytes);
        return within + length <= segment.Chunks[(int)(offset / segment.ChunkBytes)].Length;
    }

    /// <summary>
    /// Copy <paramref name="into"/>.Length bytes from <paramref name="offset"/> into the payload at
    /// <paramref name="address"/>: the windowed read, which works for a payload of any size held any way.
    /// </summary>
    public static unsafe void Read(in Source src, long address, long offset, Span<byte> into)
    {
        ref BlobSegment segment = ref src.Blob[SegmentOf(address)];
        long at = OffsetOf(address) + offset;
        if (segment.Array != null) { new ReadOnlySpan<byte>(segment.Array, checked((int)at), into.Length).CopyTo(into); return; }
        if (segment.Chunks == null) { new ReadOnlySpan<byte>(segment.Base + at, into.Length).CopyTo(into); return; }
        int done = 0;
        while (done < into.Length)
        {
            int chunk = (int)((at + done) / segment.ChunkBytes);
            int within = (int)((at + done) % segment.ChunkBytes);
            int count = Math.Min(into.Length - done, segment.Chunks[chunk].Length - within);
            new ReadOnlySpan<byte>(segment.Chunks[chunk], within, count).CopyTo(into.Slice(done, count));
            done += count;
        }
    }

    /// <summary>Copy bytes into owned payload storage at an offset: the windowed write. Never into a mapping.</summary>
    public static void Write(ref Source src, long address, long offset, ReadOnlySpan<byte> from)
    {
        ref BlobSegment segment = ref src.Blob[SegmentOf(address)];
        long at = OffsetOf(address) + offset;
        if (segment.Array != null) { from.CopyTo(new Span<byte>(segment.Array, checked((int)at), from.Length)); return; }
        if (segment.Chunks == null) throw new InvalidOperationException("a mapped payload is read-only");
        int done = 0;
        while (done < from.Length)
        {
            int chunk = (int)((at + done) / segment.ChunkBytes);
            int within = (int)((at + done) % segment.ChunkBytes);
            int count = Math.Min(from.Length - done, segment.Chunks[chunk].Length - within);
            from.Slice(done, count).CopyTo(new Span<byte>(segment.Chunks[chunk], within, count));
            done += count;
        }
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
    /// Reserves room for one payload within one owned segment, allocating another when the current
    /// one cannot fit it.
    /// </summary>
    public static long ReserveBlob(ref Source src, long count)
    {
        // Larger than one chunk: a segment of its own, held in chunks, since one array cannot be.
        if (count > src.ChunkLimit)
        {
            if (src.BlobSegments == src.Blob.Length) Array.Resize(ref src.Blob, src.Blob.Length * 2);
            int chunkBytes = src.ChunkLimit;
            var chunks = new byte[(int)((count + chunkBytes - 1) / chunkBytes)][];
            for (int i = 0; i < chunks.Length; i++)
                chunks[i] = new byte[(int)Math.Min(chunkBytes, count - (long)i * chunkBytes)];
            int chunked = src.BlobSegments;
            src.Blob[chunked] = default;
            src.Blob[chunked].Chunks = chunks;
            src.Blob[chunked].ChunkBytes = chunkBytes;
            src.Blob[chunked].Length = count;
            src.BlobSegments++;
            return BlobAddress(chunked, 0);
        }
        return ReserveOwned(ref src, (int)count);
    }

    static long ReserveOwned(ref Source src, int count)
    {
        int last = src.BlobSegments - 1;

        // A mapped or chunked segment is never the one with room: one is read-only, the other
        // holds one payload alone.
        if (last < 0 || src.Blob[last].Array == null
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

    /// <summary>Room for a reference's edge data. A plain bump: edge data is short and rare.</summary>
    public static int ReserveEdgeData(ref Source src, int count)
    {
        int needed = src.EdgeDataUsed + count;
        if (src.EdgeData.Length < needed)
        {
            int size = src.EdgeData.Length < 16 ? 16 : src.EdgeData.Length;
            while (size < needed)
            {
                size *= 2;
            }
            Array.Resize(ref src.EdgeData, size);
        }

        int offset = src.EdgeDataUsed;
        src.EdgeDataUsed += count;
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
