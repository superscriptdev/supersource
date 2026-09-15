using System;
using System.Runtime.InteropServices;

namespace Supersource;
/// <summary>One edge pointing at a node: who holds it, and which of their edges it is.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Backref
{
    public NodeId From;

    /// <summary>Index within the referring node’s reference run.</summary>
    public int RefIndex;
}

/// <summary>
/// Back-reference index with a contiguous run per target. Includes references from live nodes,
/// including references to deleted targets. Rebuilding reuses its arrays; the source graph remains
/// authoritative.
/// </summary>
public struct Backrefs
{
    /// <summary>Start offsets with a final sentinel; adjacent offsets give each target’s run length.</summary>
    public int[] Start;

    public Backref[] Entries;
    public int EntryCount;

    /// <summary>
    /// Source identity, revision, stamp and arena sizes captured when the index was built. Revision
    /// alone cannot distinguish independently loaded states.
    /// </summary>
    public SourceId Identity;
    public ulong BuiltAt;
    public long NodeCount;

    /// <summary>Checks whether the index describes the current source state.</summary>
    public static bool IsCurrent(in Backrefs index, in Source src)
    {
        return index.Start != null
            && index.Identity == src.Identity
            && index.NodeCount == src.NodeCount
            && index.BuiltAt == src.Stamp;
    }

    /// <summary>
    /// Build, or rebuild in place. O(nodes + edges), two passes, no allocation when the
    /// arrays already fit.
    /// </summary>
    public static void Build(in Source src, ref Backrefs index)
    {
        long slots = src.NodeCount;

        if (index.Start == null || index.Start.Length < slots + 1)
        {
            index.Start = new int[slots + 1 < 8 ? 8 : (int)slots + 1];
        }

        Array.Clear(index.Start, 0, (int)slots + 1);

        // Pass one: how many edges point at each slot.
        int total = 0;
        for (long i = 0; i < slots; i++)
        {
            if ((src.Nodes[i].Flags & NodeFlags.Live) == 0)
            {
                continue;
            }

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                long target = src.Refs[offset + r].Target.Index;
                if (target < 0 || target >= slots)
                {
                    continue;   // a hole points at nothing, so nothing indexes it
                }

                index.Start[target]++;
                total++;
            }
        }

        // Counts become offsets.
        int running = 0;
        for (long i = 0; i < slots; i++)
        {
            int here = index.Start[i];
            index.Start[i] = running;
            running += here;
        }
        index.Start[slots] = running;

        if (index.Entries == null || index.Entries.Length < total)
        {
            index.Entries = new Backref[total < 8 ? 8 : total];
        }

        // Place edges in source-slot order for stable results. Reuse Start as the write cursor,
        // then restore its offsets below.
        for (long i = 0; i < slots; i++)
        {
            if ((src.Nodes[i].Flags & NodeFlags.Live) == 0)
            {
                continue;
            }

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                long target = src.Refs[offset + r].Target.Index;
                if (target < 0 || target >= slots)
                {
                    continue;
                }

                int at = index.Start[target];
                index.Start[target] = at + 1;

                index.Entries[at].From = new NodeId(i);
                index.Entries[at].RefIndex = (int)r;
            }
        }

        // Every Start[i] now holds the END of run i, which is the START of run i + 1.
        for (long i = slots; i > 0; i--)
        {
            index.Start[i] = index.Start[i - 1];
        }
        index.Start[0] = 0;

        index.EntryCount = total;
        index.Identity = src.Identity;
        index.NodeCount = slots;
        index.BuiltAt = src.Stamp;
    }

    /// <summary>Returns incoming edges ordered by referring node. Out-of-range targets return an empty span.</summary>
    public static ReadOnlySpan<Backref> To(in Backrefs index, NodeId target)
    {
        if (index.Start == null || target.Index < 0 || target.Index >= index.NodeCount)
        {
            return ReadOnlySpan<Backref>.Empty;
        }

        int from = index.Start[target.Index];
        return new ReadOnlySpan<Backref>(index.Entries, from, index.Start[target.Index + 1] - from);
    }

    /// <summary>How many edges point at this node.</summary>
    public static int CountTo(in Backrefs index, NodeId target)
    {
        if (index.Start == null || target.Index < 0 || target.Index >= index.NodeCount)
        {
            return 0;
        }
        return index.Start[target.Index + 1] - index.Start[target.Index];
    }
}
