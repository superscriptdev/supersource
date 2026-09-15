using System;

namespace Supersource;
/// <summary>
/// Copies reachable arena storage into compact arrays without renumbering nodes. Retains deleted
/// slots and their data, and preserves revision and stamp. Serialized output is unchanged.
/// </summary>
public static class SourceCompact
{
    /// <summary>Reports arena space not used by any node. Storage retained for deleted nodes counts as used.</summary>
    public static void Wasted(
        in Source src,
        out int blobBytes,
        out int childSlots,
        out int refSlots)
    {
        int payload = 0;
        int labels = 0;
        int children = 0;
        int refs = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            payload += src.Nodes[i].PayloadLength;
            children += src.Nodes[i].ChildCount;

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            refs += count;

            for (int r = 0; r < count; r++)
            {
                if (!src.Refs[offset + r].IsBound)
                {
                    labels += src.Refs[offset + r].LabelLength;
                }
            }
        }

        long used = 0;
        for (int i = 0; i < src.BlobSegments; i++)
        {
            // A mapping is not space this source handed out, so none of it is waste.
            used += src.Blob[i].IsMapped ? 0 : src.Blob[i].Used;
        }

        // Include abandoned payload and reference-data bytes.
        blobBytes = (int)(used - payload) + (src.LabelsUsed - labels);
        childSlots = src.ChildArenaUsed - children;
        refSlots = src.RefArenaUsed - refs;
    }

    /// <summary>
    /// Copies the source with runs sized to their used lengths. Growth beyond those lengths will
    /// relocate the affected run.
    /// </summary>
    public static Source Compact(in Source src)
    {
        int payloadBytes = 0;
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
                    labelBytes += src.Refs[offset + r].LabelLength;
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
        into.Blob[0].Array = new byte[payloadBytes < 16 ? 16 : payloadBytes];
        into.Blob[0].Length = into.Blob[0].Array.Length;
        into.BlobSegments = 1;
        into.Labels = new byte[labelBytes < 16 ? 16 : labelBytes];
        into.LabelsUsed = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            // Everything that is not an arena offset carries over untouched, including the
            // per-node revision: compaction is not an edit.
            into.Nodes[i] = src.Nodes[i];
            into.NodeRevision[i] = src.NodeRevision[i];

            int payloadLength = src.Nodes[i].PayloadLength;
            if (payloadLength > 0)
            {
                long at = Source.ReserveBlob(ref into, payloadLength);
                Source.Bytes(in src, src.Nodes[i].PayloadOffset, payloadLength).CopyTo(
                    new Span<byte>(
                        into.Blob[Source.SegmentOf(at)].Array,
                        (int)Source.OffsetOf(at),
                        payloadLength));
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

                    // An edge's bytes have their own arena, and they move with it —
                    // whether they are a hole's label or a bound edge's qualifier.
                    if (edge.LabelLength > 0)
                    {
                        int labelAt = Source.ReserveLabel(ref into, edge.LabelLength);
                        Array.Copy(src.Labels, edge.LabelOffset, into.Labels, labelAt, edge.LabelLength);
                        edge.LabelOffset = labelAt;
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
