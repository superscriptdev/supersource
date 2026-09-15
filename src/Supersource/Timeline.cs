using System;
using System.Runtime.InteropServices;

namespace Supersource.Timeline;

/// <summary>
/// Operations recorded by history and produced by structural diff. Rename and ModifyValue both
/// replace payloads but retain distinct operation kinds for history and merge behavior.
/// </summary>
public enum OpKind : byte
{
    Insert,
    Delete,
    Move,
    Rename,
    ModifyValue,
    Wrap,
    Unwrap,
    Reorder,

    /// <summary>Adds a bound or unbound reference edge.</summary>
    Connect,

    /// <summary>Remove a reference edge. The inverse of <see cref="Connect"/>.</summary>
    Disconnect,

    /// <summary>
    /// Replaces Count bytes at Index with Value, retaining removed bytes in OldValue. Offsets are
    /// byte positions, not character indices.
    /// </summary>
    Splice,
}

/// <summary>
/// An operation on durable node IDs. Applying it captures inverse data; inversion does not replay
/// earlier history.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Operation
{
    public OpKind Kind;

    /// <summary>The node this is about.</summary>
    public NodeId Node;

    /// <summary>Where it went, or came from. Meaning varies by kind.</summary>
    public NodeId Parent;
    public int Index;

    /// <summary>Where it was before, captured so the operation can invert itself.</summary>
    public NodeId OldParent;
    public int OldIndex;

    /// <summary>
    /// Number of siblings wrapped or unwrapped; zero defaults to one for Wrap. For Splice, the
    /// number of bytes removed.
    /// </summary>
    public int Count;

    /// <summary>For Insert and Wrap: the kind of the node created.</summary>
    public KindId NodeKind;

    /// <summary>
    /// For Insert: preserve Derived and Generated metadata on the created node.
    /// Live is controlled by the operation itself. Applications interpret the other flags.
    /// </summary>
    public NodeFlags NodeFlags;

    /// <summary>For Rename and ModifyValue: the payload after, and the payload before.</summary>
    public byte[] Value;
    public byte[] OldValue;

    /// <summary>Relation handle for Connect and Disconnect.</summary>
    public RelationId Relation;

    /// <summary>
    /// Reference target for Connect and Disconnect. None represents an unbound edge whose label is
    /// carried in Value.
    /// </summary>
    public NodeId Target;

}

/// <summary>
/// Applies operations and captures inverse data in place. Invert reads that captured data without
/// consulting a source snapshot.
/// </summary>
public static class OpApply
{
    public static void Apply(ref Source src, ref Operation op)
    {
        switch (op.Kind)
        {
            case OpKind.Insert:
                ApplyInsert(ref src, ref op);
                break;
            case OpKind.Delete:
                ApplyDelete(ref src, ref op);
                break;
            case OpKind.Move:
                ApplyMove(ref src, ref op);
                break;
            case OpKind.Rename:
            case OpKind.ModifyValue:
                ApplyValue(ref src, ref op);
                break;
            case OpKind.Wrap:
                ApplyWrap(ref src, ref op);
                break;
            case OpKind.Unwrap:
                ApplyUnwrap(ref src, ref op);
                break;
            case OpKind.Reorder:
                ApplyReorder(ref src, ref op);
                break;
            case OpKind.Connect:
                ApplyConnect(ref src, ref op);
                break;
            case OpKind.Disconnect:
                ApplyDisconnect(ref src, ref op);
                break;
            case OpKind.Splice:
                ApplySplice(ref src, ref op);
                break;
        }
    }

    /// <summary>
    /// The operation that undoes this one. Pure — it reads only the captured fields, never
    /// the source.
    /// </summary>
    public static Operation Invert(in Operation op)
    {
        Operation inverse = op;

        switch (op.Kind)
        {
            case OpKind.Insert:
                inverse.Kind = OpKind.Delete;
                inverse.Parent = op.Parent;
                inverse.Index = op.Index;
                break;

            case OpKind.Delete:
                // Reinsert the retained slot so incoming references resolve to the same identity.
                inverse.Kind = OpKind.Insert;
                inverse.Parent = op.OldParent;
                inverse.Index = op.OldIndex;
                break;

            case OpKind.Move:
                inverse.Parent = op.OldParent;
                inverse.Index = op.OldIndex;
                inverse.OldParent = op.Parent;
                inverse.OldIndex = op.Index;
                break;

            case OpKind.Rename:
            case OpKind.ModifyValue:
                inverse.Value = op.OldValue;
                inverse.OldValue = op.Value;
                break;

            case OpKind.Splice:
                // Replace the inserted bytes with the captured bytes at the original offset.
                inverse.Value = op.OldValue;
                inverse.OldValue = op.Value;
                inverse.Count = op.Value == null ? 0 : op.Value.Length;
                break;

            case OpKind.Wrap:
                // Undo by dissolving the wrapper this operation created. Apply recorded
                // its id into Node, so the inverse knows exactly which node to remove.
                inverse.Kind = OpKind.Unwrap;
                break;

            case OpKind.Unwrap:
                // Undo by re-creating the wrapper at the same place, reviving its id, and
                // taking back in exactly the children it let out.
                inverse.Kind = OpKind.Wrap;
                inverse.Parent = op.Parent;
                inverse.Index = op.Index;
                inverse.Count = op.Count;
                break;

            case OpKind.Reorder:
                inverse.Index = op.OldIndex;
                inverse.OldIndex = op.Index;
                break;

            case OpKind.Connect:
                // Apply wrote back the index it actually used, so the inverse removes
                // exactly that edge and not whichever one happens to sit at the end.
                inverse.Kind = OpKind.Disconnect;
                break;

            case OpKind.Disconnect:
                // Reconnect the removed edge using the captured relation, target and data.
                inverse.Kind = OpKind.Connect;
                break;
        }

        return inverse;
    }

    static void ApplyInsert(ref Source src, ref Operation op)
    {
        if (op.Node.IsSome && src.WasAllocated(op.Node))
        {
            // Restore the retained slot and its subtree instead of allocating another ID.
            src.Nodes[op.Node.Index].Parent = NodeId.None;
            SourceBuild.Revive(ref src, op.Node);
            SourceBuild.InsertChild(ref src, op.Parent, op.Index, op.Node);
        }
        else
        {
            op.Node = SourceBuild.AddNode(ref src, op.NodeKind);
            SourceBuild.InsertChild(ref src, op.Parent, op.Index, op.Node);
        }

        // Live is controlled by insertion; copy the other recorded flags.
        src.Nodes[op.Node.Index].Flags |= op.NodeFlags & ~NodeFlags.Live;

        if (op.Value != null && op.Value.Length > 0)
        {
            SourceBuild.SetPayload(ref src, op.Node, op.Value);
        }
    }

    static void ApplyDelete(ref Source src, ref Operation op)
    {
        op.OldParent = SourceWalk.Parent(in src, op.Node);
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        op.NodeKind = src.Nodes[op.Node.Index].Kind;
        op.NodeFlags = src.Nodes[op.Node.Index].Flags & ~NodeFlags.Live;
        op.OldValue = SourceWalk.Payload(in src, op.Node).ToArray();

        SourceBuild.Delete(ref src, op.Node);
    }

    static void ApplyMove(ref Source src, ref Operation op)
    {
        op.OldParent = SourceWalk.Parent(in src, op.Node);
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        SourceBuild.Move(ref src, op.Node, op.Parent, op.Index);
    }

    static void ApplyValue(ref Source src, ref Operation op)
    {
        op.OldValue = SourceWalk.Payload(in src, op.Node).ToArray();
        SourceBuild.SetPayload(ref src, op.Node, op.Value ?? new byte[0]);
    }

    /// <summary>
    /// Moves consecutive siblings into a wrapper at their former position. Captures the range so
    /// Unwrap can invert it without changing child IDs.
    /// </summary>
    static void ApplyWrap(ref Source src, ref Operation op)
    {
        NodeId parent = op.Parent;
        int at = op.Index;
        int count = op.Count < 1 ? 1 : op.Count;

        NodeId[] taken = new NodeId[count];
        for (int i = 0; i < count; i++)
        {
            taken[i] = SourceWalk.ChildAt(in src, parent, at + i);
        }
        for (int i = 0; i < count; i++)
        {
            SourceBuild.Detach(ref src, taken[i]);
        }

        // Reuse the retained wrapper when undoing Unwrap; otherwise allocate one.
        NodeId wrapper = op.Node.IsSome && src.WasAllocated(op.Node)
            ? Revive(ref src, op.Node)
            : SourceBuild.AddNode(ref src, op.NodeKind);

        SourceBuild.InsertChild(ref src, parent, at, wrapper);
        for (int i = 0; i < count; i++)
        {
            SourceBuild.AppendChild(ref src, wrapper, taken[i]);
        }

        op.Node = wrapper;
        op.Count = count;
        op.OldParent = parent;
        op.OldIndex = at;
    }

    /// <summary>Replaces a wrapper with its children and retains the deleted wrapper for undo.</summary>
    static void ApplyUnwrap(ref Source src, ref Operation op)
    {
        NodeId wrapper = op.Node;
        NodeId parent = SourceWalk.Parent(in src, wrapper);
        int at = SourceWalk.IndexInParent(in src, wrapper);

        int count = SourceWalk.ChildCount(in src, wrapper);
        NodeId[] children = new NodeId[count];
        for (int i = 0; i < count; i++)
        {
            children[i] = SourceWalk.ChildAt(in src, wrapper, i);
        }
        for (int i = 0; i < count; i++)
        {
            SourceBuild.Detach(ref src, children[i]);
        }

        SourceBuild.Detach(ref src, wrapper);
        for (int i = 0; i < count; i++)
        {
            SourceBuild.InsertChild(ref src, parent, at + i, children[i]);
        }

        src.Nodes[wrapper.Index].Flags &= ~NodeFlags.Live;

        op.NodeKind = src.Nodes[wrapper.Index].Kind;
        op.NodeFlags = src.Nodes[wrapper.Index].Flags & ~NodeFlags.Live;
        op.Parent = parent;
        op.Index = at;
        op.Count = count;
        op.OldParent = parent;
        op.OldIndex = at;
    }

    static void ApplyReorder(ref Source src, ref Operation op)
    {
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        SourceBuild.ReorderChild(ref src, op.Parent, op.OldIndex, op.Index);
    }

    /// <summary>Adds a reference, including an unbound edge when Target is None.</summary>
    static void ApplyConnect(ref Source src, ref Operation op)
    {
        // Preserve bytes for both unresolved labels and bound edge data.
        Ref edge = SourceBuild.EdgeFor(
            ref src, op.Relation, op.Target, op.Value ?? Array.Empty<byte>());

        // Capture the actual insertion index for Disconnect to invert.
        op.Index = SourceBuild.InsertRef(ref src, op.Node, op.Index, edge);
    }

    /// <summary>
    /// Removes a reference and captures its target, relation and data bytes for undo. Arena offsets
    /// are not transferable between sources.
    /// </summary>
    static void ApplyDisconnect(ref Source src, ref Operation op)
    {
        int count = SourceWalk.RefCount(in src, op.Node);
        if (op.Index < 0 || op.Index >= count)
        {
            // There is no edge at this index to remove.
            return;
        }

        Ref edge = SourceWalk.RefAt(in src, op.Node, op.Index);
        op.Relation = edge.Relation;
        op.Target = edge.Target;
        op.Value = SourceWalk.EdgeBytes(in src, in edge).ToArray();

        SourceBuild.RemoveRef(ref src, op.Node, op.Index);
    }

    /// <summary>Clamps the byte range, captures removed bytes in OldValue and applies the splice.</summary>
    static void ApplySplice(ref Source src, ref Operation op)
    {
        if (!src.IsLive(op.Node))
        {
            return;
        }

        // Clamped here as well as inside Splice, because the capture below has to be sized
        // to what will actually come out.
        int length = SourceWalk.Payload(in src, op.Node).Length;

        int at = op.Index < 0 ? 0 : op.Index;
        if (at > length)
        {
            at = length;
        }

        int removing = op.Count < 0 ? 0 : op.Count;
        if (at + removing > length)
        {
            removing = length - at;
        }

        byte[] captured = removing == 0 ? Array.Empty<byte>() : new byte[removing];

        SourceBuild.Splice(ref src, op.Node, at, removing,
            op.Value ?? ReadOnlySpan<byte>.Empty, captured, out int removedLength);

        op.OldValue = captured;
    }

    static NodeId Revive(ref Source src, NodeId id)
    {
        src.Nodes[id.Index].Flags |= NodeFlags.Live;
        src.Nodes[id.Index].Parent = NodeId.None;
        return id;
    }
}

/// <summary>A recorded operation with actor, ordering and in-memory persistence metadata.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Entry
{
    public Operation Op;

    /// <summary>Caller-supplied actor identifier.</summary>
    public int Actor;

    /// <summary>Operation order assigned by the log or preserved from an adopted entry.</summary>
    public long Order;

    /// <summary>
    /// Stamps before and after applying the operation. Incremental saving requires an unbroken
    /// stamp chain; direct unlogged mutations force a full rewrite. Adopted entries have zero
    /// stamps. These fields are not serialized.
    /// </summary>
    public ulong StampBefore, StampAfter;
}

/// <summary>
/// An ordered operation log with sequential undo and redo. Applications own gesture
/// grouping, caret restoration and presentation filtering. Only the applied prefix is
/// persisted by HistoryCodec; a new edit discards the undone suffix.
/// </summary>
public struct Log
{
    public Entry[] Entries;
    public int Count;

    /// <summary>
    /// How far into Entries the source currently reflects. Entries beyond it have been
    /// undone but not discarded — redo is walking forward again.
    /// </summary>
    public int At;

    long nextOrder;

    public static Log Create(int capacity = 64)
    {
        Log log = default;
        log.Entries = new Entry[capacity < 4 ? 4 : capacity];
        log.Count = 0;
        log.At = 0;
        log.nextOrder = 0;
        return log;
    }

    /// <summary>Applies an operation before recording it, so the entry includes captured inverse data.</summary>
    public static void Do(ref Log log, ref Source src, Operation op, int actor = 0)
    {
        ulong before = src.Stamp;
        OpApply.Apply(ref src, ref op);

        // Discard the redo suffix when a new edit is applied.
        log.Count = log.At;

        if (log.Count == log.Entries.Length)
        {
            Array.Resize(ref log.Entries, log.Entries.Length == 0 ? 16 : log.Entries.Length * 2);
        }

        log.Entries[log.Count].Op = op;
        log.Entries[log.Count].Actor = actor;
        log.Entries[log.Count].Order = log.nextOrder;
        log.Entries[log.Count].StampBefore = before;
        log.Entries[log.Count].StampAfter = src.Stamp;
        log.nextOrder++;
        log.Count++;
        log.At = log.Count;
    }

    /// <summary>
    /// Records an operation already reflected in the source, without applying it again. Preserves
    /// its order and advances the next local order beyond it.
    /// </summary>
    public static void Adopt(ref Log log, Entry entry)
    {
        if (log.Count == log.Entries.Length)
        {
            Array.Resize(ref log.Entries, log.Entries.Length == 0 ? 16 : log.Entries.Length * 2);
        }

        log.Entries[log.Count] = entry;
        log.Count++;
        log.At = log.Count;

        if (entry.Order >= log.nextOrder)
        {
            log.nextOrder = entry.Order + 1;
        }
    }

    public static bool CanUndo(in Log log)
    {
        return log.At > 0;
    }

    public static bool CanRedo(in Log log)
    {
        return log.At < log.Count;
    }

    /// <summary>Reverse the last entry. Returns false if there is nothing to reverse.</summary>
    public static bool Undo(ref Log log, ref Source src)
    {
        if (log.At == 0)
        {
            return false;
        }

        log.At--;
        Operation inverse = OpApply.Invert(in log.Entries[log.At].Op);
        OpApply.Apply(ref src, ref inverse);
        return true;
    }

    public static bool Redo(ref Log log, ref Source src)
    {
        if (log.At >= log.Count)
        {
            return false;
        }

        Operation op = log.Entries[log.At].Op;
        OpApply.Apply(ref src, ref op);
        log.Entries[log.At].Op = op;
        log.At++;
        return true;
    }

}
