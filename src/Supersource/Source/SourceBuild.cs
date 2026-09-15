using System;

namespace Supersource;
/// <summary>
/// Mutable graph construction. Edits retain existing IDs; deletion clears liveness across a
/// containment subtree without rewriting incoming references.
/// </summary>
public static class SourceBuild
{
    /// <summary>
    /// Updates the source revision and stamp, plus cache revisions for the node and its ancestors.
    /// Costs O(containment depth).
    /// </summary>
    public static void Touch(ref Source src, NodeId id)
    {
        Stir(ref src, id.Index);

        // Bounded by the node count rather than trusting the parent links, because this
        // runs on every edit and a corrupt source must not spin here.
        int walked = 0;
        while (id.IsSome && id.Index < src.NodeCount && walked <= src.NodeCount)
        {
            src.NodeRevision[id.Index]++;
            id = src.Nodes[id.Index].Parent;
            walked++;
        }
    }

    /// <summary>Updates the source revision and mutation stamp for changes not attributed to one node.</summary>
    static void Stir(ref Source src, long what)
    {
        src.Revision++;
        src.Stamp = (src.Stamp ^ (ulong)(what + 2)) * 0x100000001B3UL;
    }

    /// <summary>Assigns a freshly minted source identity. Calling again replaces the previous identity.</summary>
    public static void MintIdentity(ref Source src)
    {
        // No node changed, so nothing to touch.
        Stir(ref src, -1);
        src.Identity = SourceId.Mint();
    }

    /// <summary>Allocates the next durable ID without attaching it to a parent.</summary>
    public static NodeId AddNode(ref Source src, KindId kind)
    {
        Source.EnsureNodeCapacity(ref src, src.NodeCount + 1);

        long index = src.NodeCount;
        src.NodeCount++;

        src.Nodes[index] = default;
        src.Capacities[index] = default;
        src.NodeRevision[index] = 0;
        src.Nodes[index].Kind = kind;
        src.Nodes[index].Parent = NodeId.None;
        src.Nodes[index].Flags = NodeFlags.Live;

        NodeId made = new NodeId(index);
        Touch(ref src, made);
        return made;
    }

    /// <summary>Allocate a node and append it as the last child of <paramref name="parent"/>.</summary>
    public static NodeId AddChild(ref Source src, NodeId parent, KindId kind)
    {
        NodeId child = AddNode(ref src, kind);
        AppendChild(ref src, parent, child);
        return child;
    }

    /// <summary>Allocates a parentless node and sets it as the source root.</summary>
    public static NodeId AddRoot(ref Source src, KindId kind)
    {
        NodeId root = AddNode(ref src, kind);
        src.Root = root;
        return root;
    }

    /// <summary>Marks a containment subtree deleted. Incoming references retain their target IDs.</summary>
    public static void Delete(ref Source src, NodeId id)
    {
        if (!src.IsLive(id))
        {
            Stir(ref src, -2);
            return;
        }

        // Before detaching, while this node still knows who contained it.
        Touch(ref src, id);
        Detach(ref src, id);
        SetSubtreeLive(ref src, id, false);
    }

    /// <summary>
    /// Restores liveness to a deleted subtree at its original IDs. The caller must reattach its
    /// root separately.
    /// </summary>
    public static void Revive(ref Source src, NodeId id)
    {
        if (id.IsNone || !src.WasAllocated(id))
        {
            Stir(ref src, -3);
            return;
        }

        Touch(ref src, id);
        SetSubtreeLive(ref src, id, true);
    }

    /// <summary>
    /// Changes subtree liveness iteratively to support deep containment. Retains child runs and
    /// descendant parent links for undo.
    /// </summary>
    static void SetSubtreeLive(ref Source src, NodeId root, bool live)
    {
        // Most subtrees are small, so the common case never touches the heap.
        Span<long> inline = stackalloc long[64];
        Span<long> work = inline;
        long[] spilled = null;
        int depth = 0;

        work[depth] = root.Index;
        depth++;

        while (depth > 0)
        {
            depth--;
            long at = work[depth];

            if (live)
            {
                src.Nodes[at].Flags |= NodeFlags.Live;
            }
            else
            {
                src.Nodes[at].Flags &= ~NodeFlags.Live;
            }

            int offset = src.Nodes[at].ChildOffset;
            int count = src.Nodes[at].ChildCount;

            if (depth + count > work.Length)
            {
                int size = work.Length;
                while (size < depth + count)
                {
                    size *= 2;
                }

                long[] grown = new long[size];
                work.Slice(0, depth).CopyTo(grown);
                spilled = grown;
                work = spilled;
            }

            for (int i = 0; i < count; i++)
            {
                work[depth] = src.Children[offset + i];
                depth++;
            }
        }
    }

    /// <summary>Appends a child. The child must not already have a parent.</summary>
    public static void AppendChild(ref Source src, NodeId parent, NodeId child)
    {
        InsertChild(ref src, parent, src.Nodes[parent.Index].ChildCount, child);
    }

    /// <summary>Inserts a child at the requested index, preserving sibling order.</summary>
    public static void InsertChild(ref Source src, NodeId parent, int index, NodeId child)
    {
        if (src.Nodes[child.Index].Parent.IsSome)
        {
            throw new InvalidOperationException(
                "node " + child + " already has parent " + src.Nodes[child.Index].Parent +
                "; one parent only (§3.2) — detach or move it instead");
        }

        if (parent == child)
        {
            throw new InvalidOperationException("a node cannot contain itself");
        }

        if (IsAncestorOf(ref src, child, parent))
        {
            throw new InvalidOperationException("attaching " + child + " under " + parent + " would make containment cyclic");
        }

        int count = src.Nodes[parent.Index].ChildCount;
        if (index < 0 || index > count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        GrowChildRun(ref src, parent, count + 1);

        int offset = src.Nodes[parent.Index].ChildOffset;
        for (int i = count; i > index; i--)
        {
            src.Children[offset + i] = src.Children[offset + i - 1];
        }

        src.Children[offset + index] = child.Index;
        src.Nodes[parent.Index].ChildCount = count + 1;
        src.Nodes[child.Index].Parent = parent;

        // From the child, so the walk covers it and every new ancestor in one pass.
        Touch(ref src, child);
    }

    /// <summary>Remove from its parent's child run, leaving it live and parentless.</summary>
    public static void Detach(ref Source src, NodeId child)
    {
        // From the child, before the link goes, so the old ancestors hear about it.
        Touch(ref src, child);

        // Deleted links are retained for undo; detaching a deleted node must not change them.
        if (!src.IsLive(child))
        {
            return;
        }

        NodeId parent = src.Nodes[child.Index].Parent;
        if (parent.IsNone)
        {
            return;
        }

        int offset = src.Nodes[parent.Index].ChildOffset;
        int count = src.Nodes[parent.Index].ChildCount;

        int at = -1;
        for (int i = 0; i < count; i++)
        {
            if (src.Children[offset + i] == child.Index)
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            // The parent link and the child run disagree. That is corruption, not a
            // representable situation, so it is worth failing loudly.
            throw new InvalidOperationException(
                "node " + child + " claims parent " + parent + " but is not among its children");
        }

        for (int i = at; i < count - 1; i++)
        {
            src.Children[offset + i] = src.Children[offset + i + 1];
        }

        src.Nodes[parent.Index].ChildCount = count - 1;
        src.Nodes[child.Index].Parent = NodeId.None;
    }

    /// <summary>
    /// Moves a child to a new parent. The destination index is measured after removing it from its
    /// current parent.
    /// </summary>
    public static void Move(ref Source src, NodeId child, NodeId newParent, int index)
    {
        if (!src.WasAllocated(child)) throw new ArgumentOutOfRangeException(nameof(child));
        if (!src.WasAllocated(newParent)) throw new ArgumentOutOfRangeException(nameof(newParent));
        if (IsAncestorOf(ref src, child, newParent))
        {
            throw new InvalidOperationException(
                "moving " + child + " under " + newParent + " would make containment cyclic");
        }

        // The index addresses the destination after removal. Validate before detaching:
        // a rejected operation must not leave an unrecorded structural edit behind.
        int count = src.Nodes[newParent.Index].ChildCount;
        if (src.Nodes[child.Index].Parent == newParent) count--;
        if (index < 0 || index > count) throw new ArgumentOutOfRangeException(nameof(index));

        Detach(ref src, child);
        InsertChild(ref src, newParent, index, child);
    }

    /// <summary>Moves a child within its parent’s ordered run.</summary>
    public static void ReorderChild(ref Source src, NodeId parent, int from, int to)
    {
        Touch(ref src, parent);
        int count = src.Nodes[parent.Index].ChildCount;
        if (from < 0 || from >= count || to < 0 || to >= count)
        {
            throw new ArgumentOutOfRangeException();
        }
        if (from == to)
        {
            return;
        }

        int offset = src.Nodes[parent.Index].ChildOffset;
        long moved = src.Children[offset + from];

        if (from < to)
        {
            for (int i = from; i < to; i++)
            {
                src.Children[offset + i] = src.Children[offset + i + 1];
            }
        }
        else
        {
            for (int i = from; i > to; i--)
            {
                src.Children[offset + i] = src.Children[offset + i - 1];
            }
        }

        src.Children[offset + to] = moved;
    }

    static bool IsAncestorOf(ref Source src, NodeId maybeAncestor, NodeId node)
    {
        NodeId walk = node;
        while (walk.IsSome)
        {
            if (walk == maybeAncestor)
            {
                return true;
            }
            walk = src.Nodes[walk.Index].Parent;
        }
        return false;
    }

    /// <summary>Adds a reference to a target node.</summary>
    public static int AddRef(ref Source src, NodeId from, RelationId relation, NodeId target)
    {
        Touch(ref src, from);
        return PushRef(ref src, from, Ref.Bound(relation, target));
    }

    /// <summary>Adds a bound reference with edge data interpreted according to the relation’s schema.</summary>
    public static int AddRef(
        ref Source src,
        NodeId from,
        RelationId relation,
        NodeId target,
        ReadOnlySpan<byte> data)
    {
        Touch(ref src, from);

        Ref edge = Ref.Bound(relation, target);
        if (data.Length > 0)
        {
            edge.LabelOffset = Source.ReserveLabel(ref src, data.Length);
            edge.LabelLength = data.Length;
            data.CopyTo(new Span<byte>(src.Labels, edge.LabelOffset, data.Length));
        }

        return PushRef(ref src, from, edge);
    }

    /// <summary>Adds an unbound reference carrying its unresolved label.</summary>
    public static int AddUnboundRef(ref Source src, NodeId from, RelationId relation, ReadOnlySpan<byte> label)
    {
        Touch(ref src, from);
        int offset = 0;
        int length = label.Length;
        if (length > 0)
        {
            offset = Source.ReserveLabel(ref src, length);
            label.CopyTo(new Span<byte>(src.Labels, offset, length));
        }
        return PushRef(ref src, from, Ref.Unbound(relation, offset, length));
    }

    /// <summary>Bind a reference that was a hole. The label is dropped; the edge now names a node.</summary>
    public static void BindRef(ref Source src, NodeId from, int refIndex, NodeId target)
    {
        Touch(ref src, from);
        int slot = RefSlot(ref src, from, refIndex);
        src.Refs[slot].Target = target;
        src.Refs[slot].LabelOffset = 0;
        src.Refs[slot].LabelLength = 0;
    }

    /// <summary>Clears the target and stores an unresolved label. Deletion does not call this automatically.</summary>
    public static void UnbindRef(ref Source src, NodeId from, int refIndex, ReadOnlySpan<byte> label)
    {
        Touch(ref src, from);
        int offset = 0;
        int length = label.Length;
        if (length > 0)
        {
            offset = Source.ReserveLabel(ref src, length);
            label.CopyTo(new Span<byte>(src.Labels, offset, length));
        }

        int slot = RefSlot(ref src, from, refIndex);
        src.Refs[slot].Target = NodeId.None;
        src.Refs[slot].LabelOffset = offset;
        src.Refs[slot].LabelLength = length;
    }

    /// <summary>
    /// Inserts a reference at its original position for undo. Indices beyond the end append.
    /// Preserving order matters because later operations address references by index.
    /// </summary>
    public static int InsertRef(ref Source src, NodeId from, int refIndex, Ref r)
    {
        Touch(ref src, from);
        int count = src.Nodes[from.Index].RefCount;
        if (refIndex < 0 || refIndex > count)
        {
            refIndex = count;
        }

        GrowRefRun(ref src, from, count + 1);

        int offset = src.Nodes[from.Index].RefOffset;
        for (int i = count; i > refIndex; i--)
        {
            src.Refs[offset + i] = src.Refs[offset + i - 1];
        }

        src.Refs[offset + refIndex] = r;
        src.Nodes[from.Index].RefCount = count + 1;
        return refIndex;
    }

    /// <summary>
    /// Creates an edge with its data copied into this source’s label arena. Arena offsets cannot be
    /// transferred between sources.
    /// </summary>
    public static Ref EdgeFor(
        ref Source src,
        RelationId relation,
        NodeId target,
        ReadOnlySpan<byte> data)
    {
        Stir(ref src, -5);

        Ref edge = target.IsSome ? Ref.Bound(relation, target) : Ref.Unbound(relation, 0, 0);
        if (data.Length > 0)
        {
            edge.LabelOffset = Source.ReserveLabel(ref src, data.Length);
            edge.LabelLength = data.Length;
            data.CopyTo(new Span<byte>(src.Labels, edge.LabelOffset, data.Length));
        }
        return edge;
    }

    public static Ref UnboundRefFor(ref Source src, RelationId relation, ReadOnlySpan<byte> label)
    {
        // No node owns this edge yet, but allocating its data changes the source stamp.
        Stir(ref src, -4);
        int offset = 0;
        int length = label.Length;
        if (length > 0)
        {
            offset = Source.ReserveLabel(ref src, length);
            label.CopyTo(new Span<byte>(src.Labels, offset, length));
        }
        return Ref.Unbound(relation, offset, length);
    }

    public static void RemoveRef(ref Source src, NodeId from, int refIndex)
    {
        Touch(ref src, from);
        int count = src.Nodes[from.Index].RefCount;
        if (refIndex < 0 || refIndex >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(refIndex));
        }

        int offset = src.Nodes[from.Index].RefOffset;
        for (int i = refIndex; i < count - 1; i++)
        {
            src.Refs[offset + i] = src.Refs[offset + i + 1];
        }
        src.Nodes[from.Index].RefCount = count - 1;
    }

    static int PushRef(ref Source src, NodeId from, Ref r)
    {
        int count = src.Nodes[from.Index].RefCount;
        GrowRefRun(ref src, from, count + 1);

        int offset = src.Nodes[from.Index].RefOffset;
        src.Refs[offset + count] = r;
        src.Nodes[from.Index].RefCount = count + 1;
        return count;
    }

    static int RefSlot(ref Source src, NodeId from, int refIndex)
    {
        int count = src.Nodes[from.Index].RefCount;
        if (refIndex < 0 || refIndex >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(refIndex));
        }
        return src.Nodes[from.Index].RefOffset + refIndex;
    }

    /// <summary>Replaces a node’s payload bytes without interpreting their format.</summary>
    public static void SetPayload(ref Source src, NodeId id, ReadOnlySpan<byte> payload)
    {
        Touch(ref src, id);
        int length = payload.Length;
        if (length == 0)
        {
            src.Nodes[id.Index].PayloadLength = 0;
            return;
        }

        if (src.Capacities[id.Index].Payload < length)
        {
            int capacity = src.Capacities[id.Index].Payload < 8 ? 8 : src.Capacities[id.Index].Payload;
            while (capacity < length)
            {
                capacity *= 2;
            }
            src.Nodes[id.Index].PayloadOffset = Source.ReserveBlob(ref src, capacity);
            src.Capacities[id.Index].Payload = capacity;
        }

        long at = src.Nodes[id.Index].PayloadOffset;
        payload.CopyTo(Owned(ref src, at, length));
        src.Nodes[id.Index].PayloadLength = length;
    }

    /// <summary>
    /// Replaces a byte range and copies the removed bytes into removed for undo. Offsets and counts
    /// are clamped to the payload. Reuses owned capacity when possible; mapped payloads are copied
    /// before editing.
    /// </summary>
    public static void Splice(
        ref Source src,
        NodeId id,
        int at,
        int removing,
        ReadOnlySpan<byte> inserting,
        Span<byte> removed,
        out int removedLength)
    {
        Touch(ref src, id);

        int length = src.Nodes[id.Index].PayloadLength;

        if (at < 0)
        {
            at = 0;
        }
        if (at > length)
        {
            at = length;
        }
        if (removing < 0)
        {
            removing = 0;
        }
        if (at + removing > length)
        {
            removing = length - at;
        }

        long address = src.Nodes[id.Index].PayloadOffset;

        removedLength = removing;
        if (removing > 0 && removed.Length >= removing)
        {
            Source.Bytes(in src, address, length).Slice(at, removing).CopyTo(removed);
        }

        int wanted = length - removing + inserting.Length;

        // Mapped payloads are read-only and have no owned capacity, so editing them takes the copy
        // path.
        if (wanted <= src.Capacities[id.Index].Payload
            && !src.Blob[Source.SegmentOf(address)].IsMapped)
        {
            byte[] segment = src.Blob[Source.SegmentOf(address)].Array;
            int offset = (int)Source.OffsetOf(address);

            // The tail moves by the difference, in place. One memmove and one copy, both
            // inside the one segment this payload lives in.
            int tail = length - at - removing;
            if (tail > 0)
            {
                Array.Copy(
                    segment, offset + at + removing,
                    segment, offset + at + inserting.Length,
                    tail);
            }

            inserting.CopyTo(new Span<byte>(segment, offset + at, inserting.Length));
            src.Nodes[id.Index].PayloadLength = wanted;
            return;
        }

        // Outgrown. Relocate, the same way a child or reference run does, and assemble
        // straight into the new home rather than into a temporary.
        int capacity = src.Capacities[id.Index].Payload < 8 ? 8 : src.Capacities[id.Index].Payload;
        while (capacity < wanted)
        {
            capacity *= 2;
        }

        long fresh = Source.ReserveBlob(ref src, capacity);

        // Read the old bytes through the accessor: they may be a file's, and reserving may
        // have opened a new segment either way.
        ReadOnlySpan<byte> old = Source.Bytes(in src, address, length);
        Span<byte> into = Owned(ref src, fresh, wanted);

        old.Slice(0, at).CopyTo(into);
        inserting.CopyTo(into.Slice(at, inserting.Length));
        old.Slice(at + removing).CopyTo(into.Slice(at + inserting.Length));

        src.Nodes[id.Index].PayloadOffset = fresh;
        src.Capacities[id.Index].Payload = capacity;
        src.Nodes[id.Index].PayloadLength = wanted;
    }

    /// <summary>
    /// Returns writable bytes in an owned segment. Call only with an address reserved from owned
    /// storage.
    /// </summary>
    static Span<byte> Owned(ref Source src, long address, int length)
    {
        ref BlobSegment segment = ref src.Blob[Source.SegmentOf(address)];
        if (segment.IsMapped)
        {
            throw new InvalidOperationException(
                "a mapped payload is read-only; it has to be relocated before it is written");
        }

        return new Span<byte>(segment.Array, (int)Source.OffsetOf(address), length);
    }

    // Relocate runs to the arena tail. Do not retain a ref Node across reserve calls that may
    // resize arrays.

    static void GrowChildRun(ref Source src, NodeId id, int needed)
    {
        if (src.Capacities[id.Index].Child >= needed)
        {
            return;
        }

        int capacity = src.Capacities[id.Index].Child < 2 ? 2 : src.Capacities[id.Index].Child;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        int fresh = Source.ReserveChildren(ref src, capacity);
        int old = src.Nodes[id.Index].ChildOffset;
        int count = src.Nodes[id.Index].ChildCount;

        if (count > 0)
        {
            Array.Copy(src.Children, old, src.Children, fresh, count);
        }

        src.Nodes[id.Index].ChildOffset = fresh;
        src.Capacities[id.Index].Child = capacity;
    }

    static void GrowRefRun(ref Source src, NodeId id, int needed)
    {
        if (src.Capacities[id.Index].Ref >= needed)
        {
            return;
        }

        int capacity = src.Capacities[id.Index].Ref < 2 ? 2 : src.Capacities[id.Index].Ref;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        int fresh = Source.ReserveRefs(ref src, capacity);
        int old = src.Nodes[id.Index].RefOffset;
        int count = src.Nodes[id.Index].RefCount;

        if (count > 0)
        {
            Array.Copy(src.Refs, old, src.Refs, fresh, count);
        }

        src.Nodes[id.Index].RefOffset = fresh;
        src.Capacities[id.Index].Ref = capacity;
    }
}
