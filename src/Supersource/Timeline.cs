using System;
using System.Runtime.InteropServices;

namespace Supersource.Timeline;

/// <summary>
/// The ways a source can change. Applied by edits, produced by structural diff and stored in
/// journals. The numbers are wire values: a journal stores them, so they never change and a
/// retired one is never reused.
/// </summary>
public enum OpKind : byte
{
    Insert = 0,
    Delete = 1,
    Move = 2,

    // 3 is retired: Rename, which replaced a payload exactly as ModifyValue does. A version 1
    // journal that carries it is read as ModifyValue.

    /// <summary>Replaces the whole payload with Value, retaining the previous bytes in OldValue.</summary>
    ModifyValue = 4,

    Wrap = 5,
    Unwrap = 6,
    Reorder = 7,

    /// <summary>Adds a bound or unbound reference edge.</summary>
    Connect = 8,

    /// <summary>Remove a reference edge. The inverse of <see cref="Connect"/>.</summary>
    Disconnect = 9,

    /// <summary>
    /// Replaces Count bytes at Index with Value, retaining removed bytes in OldValue. Offsets are
    /// byte positions, not character indices.
    /// </summary>
    Splice = 10,

    /// <summary>Changes a node's kind, keeping its id, payload, children and references.</summary>
    Retype = 11,
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

    /// <summary>
    /// A child or reference position; for Splice, the byte offset. 64-bit, because a payload may
    /// be larger than an array.
    /// </summary>
    public long Index;

    /// <summary>Where it was before, captured so the operation can invert itself.</summary>
    public NodeId OldParent;
    public int OldIndex;

    /// <summary>
    /// Number of siblings wrapped or unwrapped; zero defaults to one for Wrap. For Splice, the
    /// number of bytes removed.
    /// </summary>
    public long Count;

    /// <summary>For Insert and Wrap, the kind of the node created; for Retype, the kind it becomes.</summary>
    public KindId NodeKind;

    /// <summary>For Retype: the kind it had, captured so the operation can invert itself.</summary>
    public KindId OldKind;

    /// <summary>
    /// Set when applying it destroyed more than an operation can hold — over 2 GB of one payload,
    /// in a replace, a splice or a deleted subtree. It stands, saves and journals like any other;
    /// it cannot be taken back.
    /// </summary>
    public bool Uninvertible;

    /// <summary>
    /// For Delete and Unwrap: what the operation removed, captured when it is applied. The source
    /// keeps nothing of a deleted node, so this is what an undo puts back. Never stored in a file.
    /// </summary>
    public Removed Removed;

    /// <summary>
    /// For ModifyValue: the payload after, and the payload before. For Insert, the new node's
    /// payload; for an unbound Connect, the edge's label; for Splice, the bytes inserted and removed.
    /// </summary>
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
            case OpKind.Retype:
                ApplyRetype(ref src, ref op);
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
                inverse.Removed = null;
                break;

            case OpKind.Delete:
                // Put back what the deletion captured, under the same ids, so the references that
                // were left dangling find their targets again.
                inverse.Kind = OpKind.Insert;
                inverse.Parent = op.OldParent;
                inverse.Index = op.OldIndex;
                break;

            case OpKind.Retype:
                inverse.NodeKind = op.OldKind;
                inverse.OldKind = op.NodeKind;
                break;

            case OpKind.Move:
                inverse.Parent = op.OldParent;
                inverse.Index = op.OldIndex;
                inverse.OldParent = op.Parent;
                inverse.OldIndex = Position(op.Index);
                break;

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
                inverse.OldIndex = Position(op.Index);
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
        if (op.Removed != null)
        {
            // Taking a deletion back: the whole subtree, under the ids it had.
            op.Node = SourceBuild.Restore(ref src, op.Removed);
            SourceBuild.InsertChild(ref src, op.Parent, Position(op.Index), op.Node);
            return;
        }

        // An insert that names its id re-creates a node where it was: a redo, a journal replay, a
        // diff. One that does not takes the next id.
        op.Node = op.Node.IsSome
            ? SourceBuild.AddNodeAt(ref src, op.Node, op.NodeKind)
            : SourceBuild.AddNode(ref src, op.NodeKind);
        SourceBuild.InsertChild(ref src, op.Parent, Position(op.Index), op.Node);

        if (op.Value != null && op.Value.Length > 0)
        {
            SourceBuild.SetPayload(ref src, op.Node, op.Value);
        }
    }

    /// <summary>A child or reference position, which a run's 32-bit length bounds.</summary>
    static int Position(long value)
    {
        return checked((int)value);
    }

    static void ApplyDelete(ref Source src, ref Operation op)
    {
        op.OldParent = SourceWalk.Parent(in src, op.Node);
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        op.Removed = SourceBuild.Capture(in src, op.Node);
        op.Uninvertible = !op.Removed.Whole;

        SourceBuild.Delete(ref src, op.Node);
    }

    static void ApplyRetype(ref Source src, ref Operation op)
    {
        op.OldKind = src.Nodes[op.Node.Index].Kind;
        SourceBuild.SetKind(ref src, op.Node, op.NodeKind);
    }

    static void ApplyMove(ref Source src, ref Operation op)
    {
        op.OldParent = SourceWalk.Parent(in src, op.Node);
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        SourceBuild.Move(ref src, op.Node, op.Parent, Position(op.Index));
    }

    static void ApplyValue(ref Source src, ref Operation op)
    {
        // What it replaces is the undo; more than an array holds, and there is none.
        op.Uninvertible = SourceWalk.PayloadLength(in src, op.Node) > Array.MaxLength;
        op.OldValue = op.Uninvertible ? null : SourceWalk.Payload(in src, op.Node).ToArray();
        SourceBuild.SetPayload(ref src, op.Node, op.Value ?? new byte[0]);
    }

    /// <summary>
    /// Moves consecutive siblings into a wrapper at their former position. Captures the range so
    /// Unwrap can invert it without changing child IDs.
    /// </summary>
    static void ApplyWrap(ref Source src, ref Operation op)
    {
        NodeId parent = op.Parent;
        int at = Position(op.Index);
        int count = op.Count < 1 ? 1 : Position(op.Count);

        NodeId[] taken = new NodeId[count];
        for (int i = 0; i < count; i++)
        {
            taken[i] = SourceWalk.ChildAt(in src, parent, at + i);
        }
        for (int i = 0; i < count; i++)
        {
            SourceBuild.Detach(ref src, taken[i]);
        }

        // Undoing an Unwrap puts back the wrapper it captured; a redo re-creates it at its id.
        NodeId wrapper = op.Removed != null
            ? SourceBuild.Restore(ref src, op.Removed)
            : op.Node.IsSome
                ? SourceBuild.AddNodeAt(ref src, op.Node, op.NodeKind)
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

    /// <summary>Replaces a wrapper with its children, capturing the wrapper so an undo can put it back.</summary>
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

        // Its children have moved out, so this captures the wrapper alone.
        op.NodeKind = src.Nodes[wrapper.Index].Kind;
        op.Removed = SourceBuild.Capture(in src, wrapper);
        op.Uninvertible = !op.Removed.Whole;
        SourceBuild.Delete(ref src, wrapper);

        op.Parent = parent;
        op.Index = at;
        op.Count = count;
        op.OldParent = parent;
        op.OldIndex = at;
    }

    static void ApplyReorder(ref Source src, ref Operation op)
    {
        op.OldIndex = SourceWalk.IndexInParent(in src, op.Node);
        SourceBuild.ReorderChild(ref src, op.Parent, op.OldIndex, Position(op.Index));
    }

    /// <summary>Adds a reference, including an unbound edge when Target is None.</summary>
    static void ApplyConnect(ref Source src, ref Operation op)
    {
        // The edge data goes with the edge, bound or unbound.
        Ref edge = SourceBuild.EdgeFor(
            ref src, op.Relation, op.Target, op.Value ?? Array.Empty<byte>());

        // Capture the actual insertion index for Disconnect to invert.
        op.Index = SourceBuild.InsertRef(ref src, op.Node, Position(op.Index), edge);
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

        Ref edge = SourceWalk.RefAt(in src, op.Node, Position(op.Index));
        op.Relation = edge.Relation;
        op.Target = edge.Target;
        op.Value = SourceWalk.EdgeData(in src, in edge).ToArray();

        SourceBuild.RemoveRef(ref src, op.Node, Position(op.Index));
    }

    /// <summary>Clamps the byte range, captures removed bytes in OldValue and applies the splice.</summary>
    static void ApplySplice(ref Source src, ref Operation op)
    {
        if (!src.IsLive(op.Node))
        {
            return;
        }

        // Clamped here as well as inside Splice, because the capture below has to be sized
        // to what will actually come out. What it removes is its undo; more than an array
        // holds, and the splice stands without one.
        long length = SourceWalk.PayloadLength(in src, op.Node);

        long at = Math.Min(op.Index < 0 ? 0 : op.Index, length);
        long removing = Math.Min(op.Count < 0 ? 0 : op.Count, length - at);

        op.Uninvertible = removing > Array.MaxLength;
        byte[] captured = removing == 0 || op.Uninvertible ? Array.Empty<byte>() : new byte[removing];

        SourceBuild.Splice(ref src, op.Node, at, removing,
            op.Value ?? ReadOnlySpan<byte>.Empty, captured, out long removedLength);

        op.OldValue = op.Uninvertible ? null : captured;
    }
}

/// <summary>An applied operation, with the order and stamps the log gave it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Entry
{
    public Operation Op;

    /// <summary>Operation order assigned by the log or preserved from an adopted entry.</summary>
    public long Order;

    /// <summary>
    /// Stamps before and after applying the operation. Incremental saving requires an unbroken
    /// stamp chain; direct unlogged mutations force a full rewrite. Adopted entries have zero
    /// stamps.
    /// </summary>
    public ulong StampBefore, StampAfter;
}

/// <summary>
/// The operations applied to a source since it was opened, with sequential undo and redo. It is
/// session state and is never stored: a file holds a state, not the edits that produced it. A save
/// appends the entries since the previous save as a journal, so the log is also what makes an
/// incremental save possible. How edits group into one undo, and what undo means, belong to the
/// application. A new edit discards the undone suffix.
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

    /// <summary>
    /// An independent copy, including the undone suffix past <see cref="At"/> and the next order
    /// number, so the copy can be undone and redone exactly as far as the original. Each entry's
    /// Value and OldValue are copied rather than shared, so editing one log never reaches the
    /// other's inverse data; a captured <see cref="Removed"/> is shared, since nothing writes to
    /// one after capture.
    /// </summary>
    public static Log Clone(in Log log)
    {
        Log into = default;
        into.Count = log.Count;
        into.At = log.At;
        into.nextOrder = log.nextOrder;

        if (log.Entries == null)
        {
            return into;
        }

        into.Entries = new Entry[log.Entries.Length];
        for (int i = 0; i < log.Count; i++)
        {
            Entry entry = log.Entries[i];
            if (entry.Op.Value != null)
            {
                entry.Op.Value = (byte[])entry.Op.Value.Clone();
            }
            if (entry.Op.OldValue != null)
            {
                entry.Op.OldValue = (byte[])entry.Op.OldValue.Clone();
            }
            into.Entries[i] = entry;
        }

        return into;
    }

    /// <summary>Applies an operation before recording it, so the entry includes captured inverse data.</summary>
    public static void Do(ref Log log, ref Source src, Operation op)
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

    /// <summary>
    /// Takes back the last operation. False when there is none, or when it destroyed more than an
    /// operation can hold — over 2 GB of one payload — and so has no undo.
    /// </summary>
    public static bool Undo(ref Log log, ref Source src)
    {
        if (log.At == 0 || log.Entries[log.At - 1].Op.Uninvertible)
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
