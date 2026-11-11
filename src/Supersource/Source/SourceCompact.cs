using System;

namespace Supersource;
/// <summary>
/// Copies reachable arena storage into compact arrays without renumbering nodes. Keeps every id,
/// deleted ones included, and preserves revision and stamp. Serialized output is unchanged.
/// </summary>
public static class SourceCompact
{
    /// <summary>
    /// Reports arena space no node uses: runs abandoned by growth, and what deleted nodes held.
    /// </summary>
    public static void Wasted(
        in Source src,
        out int blobBytes,
        out int childSlots,
        out int refSlots)
    {
        long payload = 0;
        long edgeData = 0;
        int children = 0;
        int refs = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            // A mapping is not space this source handed out: a payload in one is neither used
            // nor waste.
            long length = src.Nodes[i].PayloadLength;
            if (length > 0 && !src.Blob[Source.SegmentOf(src.Nodes[i].PayloadOffset)].IsMapped)
            {
                payload += length;
            }
            children += src.Nodes[i].ChildCount;

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            refs += count;

            // A reference's edge data is in use whether the reference is bound or not.
            for (int r = 0; r < count; r++)
            {
                edgeData += src.Refs[offset + r].DataLength;
            }
        }

        long used = 0;
        for (int i = 0; i < src.BlobSegments; i++)
        {
            // An owned segment has handed out what it has used, and a chunked one all of itself,
            // since it holds one payload. A mapped one handed out nothing.
            if (src.Blob[i].IsChunked)
            {
                used += src.Blob[i].Length;
            }
            else if (!src.Blob[i].IsMapped)
            {
                used += src.Blob[i].Used;
            }
        }

        // Abandoned payload and edge-data bytes. More than an int holds reads as int.MaxValue.
        blobBytes = (int)Math.Min(used - payload + (src.EdgeDataUsed - edgeData), int.MaxValue);
        childSlots = src.ChildArenaUsed - children;
        refSlots = src.RefArenaUsed - refs;
    }

    /// <summary>Copy a payload from one source into another in windows, so it need not fit a span.</summary>
    static void Across(in Source from, long address, ref Source into, long at, long length)
    {
        var window = new byte[(int)Math.Min(length, 1 << 20)];
        long done = 0;
        while (done < length)
        {
            int n = (int)Math.Min(window.Length, length - done);
            Source.Read(in from, address, done, window.AsSpan(0, n));
            Source.Write(ref into, at, done, window.AsSpan(0, n));
            done += n;
        }
    }

    /// <summary>
    /// Copies the source with runs sized to their used lengths. Growth beyond those lengths will
    /// relocate the affected run.
    /// </summary>
    public static Source Compact(in Source src)
    {
        long payloadBytes = 0;
        int labelBytes = 0;
        int children = 0;
        int refs = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            payloadBytes += src.Nodes[i].PayloadLength;
            children += src.Nodes[i].ChildCount;

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            refs += count;

            for (int r = 0; r < count; r++)
            {
                if (!src.Refs[offset + r].IsBound)
                {
                    labelBytes += src.Refs[offset + r].DataLength;
                }
            }
        }

        Source into = default;
        into.Identity = src.Identity;
        into.Root = src.Root;
        into.Revision = src.Revision;
        into.Stamp = src.Stamp;

        long slots = src.NodeCount < 4 ? 4 : src.NodeCount;
        into.Nodes = new Node[slots];
        into.Capacities = new RunCapacity[slots];
        into.NodeRevision = new int[slots];
        into.NodeCount = src.NodeCount;
        into.Children = new long[children < 4 ? 4 : children];
        into.Refs = new Ref[refs < 4 ? 4 : refs];

        // Copy mapped payloads into owned segments so the compacted source can outlive its
        // mapping.
        into.Blob = new BlobSegment[4];
        into.ChunkBytes = src.ChunkBytes;
        into.Blob[0].Array = new byte[(int)Math.Clamp(payloadBytes, 16, into.ChunkLimit)];
        into.Blob[0].Length = into.Blob[0].Array.Length;
        into.BlobSegments = 1;
        into.EdgeData = new byte[labelBytes < 16 ? 16 : labelBytes];
        into.EdgeDataUsed = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            // Everything that is not an arena offset carries over untouched, including the
            // per-node revision: compaction is not an edit.
            into.Nodes[i] = src.Nodes[i];
            into.NodeRevision[i] = src.NodeRevision[i];

            long payloadLength = src.Nodes[i].PayloadLength;
            if (payloadLength > 0)
            {
                long at = Source.ReserveBlob(ref into, payloadLength);
                Across(in src, src.Nodes[i].PayloadOffset, ref into, at, payloadLength);
                into.Nodes[i].PayloadOffset = at;
                into.Capacities[i].Payload = payloadLength;
            }
            else
            {
                into.Nodes[i].PayloadOffset = 0;
                into.Capacities[i].Payload = 0;
            }

            int childCount = src.Nodes[i].ChildCount;
            if (childCount > 0)
            {
                int at = Source.ReserveChildren(ref into, childCount);
                Array.Copy(src.Children, src.Nodes[i].ChildOffset, into.Children, at, childCount);
                into.Nodes[i].ChildOffset = at;
                into.Capacities[i].Child = childCount;
            }
            else
            {
                into.Nodes[i].ChildOffset = 0;
                into.Capacities[i].Child = 0;
            }

            int refCount = src.Nodes[i].RefCount;
            if (refCount > 0)
            {
                int at = Source.ReserveRefs(ref into, refCount);
                into.Nodes[i].RefOffset = at;
                into.Capacities[i].Ref = refCount;

                for (int r = 0; r < refCount; r++)
                {
                    Ref edge = src.Refs[src.Nodes[i].RefOffset + r];

                    // An edge's bytes have their own arena, and they move with it, whether
                    // they are an unbound reference's label or a bound one's edge data.
                    if (edge.DataLength > 0)
                    {
                        int labelAt = Source.ReserveEdgeData(ref into, edge.DataLength);
                        Array.Copy(src.EdgeData, edge.DataOffset, into.EdgeData, labelAt, edge.DataLength);
                        edge.DataOffset = labelAt;
                    }

                    into.Refs[at + r] = edge;
                }
            }
            else
            {
                into.Nodes[i].RefOffset = 0;
                into.Capacities[i].Ref = 0;
            }
        }

        return into;
    }
}
