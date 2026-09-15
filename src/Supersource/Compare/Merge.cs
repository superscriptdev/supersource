using System;
using Supersource.Kinds;

namespace Supersource.Compare;
/// <summary>Outcome of a merge decision.</summary>
public enum Decision : byte
{
    /// <summary>Only one side touched it, and that side's version stands.</summary>
    OneSide = 0,

    /// <summary>Both sides made the same change. Nothing to decide.</summary>
    Agreed = 1,

    /// <summary>Conflicting changes; ours was retained.</summary>
    Conflict = 2,

    /// <summary>
    /// Their change could not be expressed here at all: a move under a node we deleted, an
    /// edge to something that is gone. Ours stands.
    /// </summary>
    Unrepresentable = 3,

    /// <summary>Both sides changed disjoint payload regions, and both changes were retained.</summary>
    Combined = 4,
}

/// <summary>What about a node had to be decided.</summary>
public enum Facet : byte
{
    Payload = 0,
    Containment = 1,
    ChildOrder = 2,
    Edges = 3,

    /// <summary>One side deleted it and the other did not.</summary>
    Existence = 4,
}

/// <summary>A recorded merge decision about one node and facet.</summary>
public struct MergeNote
{
    public NodeId Node;
    public Facet Facet;
    public Decision How;
    public string Detail;
}

/// <summary>
/// Three-way merge by durable node ID and facet. Allocations unique to theirs receive fresh IDs,
/// with their references remapped. Unresolved conflicts retain ours and produce notes.
/// </summary>
public static class Merge
{
    /// <summary>
    /// Maps one side’s node, kind and relation handles into the merged source. Null maps mean
    /// identity.
    /// </summary>
    struct Lens
    {
        public long[] Nodes;
        public KindId[] Kinds;
        public RelationId[] Relations;

        public static readonly Lens Same = default;
    }

    /// <summary>
    /// Merges states using the same KindTable. Returns decisions in notes and their count in
    /// noteCount.
    /// </summary>
    public static Source Between(
        in Source common,
        in Source ours,
        in Source theirs,
        in KindTable table,
        ref MergeNote[] notes,
        out int noteCount)
    {
        Lens lens = Lens.Same;
        return Between(in common, in ours, in theirs, in table, ref lens, ref notes, out noteCount);
    }

    /// <summary>
    /// Remaps theirs through its table. Common must already use table’s numbering; use KindRemap
    /// before calling.
    /// </summary>
    public static Source Between(
        in Source common,
        in Source ours,
        in Source theirs,
        ref KindTable table,
        in KindTable theirsTable,
        ref MergeNote[] notes,
        out int noteCount)
    {
        Lens lens = default;

        lens.Kinds = new KindId[theirsTable.KindCount];
        for (int i = 0; i < theirsTable.KindCount; i++)
        {
            QualifiedName name = theirsTable.KindNames[i];
            lens.Kinds[i] = name.IsNone ? KindId.Unknown : KindTable.Intern(ref table, name);
        }

        lens.Relations = new RelationId[theirsTable.RelationCount];
        for (int i = 0; i < theirsTable.RelationCount; i++)
        {
            QualifiedName name = theirsTable.RelationNames[i];
            lens.Relations[i] = name.IsNone
                ? RelationId.Unknown
                : KindTable.InternRelation(ref table, name);
        }

        return Between(in common, in ours, in theirs, in table, ref lens, ref notes, out noteCount);
    }

    static Source Between(
        in Source common,
        in Source ours,
        in Source theirs,
        in KindTable table,
        ref Lens theirsLens,
        ref MergeNote[] notes,
        out int noteCount)
    {
        Check(in common, in ours, "ours");
        Check(in common, in theirs, "theirs");

        if (notes == null)
        {
            notes = new MergeNote[16];
        }

        noteCount = 0;

        // Handle equal and unchanged states before treating each side’s new slots as independent
        // allocations. Otherwise a fast-forward would duplicate nodes.
        if (SameState(in theirs, in common) || SameState(in theirs, in ours))
        {
            return SourceCompact.Compact(in ours);
        }

        if (SameState(in ours, in common))
        {
            return SourceCompact.Compact(in theirs);
        }

        Source merged = SourceCompact.Compact(in ours);

        // Allocate all of their new slots, including deleted ones, before copying references to
        // them.
        theirsLens.Nodes = Adopt(ref merged, in common, in theirs, in theirsLens,
            ref notes, ref noteCount);

        long shared = common.NodeCount;
        for (long i = 0; i < shared; i++)
        {
            NodeId node = new NodeId(i);

            bool weDeleted = !merged.IsLive(node) && common.IsLive(node);
            bool theyDeleted = !theirs.IsLive(node) && common.IsLive(node);

            if (theyDeleted && !weDeleted)
            {
                if (TouchedAnything(in common, in merged, node))
                {
                    noteCount = Note(ref notes, noteCount, node, Facet.Existence,
                        Decision.Conflict, "they deleted it and we changed it; ours stands");
                    continue;
                }

                SourceBuild.Delete(ref merged, node);
                noteCount = Note(ref notes, noteCount, node, Facet.Existence,
                    Decision.OneSide, "deleted by them");
                continue;
            }

            if (weDeleted)
            {
                if (!theyDeleted && TouchedAnything(in common, in theirs, node))
                {
                    noteCount = Note(ref notes, noteCount, node, Facet.Existence,
                        Decision.Conflict, "we deleted it and they changed it; ours stands");
                }
                continue;
            }

            if (!merged.IsLive(node) || !theirs.IsLive(node))
            {
                continue;
            }

            noteCount = Payload(ref merged, in common, in theirs, in table, node,
                ref notes, noteCount);
            noteCount = Containment(ref merged, in common, in theirs, in theirsLens, node,
                ref notes, noteCount);
            noteCount = Edges(ref merged, in common, in theirs, in theirsLens, node,
                ref notes, noteCount);
        }

        // Reconcile child order only after finalizing membership and moves.
        for (long i = 0; i < shared; i++)
        {
            NodeId node = new NodeId(i);
            if (!merged.IsLive(node) || !theirs.IsLive(node) || !common.IsLive(node))
            {
                continue;
            }

            noteCount = ChildOrder(ref merged, in common, in theirs, node, ref notes, noteCount);
        }

        return merged;
    }

    static void Check(in Source common, in Source side, string which)
    {
        if (!common.Identity.IsNone && !side.Identity.IsNone && common.Identity != side.Identity)
        {
            throw new InvalidOperationException(
                which + " is a different source from the common state, not a later state of it");
        }

        if (side.NodeCount < common.NodeCount)
        {
            throw new InvalidOperationException(
                which + " has fewer slots than the common state, so it is not descended from it");
        }
    }

    /// <summary>
    /// Allocates their new slots, then wires their containment and references using the completed
    /// ID map.
    /// </summary>
    static long[] Adopt(
        ref Source merged,
        in Source common,
        in Source theirs,
        in Lens lens,
        ref MergeNote[] notes,
        ref int noteCount)
    {
        long newCount = theirs.NodeCount - common.NodeCount;
        long[] remap = new long[newCount < 0 ? 0 : newCount];

        for (int i = 0; i < remap.Length; i++)
        {
            NodeId original = new NodeId(common.NodeCount + i);
            NodeId here = SourceBuild.AddNode(ref merged,
                Kind(in lens, theirs.Nodes[original.Index].Kind));
            remap[i] = here.Index;

            ReadOnlySpan<byte> payload = SourceWalk.Payload(in theirs, original);
            if (payload.Length > 0)
            {
                SourceBuild.SetPayload(ref merged, here, payload);
            }

            // Derived and Generated travel; Live is decided below by whether they kept it.
            merged.Nodes[here.Index].Flags |= theirs.Nodes[original.Index].Flags
                & (NodeFlags.Derived | NodeFlags.Generated);
        }

        for (int i = 0; i < remap.Length; i++)
        {
            NodeId original = new NodeId(common.NodeCount + i);
            NodeId here = new NodeId(remap[i]);

            NodeId parent = Remap(in common, remap, theirs.Nodes[original.Index].Parent);

            if (!merged.IsLive(here))
            {
                continue;
            }

            // If their new node’s parent was deleted, retain the node under the root and report
            // the placement conflict.
            if (parent.IsNone || !merged.IsLive(parent))
            {
                if (merged.Root.IsNone || !merged.IsLive(merged.Root))
                {
                    continue;
                }

                SourceBuild.AppendChild(ref merged, merged.Root, here);
                noteCount = Note(ref notes, noteCount, here, Facet.Containment,
                    Decision.Unrepresentable,
                    "they added it inside something that is not here; it is at the root");
                continue;
            }

            SourceBuild.AppendChild(ref merged, parent, here);

            ReadOnlySpan<Ref> refs = SourceWalk.References(in theirs, original);
            for (int r = 0; r < refs.Length; r++)
            {
                Lens wired = lens;
                wired.Nodes = remap;
                Adopt(ref merged, in theirs, in wired, in common, here, in refs[r]);
            }
        }

        // Nodes deleted in their source remain deleted here. Done after wiring so a dead node keeps the
        // shape it had, exactly as a delete leaves it.
        for (int i = 0; i < remap.Length; i++)
        {
            if (!theirs.IsLive(new NodeId(common.NodeCount + i)))
            {
                SourceBuild.Delete(ref merged, new NodeId(remap[i]));
            }
        }

        return remap;
    }

    static void Adopt(
        ref Source merged,
        in Source theirs,
        in Lens lens,
        in Source common,
        NodeId here,
        in Ref edge)
    {
        RelationId relation = Relation(in lens, edge.Relation);

        if (edge.IsUnbound)
        {
            SourceBuild.AddUnboundRef(ref merged, here, relation,
                SourceWalk.EdgeBytes(in theirs, in edge));
            return;
        }

        SourceBuild.AddRef(ref merged, here, relation, Remap(in common, lens.Nodes, edge.Target));
    }

    static KindId Kind(in Lens lens, KindId theirs)
    {
        if (lens.Kinds == null)
        {
            return theirs;
        }
        return theirs.Value >= 0 && theirs.Value < lens.Kinds.Length
            ? lens.Kinds[theirs.Value]
            : KindId.Unknown;
    }

    static RelationId Relation(in Lens lens, RelationId theirs)
    {
        if (lens.Relations == null)
        {
            return theirs;
        }
        return theirs.Value >= 0 && theirs.Value < lens.Relations.Length
            ? lens.Relations[theirs.Value]
            : RelationId.Unknown;
    }

    /// <summary>Preserves common IDs and maps IDs allocated only by theirs.</summary>
    static NodeId Remap(in Source common, long[] remap, NodeId theirs)
    {
        // Common slots and identity-mapped sides retain their IDs.
        if (remap == null || theirs.IsNone || theirs.Index < common.NodeCount)
        {
            return theirs;
        }

        long at = theirs.Index - common.NodeCount;
        return at < remap.Length ? new NodeId(remap[at]) : NodeId.None;
    }

    static int Payload(
        ref Source merged,
        in Source common,
        in Source theirs,
        in KindTable table,
        NodeId node,
        ref MergeNote[] notes,
        int count)
    {
        ReadOnlySpan<byte> was = SourceWalk.Payload(in common, node);
        ReadOnlySpan<byte> mine = SourceWalk.Payload(in merged, node);
        ReadOnlySpan<byte> theirsBytes = SourceWalk.Payload(in theirs, node);

        if (theirsBytes.SequenceEqual(was))
        {
            return count;
        }

        if (mine.SequenceEqual(was))
        {
            SourceBuild.SetPayload(ref merged, node, theirsBytes);
            return Note(ref notes, count, node, Facet.Payload, Decision.OneSide,
                "changed by them");
        }

        if (mine.SequenceEqual(theirsBytes))
        {
            return Note(ref notes, count, node, Facet.Payload, Decision.Agreed,
                "both made the same change");
        }

        // Try combining disjoint changed regions. A single region per side may report conflicts
        // that a more detailed domain diff could avoid.
        if (Combine(in table, merged.Nodes[node.Index].Kind, was, mine, theirsBytes,
                out byte[] combined))
        {
            SourceBuild.SetPayload(ref merged, node, combined);
            return Note(ref notes, count, node, Facet.Payload, Decision.Combined,
                "both changed the payload, in regions that do not overlap");
        }

        return Note(ref notes, count, node, Facet.Payload, Decision.Conflict,
            "both changed the payload in the same place; ours stands");
    }

    /// <summary>
    /// Combines disjoint changes in common-state coordinates. Returns false for overlapping
    /// changes, atomic payloads or an unchanged side.
    /// </summary>
    static bool Combine(
        in KindTable table,
        KindId kind,
        ReadOnlySpan<byte> was,
        ReadOnlySpan<byte> mine,
        ReadOnlySpan<byte> theirs,
        out byte[] combined)
    {
        combined = null;

        // Only text and records with a declared stride have independently mergeable units here.
        // Treat opaque and unshaped record payloads as atomic.
        KindSchema schema = KindTable.SchemaOf(in table, kind);
        PayloadDiff.Shape shape = PayloadDiff.ShapeOf(in schema);
        if (shape != PayloadDiff.Shape.Text && shape != PayloadDiff.Shape.Array)
        {
            return false;
        }

        if (!PayloadDiff.Region(shape, in schema.Stride, was, mine,
                out int ourAt, out int ourRemoved, out int ourInserted))
        {
            return false;
        }

        if (!PayloadDiff.Region(shape, in schema.Stride, was, theirs,
                out int theirAt, out int theirRemoved, out int theirInserted))
        {
            return false;
        }

        int at;
        if (ourAt + ourRemoved <= theirAt)
        {
            // Ours comes first, so theirs has moved by the difference ours made.
            at = theirAt + ourInserted - ourRemoved;
        }
        else if (theirAt + theirRemoved <= ourAt)
        {
            // Theirs comes first, inside the part of ours that is still the common state.
            at = theirAt;
        }
        else
        {
            return false;
        }

        if (at < 0 || at + theirRemoved > mine.Length)
        {
            return false;
        }

        combined = new byte[mine.Length - theirRemoved + theirInserted];
        mine.Slice(0, at).CopyTo(combined);
        theirs.Slice(theirAt, theirInserted).CopyTo(new Span<byte>(combined, at, theirInserted));
        mine.Slice(at + theirRemoved).CopyTo(
            new Span<byte>(combined, at + theirInserted, mine.Length - at - theirRemoved));
        return true;
    }

    static int Containment(
        ref Source merged,
        in Source common,
        in Source theirs,
        in Lens lens,
        NodeId node,
        ref MergeNote[] notes,
        int count)
    {
        NodeId was = SourceWalk.Parent(in common, node);
        NodeId mine = SourceWalk.Parent(in merged, node);
        NodeId want = Remap(in common, lens.Nodes, SourceWalk.Parent(in theirs, node));

        if (want == was)
        {
            return count;
        }

        if (mine != was)
        {
            return mine == want
                ? Note(ref notes, count, node, Facet.Containment, Decision.Agreed,
                    "both moved it to the same place")
                : Note(ref notes, count, node, Facet.Containment, Decision.Conflict,
                    "both moved it, to different places; ours stands");
        }

        if (want.IsNone || !merged.IsLive(want))
        {
            return Note(ref notes, count, node, Facet.Containment, Decision.Unrepresentable,
                "they moved it under a node that is not here; ours stands");
        }

        // Cycles are the one thing containment refuses, and a merge is exactly where two
        // innocent moves make one.
        if (Contains(in merged, node, want))
        {
            return Note(ref notes, count, node, Facet.Containment, Decision.Unrepresentable,
                "their move would make containment cyclic; ours stands");
        }

        SourceBuild.Move(ref merged, node, want, SourceWalk.ChildCount(in merged, want));
        return Note(ref notes, count, node, Facet.Containment, Decision.OneSide,
            "moved by them");
    }

    static int ChildOrder(
        ref Source merged,
        in Source common,
        in Source theirs,
        NodeId parent,
        ref MergeNote[] notes,
        int count)
    {
        ReadOnlySpan<long> was = SourceWalk.Children(in common, parent);
        ReadOnlySpan<long> theirsRun = SourceWalk.Children(in theirs, parent);
        ReadOnlySpan<long> mine = SourceWalk.Children(in merged, parent);

        // Only permutations count as reorder conflicts; insertions, moves and deletions are
        // handled separately.
        bool theyReordered = SameSet(was, theirsRun) && !SameOrder(was, theirsRun);
        if (!theyReordered)
        {
            return count;
        }

        bool weReordered = SameSet(was, mine) && !SameOrder(was, mine);
        if (weReordered)
        {
            return SameOrder(mine, theirsRun)
                ? Note(ref notes, count, parent, Facet.ChildOrder, Decision.Agreed,
                    "both reordered it the same way")
                : Note(ref notes, count, parent, Facet.ChildOrder, Decision.Conflict,
                    "both reordered the children; ours stands");
        }

        // Their ordering cannot be applied directly if membership also changed.
        if (!SameSet(mine, theirsRun))
        {
            return Note(ref notes, count, parent, Facet.ChildOrder, Decision.Unrepresentable,
                "they reordered children that are not the ones here; ours stands");
        }

        for (int at = 0; at < theirsRun.Length; at++)
        {
            ReadOnlySpan<long> have = SourceWalk.Children(in merged, parent);
            if (have[at] == theirsRun[at])
            {
                continue;
            }

            for (int c = at + 1; c < have.Length; c++)
            {
                if (have[c] == theirsRun[at])
                {
                    SourceBuild.ReorderChild(ref merged, parent, c, at);
                    break;
                }
            }
        }

        return Note(ref notes, count, parent, Facet.ChildOrder, Decision.OneSide,
            "reordered by them");
    }

    static int Edges(
        ref Source merged,
        in Source common,
        in Source theirs,
        in Lens lens,
        NodeId node,
        ref MergeNote[] notes,
        int count)
    {
        bool theyChanged = !SameRefs(in theirs, node, in lens, in common, node, in Lens.Same, in common);
        if (!theyChanged)
        {
            return count;
        }

        bool weChanged = !SameRefs(in merged, node, in Lens.Same, in common, node, in Lens.Same, in common);

        if (weChanged)
        {
            // Their side is the one that needs translating, whichever side of the
            // comparison it is written on.
            return SameRefs(in merged, node, in Lens.Same, in theirs, node, in lens, in common)
                ? Note(ref notes, count, node, Facet.Edges, Decision.Agreed,
                    "both made the same change to the edges")
                : Note(ref notes, count, node, Facet.Edges, Decision.Conflict,
                    "both changed the edges; ours stands");
        }

        // Only theirs changed the reference run. Replace it as a whole to preserve reference
        // indices.
        for (int r = SourceWalk.RefCount(in merged, node) - 1; r >= 0; r--)
        {
            SourceBuild.RemoveRef(ref merged, node, r);
        }

        ReadOnlySpan<Ref> want = SourceWalk.References(in theirs, node);
        for (int r = 0; r < want.Length; r++)
        {
            Adopt(ref merged, in theirs, in lens, in common, node, in want[r]);
        }

        return Note(ref notes, count, node, Facet.Edges, Decision.OneSide, "changed by them");
    }

    static bool TouchedAnything(in Source common, in Source side, NodeId node)
    {
        if (!side.IsLive(node) || !common.IsLive(node))
        {
            return false;
        }

        return !SourceWalk.Payload(in side, node).SequenceEqual(SourceWalk.Payload(in common, node))
            || SourceWalk.Parent(in side, node) != SourceWalk.Parent(in common, node)
            || !SameOrder(SourceWalk.Children(in side, node), SourceWalk.Children(in common, node))
            || !SameRefs(in side, node, in Lens.Same, in common, node, in Lens.Same, in common);
    }

    /// <summary>Compares ordered reference runs after translating each side’s IDs and handles.</summary>
    static bool SameRefs(
        in Source left,
        NodeId leftNode,
        in Lens leftLens,
        in Source right,
        NodeId rightNode,
        in Lens rightLens,
        in Source common)
    {
        ReadOnlySpan<Ref> a = SourceWalk.References(in left, leftNode);
        ReadOnlySpan<Ref> b = SourceWalk.References(in right, rightNode);

        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Relation(in leftLens, a[i].Relation) != Relation(in rightLens, b[i].Relation))
            {
                return false;
            }

            if (Remap(in common, leftLens.Nodes, a[i].Target)
                != Remap(in common, rightLens.Nodes, b[i].Target))
            {
                return false;
            }

            if (!SourceWalk.EdgeBytes(in left, in a[i]).SequenceEqual(SourceWalk.EdgeBytes(in right, in b[i])))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compares graph state by slot, including retained deleted nodes.</summary>
    static bool SameState(in Source a, in Source b)
    {
        if (a.NodeCount != b.NodeCount || a.Root != b.Root)
        {
            return false;
        }

        for (long i = 0; i < a.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            if (a.IsLive(node) != b.IsLive(node))
            {
                return false;
            }
            if (!a.IsLive(node))
            {
                continue;
            }

            if (a.Nodes[i].Kind != b.Nodes[i].Kind || a.Nodes[i].Parent != b.Nodes[i].Parent)
            {
                return false;
            }
            if (!SourceWalk.Payload(in a, node).SequenceEqual(SourceWalk.Payload(in b, node)))
            {
                return false;
            }
            if (!SameOrder(SourceWalk.Children(in a, node), SourceWalk.Children(in b, node)))
            {
                return false;
            }
            if (!SameRefs(in a, node, in Lens.Same, in b, node, in Lens.Same, in a))
            {
                return false;
            }
        }
        return true;
    }

    static bool SameOrder(ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        return a.SequenceEqual(b);
    }

    static bool SameSet(ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < b.Length; j++)
            {
                if (a[i] == b[j])
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }

    static bool Contains(in Source src, NodeId maybeAncestor, NodeId node)
    {
        NodeId walk = node;
        int steps = 0;
        while (walk.IsSome && steps <= src.NodeCount)
        {
            if (walk == maybeAncestor)
            {
                return true;
            }
            walk = SourceWalk.Parent(in src, walk);
            steps++;
        }
        return false;
    }

    static int Note(
        ref MergeNote[] notes,
        int count,
        NodeId node,
        Facet facet,
        Decision how,
        string detail)
    {
        if (count == notes.Length)
        {
            Array.Resize(ref notes, notes.Length == 0 ? 16 : notes.Length * 2);
        }

        notes[count].Node = node;
        notes[count].Facet = facet;
        notes[count].How = how;
        notes[count].Detail = detail;
        return count + 1;
    }
}
