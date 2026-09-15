using System;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Resolve;

/// <summary>
/// Finds declarations using the Declaration and OpensScope schema facts. Returns candidates in
/// lexical order without choosing a binding.
/// </summary>
public static class Scope
{
    /// <summary>
    /// Returns visible declarations, innermost scope first and source order within each scope.
    /// Grows into as needed and returns the number written.
    /// </summary>
    public static int Visible(
        in Source src,
        in KindTable table,
        NodeId at,
        ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[16];
        }

        int count = 0;
        if (!src.IsLive(at))
        {
            return 0;
        }

        // Walk outwards. The node itself counts as a scope if it opens one — a body's
        // locals are visible to things inside the body, including to the body's own
        // children.
        NodeId walk = at;
        while (walk.IsSome)
        {
            if (OpensScope(in src, in table, walk))
            {
                count = CollectDeclarations(in src, in table, walk, ref into, count);
            }
            walk = src.Nodes[walk.Index].Parent;
        }

        return count;
    }

    /// <summary>Collects descendant declarations, stopping at nested scope boundaries.</summary>
    static int CollectDeclarations(
        in Source src,
        in KindTable table,
        NodeId scope,
        ref NodeId[] into,
        int count)
    {
        ReadOnlySpan<long> children = SourceWalk.Children(in src, scope);
        for (int i = 0; i < children.Length; i++)
        {
            NodeId child = new NodeId(children[i]);
            if (!src.IsLive(child))
            {
                continue;
            }

            if (IsDeclaration(in src, in table, child))
            {
                if (count == into.Length)
                {
                    Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
                }
                into[count] = child;
                count++;
            }

            // Include a scope-opening declaration itself, but not its interior.
            if (!OpensScope(in src, in table, child))
            {
                count = CollectDeclarations(in src, in table, child, ref into, count);
            }
        }

        return count;
    }

    public static bool OpensScope(in Source src, in KindTable table, NodeId id)
    {
        return KindTable.SchemaOf(in table, src.Nodes[id.Index].Kind).OpensScope;
    }

    public static bool IsDeclaration(in Source src, in KindTable table, NodeId id)
    {
        return KindTable.SchemaOf(in table, src.Nodes[id.Index].Kind).IsDeclaration;
    }

    /// <summary>
    /// Returns the nearest scope, including at itself if it opens one. Start at its parent to find
    /// the outer scope.
    /// </summary>
    public static NodeId EnclosingScope(in Source src, in KindTable table, NodeId at)
    {
        NodeId walk = at;
        while (walk.IsSome)
        {
            if (OpensScope(in src, in table, walk))
            {
                return walk;
            }
            walk = src.Nodes[walk.Index].Parent;
        }
        return NodeId.None;
    }
}

/// <summary>
/// Matches UTF-8 declaration labels in lexical scope. Candidates exposes all matches; Bind is a
/// convenience that selects the first.
/// </summary>
public static class Binder
{
    /// <summary>
    /// Returns matching UTF-8 declarations, innermost first. Other payload formats do not match;
    /// callers can apply a custom matcher to Scope.Visible.
    /// </summary>
    public static int Candidates(
        in Source src,
        in KindTable table,
        NodeId at,
        ReadOnlySpan<byte> label,
        ref NodeId[] into)
    {
        NodeId[] visible = null;
        int count = Scope.Visible(in src, in table, at, ref visible);

        if (into == null)
        {
            into = new NodeId[8];
        }

        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (!HasTextLabel(in src, in table, visible[i], label))
            {
                continue;
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = visible[i];
            found++;
        }

        return found;
    }

    /// <summary>
    /// Adds a reference to the first matching declaration, or a labelled hole if no match exists.
    /// Returns the reference index. Use Candidates when the caller must choose among matches.
    /// </summary>
    public static int Bind(
        ref Source src,
        in KindTable table,
        NodeId from,
        RelationId relation,
        ReadOnlySpan<byte> label)
    {
        NodeId[] candidates = null;
        int count = Candidates(in src, in table, from, label, ref candidates);

        if (count == 0)
        {
            return SourceBuild.AddUnboundRef(ref src, from, relation, label);
        }

        return SourceBuild.AddRef(ref src, from, relation, candidates[0]);
    }

    /// <summary>Convenience for the text case, which is most of them.</summary>
    public static int Bind(
        ref Source src,
        in KindTable table,
        NodeId from,
        RelationId relation,
        string label)
    {
        return Bind(ref src, in table, from, relation, Encoding.UTF8.GetBytes(label));
    }

    static bool HasTextLabel(
        in Source src,
        in KindTable table,
        NodeId id,
        ReadOnlySpan<byte> label)
    {
        if (KindTable.SchemaOf(in table, src.Nodes[id.Index].Kind).Payload != PayloadFormat.Utf8)
        {
            return false;
        }

        return SourceWalk.Payload(in src, id).SequenceEqual(label);
    }
}

/// <summary>
/// What a reference actually resolves to, once the source is in hand.
/// </summary>
public enum Resolution
{
    /// <summary>Points at a live node. The ordinary case.</summary>
    Bound,

    /// <summary>An unbound edge with no target.</summary>
    Hole,

    /// <summary>Points at a slot that was allocated and later deleted.</summary>
    Deleted,

    /// <summary>Points at a slot that was never allocated. Only reachable from a damaged file.</summary>
    OutOfRange,
}

/// <summary>
/// Inspects reference targets and optionally converts dangling references into labelled holes. Link
/// is an explicit mutation; loading a source does not invoke it.
/// </summary>
public static class Linker
{
    public static Resolution Resolve(in Source src, in Ref edge)
    {
        if (edge.IsUnbound)
        {
            return Resolution.Hole;
        }
        if (!src.WasAllocated(edge.Target))
        {
            return Resolution.OutOfRange;
        }
        if (!src.IsLive(edge.Target))
        {
            return Resolution.Deleted;
        }
        return Resolution.Bound;
    }

    /// <summary>
    /// Converts dangling targets to unbound references, recovering UTF-8 labels from retained
    /// deleted nodes when possible. Returns the number changed.
    /// </summary>
    public static int Link(ref Source src, in KindTable table)
    {
        int repaired = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            if (!src.IsLive(new NodeId(i)))
            {
                continue;
            }

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;

            for (int r = 0; r < count; r++)
            {
                Ref edge = src.Refs[offset + r];
                Resolution state = Resolve(in src, in edge);
                if (state == Resolution.Bound || state == Resolution.Hole)
                {
                    continue;
                }

                byte[] label = RecoverLabel(in src, in table, edge.Target);
                SourceBuild.UnbindRef(ref src, new NodeId(i), r, label);
                repaired++;

                // UnbindRef may have grown the blob arena, which does not move the ref run,
                // but the local copy of the offset is worth re-reading anyway.
                offset = src.Nodes[i].RefOffset;
            }
        }

        return repaired;
    }

    /// <summary>Recovers a retained UTF-8 label, or returns empty bytes when none is available.</summary>
    static byte[] RecoverLabel(in Source src, in KindTable table, NodeId target)
    {
        if (!src.WasAllocated(target))
        {
            return new byte[0];
        }

        if (KindTable.SchemaOf(in table, src.Nodes[target.Index].Kind).Payload != PayloadFormat.Utf8)
        {
            return new byte[0];
        }

        return SourceWalk.Payload(in src, target).ToArray();
    }
}
