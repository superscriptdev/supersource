using System;
using System.Runtime.CompilerServices;

namespace Supersource;
/// <summary>
/// Read-only graph queries. Containment, outgoing references and incoming references are separate
/// traversals.
/// </summary>
public static class SourceWalk
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChildCount(in Source src, NodeId id)
    {
        return src.Nodes[id.Index].ChildCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeId ChildAt(in Source src, NodeId id, int index)
    {
        if (index < 0 || index >= src.Nodes[id.Index].ChildCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return new NodeId(src.Children[src.Nodes[id.Index].ChildOffset + index]);
    }

    /// <summary>The child run as a span of raw slot indices. Ordered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<long> Children(in Source src, NodeId id)
    {
        return new ReadOnlySpan<long>(
            src.Children,
            src.Nodes[id.Index].ChildOffset,
            src.Nodes[id.Index].ChildCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeId Parent(in Source src, NodeId id)
    {
        return src.Nodes[id.Index].Parent;
    }

    /// <summary>Index of this node among its parent's children, or -1 if it has no parent.</summary>
    public static int IndexInParent(in Source src, NodeId id)
    {
        NodeId parent = src.Nodes[id.Index].Parent;
        if (parent.IsNone)
        {
            return -1;
        }

        int offset = src.Nodes[parent.Index].ChildOffset;
        int count = src.Nodes[parent.Index].ChildCount;
        for (int i = 0; i < count; i++)
        {
            if (src.Children[offset + i] == id.Index)
            {
                return i;
            }
        }
        return -1;
    }

    public static int Depth(in Source src, NodeId id)
    {
        int depth = 0;
        NodeId walk = src.Nodes[id.Index].Parent;
        while (walk.IsSome)
        {
            depth++;
            walk = src.Nodes[walk.Index].Parent;
        }
        return depth;
    }

    public static bool IsAncestorOf(in Source src, NodeId ancestor, NodeId node)
    {
        NodeId walk = src.Nodes[node.Index].Parent;
        while (walk.IsSome)
        {
            if (walk == ancestor)
            {
                return true;
            }
            walk = src.Nodes[walk.Index].Parent;
        }
        return false;
    }

    /// <summary>
    /// Every node contained by <paramref name="root"/>, depth-first, root first.
    /// Appends into <paramref name="into"/> and returns how many were written.
    /// </summary>
    public static int Descendants(in Source src, NodeId root, ref NodeId[] into, int count = 0)
    {
        if (into == null)
        {
            into = new NodeId[16];
        }
        if (count == into.Length)
        {
            Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
        }

        into[count] = root;
        count++;

        int offset = src.Nodes[root.Index].ChildOffset;
        int children = src.Nodes[root.Index].ChildCount;
        for (int i = 0; i < children; i++)
        {
            count = Descendants(in src, new NodeId(src.Children[offset + i]), ref into, count);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RefCount(in Source src, NodeId id)
    {
        return src.Nodes[id.Index].RefCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Ref RefAt(in Source src, NodeId id, int index)
    {
        if (index < 0 || index >= src.Nodes[id.Index].RefCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return src.Refs[src.Nodes[id.Index].RefOffset + index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<Ref> References(in Source src, NodeId id)
    {
        return new ReadOnlySpan<Ref>(
            src.Refs,
            src.Nodes[id.Index].RefOffset,
            src.Nodes[id.Index].RefCount);
    }

    /// <summary>Returns the first matching reference index, or -1. A relation may occur more than once.</summary>
    public static int FindRef(in Source src, NodeId id, RelationId relation)
    {
        int offset = src.Nodes[id.Index].RefOffset;
        int count = src.Nodes[id.Index].RefCount;
        for (int i = 0; i < count; i++)
        {
            if (src.Refs[offset + i].Relation == relation)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Target of the first reference with this relation, or None if absent or unbound.</summary>
    public static NodeId Target(in Source src, NodeId id, RelationId relation)
    {
        int at = FindRef(in src, id, relation);
        return at < 0 ? NodeId.None : src.Refs[src.Nodes[id.Index].RefOffset + at].Target;
    }

    /// <summary>What a reference points at: a live node, nothing, a deleted node, or an id never given out.</summary>
    public static RefState StateOf(in Source src, in Ref edge)
    {
        if (edge.IsUnbound)
        {
            return RefState.Unbound;
        }
        if (!src.WasAllocated(edge.Target))
        {
            return RefState.OutOfRange;
        }
        return src.IsLive(edge.Target) ? RefState.Bound : RefState.Dangling;
    }

    /// <summary>
    /// Returns live nodes referring to the target by scanning the source. Use Backrefs for repeated
    /// incoming-edge queries.
    /// </summary>
    public static int Referrers(in Source src, NodeId target, ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[16];
        }

        int found = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            if ((src.Nodes[i].Flags & NodeFlags.Live) == 0)
            {
                continue;
            }

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                if (src.Refs[offset + r].Target == target)
                {
                    if (found == into.Length)
                    {
                        Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
                    }
                    into[found] = new NodeId(i);
                    found++;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Returns nodes with unbound references or references to deleted targets. Does not repair the
    /// edges.
    /// </summary>
    public static int Dangling(in Source src, ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[16];
        }

        int found = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            if ((src.Nodes[i].Flags & NodeFlags.Live) == 0)
            {
                continue;
            }

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                NodeId target = src.Refs[offset + r].Target;
                bool broken = target.IsNone || !src.IsLive(target);
                if (broken)
                {
                    if (found == into.Length)
                    {
                        Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
                    }
                    into[found] = new NodeId(i);
                    found++;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The node's payload as one span. A payload larger than a span can hold, or held in chunks,
    /// is read with <see cref="ReadPayload"/> instead, and asking for it here says so.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Payload(in Source src, NodeId id)
    {
        long length = src.Nodes[id.Index].PayloadLength;
        if (length > int.MaxValue)
            throw new InvalidOperationException("this payload is larger than one span; read it with ReadPayload");
        return Source.Bytes(in src, src.Nodes[id.Index].PayloadOffset, (int)length);
    }

    /// <summary>
    /// Whether the payload is one run of memory, so <see cref="Payload"/> can return it. A payload
    /// held in chunks, or larger than a span, is read in windows instead.
    /// </summary>
    public static bool IsContiguous(in Source src, NodeId id)
    {
        long length = src.Nodes[id.Index].PayloadLength;
        return length == 0 || Source.IsContiguous(in src, src.Nodes[id.Index].PayloadOffset, length);
    }

    /// <summary>The window payloads of any size are compared and hashed in.</summary>
    public const int Window = 1 << 20;

    /// <summary>Whether two nodes hold the same payload bytes, compared in windows when they must be.</summary>
    public static bool SamePayload(in Source a, NodeId x, in Source b, NodeId y)
    {
        long length = a.Nodes[x.Index].PayloadLength;
        if (length != b.Nodes[y.Index].PayloadLength) return false;
        if (IsContiguous(in a, x) && IsContiguous(in b, y)) return Payload(in a, x).SequenceEqual(Payload(in b, y));

        byte[] left = new byte[(int)Math.Min(length, Window)];
        byte[] right = new byte[left.Length];
        for (long at = 0; at < length; at += left.Length)
        {
            int count = (int)Math.Min(left.Length, length - at);
            ReadPayload(in a, x, at, new Span<byte>(left, 0, count));
            ReadPayload(in b, y, at, new Span<byte>(right, 0, count));
            if (!new ReadOnlySpan<byte>(left, 0, count).SequenceEqual(new ReadOnlySpan<byte>(right, 0, count))) return false;
        }
        return true;
    }

    /// <summary>
    /// The payload as a read-only stream, for copying or hashing one of any size. It reads the
    /// source as it is now; edit the node and the stream is no longer about it.
    /// </summary>
    public static System.IO.Stream OpenPayload(in Source src, NodeId id)
    {
        return new PayloadStream(src, id);
    }

    /// <summary>How many payload bytes a node has, whatever their size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PayloadLength(in Source src, NodeId id) => src.Nodes[id.Index].PayloadLength;

    /// <summary>
    /// Copy <paramref name="into"/>.Length payload bytes from <paramref name="offset"/>: the read
    /// that works for a payload of any size, owned, chunked or mapped.
    /// </summary>
    public static void ReadPayload(in Source src, NodeId id, long offset, Span<byte> into)
    {
        if (offset < 0 || offset + into.Length > src.Nodes[id.Index].PayloadLength)
            throw new ArgumentOutOfRangeException(nameof(offset));
        Source.Read(in src, src.Nodes[id.Index].PayloadOffset, offset, into);
    }

    /// <summary>
    /// Returns reference data: the label of an unbound edge, or relation-defined data for a bound
    /// edge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> EdgeData(in Source src, in Ref r)
    {
        if (r.DataLength == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        return new ReadOnlySpan<byte>(src.EdgeData, r.DataOffset, r.DataLength);
    }

    /// <summary>
    /// An unbound reference's label: its edge data, which says what it is waiting for, such as a
    /// name. Empty when the reference is bound.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Label(in Source src, in Ref r)
    {
        return r.IsBound ? ReadOnlySpan<byte>.Empty : EdgeData(in src, in r);
    }
}
