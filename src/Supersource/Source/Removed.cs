using Supersource.Kinds;

namespace Supersource;

/// <summary>
/// What a deletion removed, kept by the operation that removed it so the deletion can be taken
/// back under the same ids. A source keeps nothing of a deleted node; this is the only copy, and
/// it lives exactly as long as whoever holds the operation keeps it.
/// </summary>
public sealed class Removed
{
    /// <summary>The removed nodes, the subtree's root first. Every node's children are here too.</summary>
    public RemovedNode[] Nodes;

    /// <summary>
    /// False when a payload was larger than an array and its bytes are not here: the subtree
    /// cannot be put back.
    /// </summary>
    public bool Whole = true;
}

/// <summary>One removed node: everything a source held for it.</summary>
public struct RemovedNode
{
    public NodeId Id;
    public KindId Kind;
    public byte[] Payload;

    /// <summary>Its children in order. Each is also in <see cref="Removed.Nodes"/>.</summary>
    public NodeId[] Children;

    public RemovedRef[] Refs;
}

/// <summary>One removed reference, with its edge data copied out of the source.</summary>
public struct RemovedRef
{
    public RelationId Relation;
    public NodeId Target;
    public byte[] Data;
}
