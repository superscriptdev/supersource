using System;
using Supersource.Kinds;
using Supersource.Timeline;

namespace Supersource.Compare;
/// <summary>
/// Produces operations between states of the same source using durable node IDs. Applies each
/// operation to a working copy as it is emitted so later indices reflect earlier changes.
/// </summary>
public static class StructuralDiff
{
    /// <summary>
    /// Operations turning <paramref name="before"/> into <paramref name="after"/>.
    ///
    /// Returns how many were written into <paramref name="into"/>.
    /// </summary>
    public static int Between(
        in Source before,
        in Source after,
        in KindTable table,
        ref Operation[] into)
    {
        if (!before.Identity.IsNone && !after.Identity.IsNone && before.Identity != after.Identity)
        {
            throw new InvalidOperationException(
                "these are two sources, not two states of one; slots correspond only within one identity");
        }

        if (into == null)
        {
            into = new Operation[32];
        }

        // Apply emitted operations to an independent working copy.
        Source working = SourceCompact.Compact(in before);
        int found = 0;

        // Allocate before referencing, move before deleting, and reconcile order after membership
        // is final.
        found = Appear(ref working, in after, ref into, found);
        found = Reparent(ref working, in after, ref into, found);
        found = Reorder(ref working, in after, ref into, found);
        found = Kinds(ref working, in after, ref into, found);
        found = Payloads(ref working, in after, in table, ref into, found);
        found = Edges(ref working, in after, ref into, found);
        found = Disappear(ref working, in after, ref into, found);

        return found;
    }

    /// <summary>
    /// Creates every new node at the id it has in the target, so ids match without a matching
    /// step. Final sibling positions are assigned after all nodes exist. A deleted id inside the
    /// new range needs nothing, since creating a later id moves the next id past it; a deleted id
    /// at the very end is created and deleted again, so the next id matches too.
    /// </summary>
    static int Appear(ref Source working, in Source after, ref Operation[] into, int found)
    {
        for (long i = working.NodeCount; i < after.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!after.IsLive(node) && i != after.NodeCount - 1)
            {
                continue;
            }

            // Use an existing parent when possible; otherwise attach temporarily under the root.
            NodeId parent = after.Nodes[i].Parent;
            if (parent.IsNone || !working.IsLive(parent))
            {
                parent = working.Root;
            }

            Operation op = default;
            op.Kind = OpKind.Insert;
            op.Node = node;
            op.Parent = parent;
            op.Index = SourceWalk.ChildCount(in working, parent);
            op.NodeKind = after.Nodes[i].Kind;

            // An operation carries at most one piece; a larger payload follows as appending splices.
            long length = SourceWalk.PayloadLength(in after, node);
            bool whole = SourceWalk.IsContiguous(in after, node) && length <= Piece;
            op.Value = whole ? SourceWalk.Payload(in after, node).ToArray() : Array.Empty<byte>();

            found = Emit(ref working, ref into, found, op);
            if (!whole)
            {
                found = Pieces(ref working, ref into, found, in after, node, 0, 0, 0, length);
            }
        }

        return found;
    }

    /// <summary>Reparents nodes shallowest first so containers move before their descendants.</summary>
    static int Reparent(ref Source working, in Source after, ref Operation[] into, int found)
    {
        long slots = working.NodeCount < after.NodeCount ? working.NodeCount : after.NodeCount;

        long[] order = ByDepth(in after, slots);
        for (int o = 0; o < order.Length; o++)
        {
            long i = order[o];
            NodeId node = new NodeId(i);

            if (!working.IsLive(node) || !after.IsLive(node))
            {
                continue;
            }

            NodeId parent = after.Nodes[i].Parent;
            if (parent.IsNone || SourceWalk.Parent(in working, node) == parent)
            {
                continue;
            }

            Operation op = default;
            op.Kind = OpKind.Move;
            op.Node = node;
            op.Parent = parent;
            op.Index = SourceWalk.ChildCount(in working, parent);

            found = Emit(ref working, ref into, found, op);
        }

        return found;
    }

    /// <summary>
    /// Restores child order even when the schema does not mark it significant, so applying the diff
    /// reproduces the target state.
    /// </summary>
    static int Reorder(ref Source working, in Source after, ref Operation[] into, int found)
    {
        long slots = working.NodeCount < after.NodeCount ? working.NodeCount : after.NodeCount;

        for (long i = 0; i < slots; i++)
        {
            NodeId parent = new NodeId(i);
            if (!working.IsLive(parent) || !after.IsLive(parent))
            {
                continue;
            }

            ReadOnlySpan<long> wanted = SourceWalk.Children(in after, parent);
            for (int at = 0; at < wanted.Length; at++)
            {
                ReadOnlySpan<long> have = SourceWalk.Children(in working, parent);
                if (at >= have.Length || have[at] == wanted[at])
                {
                    continue;
                }

                int from = -1;
                for (int c = at + 1; c < have.Length; c++)
                {
                    if (have[c] == wanted[at])
                    {
                        from = c;
                        break;
                    }
                }

                if (from < 0)
                {
                    // Skip children not present in this parent’s current run.
                    continue;
                }

                Operation op = default;
                op.Kind = OpKind.Reorder;
                op.Node = new NodeId(have[from]);
                op.Parent = parent;
                op.Index = at;

                found = Emit(ref working, ref into, found, op);
            }
        }

        return found;
    }

    /// <summary>A node that is live in both states and changed kind keeps its id: a retype.</summary>
    static int Kinds(ref Source working, in Source after, ref Operation[] into, int found)
    {
        long slots = working.NodeCount < after.NodeCount ? working.NodeCount : after.NodeCount;

        for (long i = 0; i < slots; i++)
        {
            NodeId node = new NodeId(i);
            if (!working.IsLive(node) || !after.IsLive(node))
            {
                continue;
            }
            if (working.Nodes[i].Kind == after.Nodes[i].Kind)
            {
                continue;
            }

            Operation op = default;
            op.Kind = OpKind.Retype;
            op.Node = node;
            op.NodeKind = after.Nodes[i].Kind;
            found = Emit(ref working, ref into, found, op);
        }

        return found;
    }

    /// <summary>Diffs changed payloads, aligning to code points or elements as their schema's format says.</summary>
    static int Payloads(
        ref Source working,
        in Source after,
        in KindTable table,
        ref Operation[] into,
        int found)
    {
        long slots = working.NodeCount < after.NodeCount ? working.NodeCount : after.NodeCount;

        for (long i = 0; i < slots; i++)
        {
            NodeId node = new NodeId(i);
            if (!working.IsLive(node) || !after.IsLive(node))
            {
                continue;
            }

            // Without a schema, compare raw bytes.
            KindSchema schema = KindTable.SchemaOf(in table, working.Nodes[i].Kind);

            if (!SourceWalk.IsContiguous(in working, node) || !SourceWalk.IsContiguous(in after, node))
            {
                if (PayloadDiff.RegionOf(PayloadDiff.ShapeOf(in schema), in schema.Payload,
                        in working, in after, node, out long at, out long removed, out long inserted))
                {
                    found = Pieces(ref working, ref into, found, in after, node, at, removed, at, inserted);
                }
                continue;
            }

            if (!PayloadDiff.Between(
                    in schema,
                    node,
                    SourceWalk.Payload(in working, node),
                    SourceWalk.Payload(in after, node),
                    out Operation op))
            {
                continue;
            }

            found = Emit(ref working, ref into, found, op);
        }

        return found;
    }

    /// <summary>Reconciles reference runs positionally so the resulting reference indices match the target.</summary>
    static int Edges(ref Source working, in Source after, ref Operation[] into, int found)
    {
        long slots = working.NodeCount < after.NodeCount ? working.NodeCount : after.NodeCount;

        for (long i = 0; i < slots; i++)
        {
            NodeId node = new NodeId(i);
            if (!working.IsLive(node) || !after.IsLive(node))
            {
                continue;
            }

            ReadOnlySpan<Ref> want = SourceWalk.References(in after, node);

            for (int r = 0; r < want.Length; r++)
            {
                ReadOnlySpan<Ref> have = SourceWalk.References(in working, node);

                if (r < have.Length)
                {
                    if (Same(in working, in have[r], in after, in want[r]))
                    {
                        continue;
                    }

                    Operation drop = default;
                    drop.Kind = OpKind.Disconnect;
                    drop.Node = node;
                    drop.Index = r;
                    found = Emit(ref working, ref into, found, drop);
                }

                Operation add = default;
                add.Kind = OpKind.Connect;
                add.Node = node;
                add.Index = r;
                add.Relation = want[r].Relation;
                add.Target = want[r].Target;
                add.Value = SourceWalk.EdgeData(in after, in want[r]).ToArray();

                found = Emit(ref working, ref into, found, add);
            }

            // Anything past the end of the after state's run is gone. Removed from the
            // back, so each index still names what it named when it was decided.
            for (int r = SourceWalk.RefCount(in working, node) - 1; r >= want.Length; r--)
            {
                Operation drop = default;
                drop.Kind = OpKind.Disconnect;
                drop.Node = node;
                drop.Index = r;
                found = Emit(ref working, ref into, found, drop);
            }
        }

        return found;
    }

    static bool Same(in Source a, in Ref left, in Source b, in Ref right)
    {
        if (left.Relation != right.Relation || left.Target != right.Target)
        {
            return false;
        }
        return SourceWalk.EdgeData(in a, in left).SequenceEqual(SourceWalk.EdgeData(in b, in right));
    }

    /// <summary>Deletes obsolete nodes after moves have preserved surviving descendants.</summary>
    static int Disappear(ref Source working, in Source after, ref Operation[] into, int found)
    {
        for (long i = 0; i < working.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!working.IsLive(node))
            {
                continue;
            }

            if (i < after.NodeCount && after.IsLive(node))
            {
                continue;
            }

            Operation op = default;
            op.Kind = OpKind.Delete;
            op.Node = node;

            found = Emit(ref working, ref into, found, op);
        }

        return found;
    }

    /// <summary>The most bytes one operation carries: a payload larger than this is changed piece by piece.</summary>
    const int Piece = 64 << 20;

    /// <summary>
    /// Splices that replace <paramref name="removed"/> bytes at <paramref name="at"/> with
    /// <paramref name="inserted"/> bytes of <paramref name="after"/>'s payload from
    /// <paramref name="from"/>, one piece per operation: the first removes, the rest append.
    /// </summary>
    static int Pieces(ref Source working, ref Operation[] into, int found, in Source after, NodeId node,
        long at, long removed, long from, long inserted)
    {
        long done = 0;
        do
        {
            int count = (int)Math.Min(Piece, inserted - done);
            Operation op = default;
            op.Kind = OpKind.Splice;
            op.Node = node;
            op.Index = at + done;
            op.Count = done == 0 ? removed : 0;
            op.Value = new byte[count];
            SourceWalk.ReadPayload(in after, node, from + done, op.Value);
            found = Emit(ref working, ref into, found, op);
            done += count;
        }
        while (done < inserted);
        return found;
    }

    /// <summary>Applies before recording so the emitted operation includes captured inverse data.</summary>
    static int Emit(ref Source working, ref Operation[] into, int found, Operation op)
    {
        OpApply.Apply(ref working, ref op);

        if (found == into.Length)
        {
            Array.Resize(ref into, into.Length == 0 ? 32 : into.Length * 2);
        }

        into[found] = op;
        return found + 1;
    }

    /// <summary>Orders slots by containment depth, shallowest first, using counting sort.</summary>
    static long[] ByDepth(in Source after, long slots)
    {
        int[] depth = new int[slots];
        int deepest = 0;

        for (long i = 0; i < slots; i++)
        {
            int d = 0;
            NodeId walk = i < after.NodeCount ? after.Nodes[i].Parent : NodeId.None;

            // Bound traversal by slot count to terminate on malformed parent cycles.
            while (walk.IsSome && walk.Index < after.NodeCount && d <= slots)
            {
                d++;
                walk = after.Nodes[walk.Index].Parent;
            }

            depth[i] = d;
            if (d > deepest)
            {
                deepest = d;
            }
        }

        int[] counts = new int[deepest + 2];
        for (long i = 0; i < slots; i++)
        {
            counts[depth[i] + 1]++;
        }
        for (int d = 0; d < deepest + 1; d++)
        {
            counts[d + 1] += counts[d];
        }

        long[] order = new long[slots];
        for (long i = 0; i < slots; i++)
        {
            order[counts[depth[i]]] = i;
            counts[depth[i]]++;
        }
        return order;
    }
}
