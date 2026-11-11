using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supersource;

[Flags]
public enum NodeFlags : byte
{
    None = 0,

    /// <summary>
    /// The slot holds a node. A slot below the source's next id without this flag held a node
    /// that was deleted; its id is never handed out again.
    /// </summary>
    Live = 1 << 0,
}

/// <summary>
/// A node’s kind, flags and arena locations. Its ID is its slot in Source.Nodes. Capacity and
/// revision arrays are stored separately from traversal data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Node
{
    /// <summary>
    /// Parent node, or None when detached or at the root. Kept alongside child runs for upward
    /// traversal.
    /// </summary>
    public NodeId Parent;

    /// <summary>Segmented address of the payload. Each payload occupies one segment.</summary>
    public long PayloadOffset;

    /// <summary>Payload bytes: 64-bit, so one payload may be larger than an array.</summary>
    public long PayloadLength;

    /// <summary>Kind handle into the source’s KindTable.</summary>
    public KindId Kind;

    public int ChildOffset;
    public int ChildCount;

    public int RefOffset;
    public int RefCount;

    public NodeFlags Flags;

    public bool IsLive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Flags & NodeFlags.Live) != 0; }
    }

    public bool HasPayload
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return PayloadLength > 0; }
    }
}

/// <summary>
/// A typed reference. An unbound edge has no target; a dangling edge retains the ID of a deleted
/// target. Deletion does not rewrite incoming references.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Ref
{
    /// <summary>Relation handle into the source’s KindTable.</summary>
    public RelationId Relation;

    /// <summary>The node pointed at, or NodeId.None when unbound.</summary>
    public NodeId Target;

    /// <summary>Offset of the edge data in Source.EdgeData: an unbound reference's label, or a bound one's data.</summary>
    public int DataOffset;

    /// <summary>Edge data length in bytes. Zero means none; an unbound reference with no label is legal.</summary>
    public int DataLength;

    public bool IsBound
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Target.IsSome; }
    }

    public bool IsUnbound
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Target.IsNone; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Ref Bound(RelationId relation, NodeId target)
    {
        Ref r;
        r.Relation = relation;
        r.Target = target;
        r.DataOffset = 0;
        r.DataLength = 0;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Ref Unbound(RelationId relation, int dataOffset, int dataLength)
    {
        Ref r;
        r.Relation = relation;
        r.Target = NodeId.None;
        r.DataOffset = dataOffset;
        r.DataLength = dataLength;
        return r;
    }
}
