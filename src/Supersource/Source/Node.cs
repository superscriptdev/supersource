using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Supersource;

[Flags]
public enum NodeFlags : byte
{
    None = 0,

    /// <summary>
    /// The node is active. Cleared on deletion and restored by undo for the same node.
    /// A deleted slot is retained; it is never reassigned to a different node.
    /// </summary>
    Live = 1 << 0,

    /// <summary>
    /// Marks computed content. Applications choose its provenance and lifecycle; the
    /// core preserves it like other content.
    /// </summary>
    Derived = 1 << 1,

    /// <summary>
    /// Marks generated content. The flag does not change persistence, identity allocation
    /// or edit permissions. Regeneration is an explicit application operation.
    /// </summary>
    Generated = 1 << 2,
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

    /// <summary>
    /// Segmented address and byte length of the payload. Each payload occupies one segment; its
    /// length is limited to a signed 32-bit count.
    /// </summary>
    public long PayloadOffset;

    /// <summary>Kind handle into the source’s KindTable.</summary>
    public KindId Kind;

    public int ChildOffset;
    public int ChildCount;

    public int RefOffset;
    public int RefCount;

    public int PayloadLength;

    public NodeFlags Flags;

    public bool IsLive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Flags & NodeFlags.Live) != 0; }
    }

    public bool IsDerived
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Flags & NodeFlags.Derived) != 0; }
    }

    public bool IsGenerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (Flags & NodeFlags.Generated) != 0; }
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

    /// <summary>Offset of unresolved label or bound edge-data bytes in Source.Labels.</summary>
    public int LabelOffset;

    /// <summary>Label length in bytes. Zero means an unbound reference with no label — legal.</summary>
    public int LabelLength;

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
        r.LabelOffset = 0;
        r.LabelLength = 0;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Ref Unbound(RelationId relation, int labelOffset, int labelLength)
    {
        Ref r;
        r.Relation = relation;
        r.Target = NodeId.None;
        r.LabelOffset = labelOffset;
        r.LabelLength = labelLength;
        return r;
    }
}
