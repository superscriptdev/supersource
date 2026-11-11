using System;

namespace Supersource;
/// <summary>
/// Mutable graph construction. Edits retain existing IDs. Deletion removes a containment subtree
/// outright and leaves incoming references dangling; its ids are never handed out to anything
/// else.
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

    /// <summary>
    /// Allocates a node at a given id: one this source handed out before and no longer holds, or
    /// one past its next id, which moves the next id beyond it. Used to re-create a node where it
    /// was — replaying a journal, redoing an insert, applying a diff. An id that names a live
    /// node allocates the next id instead, so a caller comparing ids sees the disagreement.
    /// </summary>
    public static NodeId AddNodeAt(ref Source src, NodeId id, KindId kind)
    {
        if (id.IsNone || src.IsLive(id))
        {
            return AddNode(ref src, kind);
        }

        long index = id.Index;
        if (index >= src.NodeCount)
        {
            Source.EnsureNodeCapacity(ref src, index + 1);
            for (long gap = src.NodeCount; gap < index; gap++)
            {
                src.Nodes[gap] = default;
                src.Nodes[gap].Parent = NodeId.None;
                src.Capacities[gap] = default;
                src.NodeRevision[gap] = 0;
            }
            src.NodeRevision[index] = 0;
            src.NodeCount = index + 1;
        }

        src.Nodes[index] = default;
        src.Capacities[index] = default;
        src.Nodes[index].Kind = kind;
        src.Nodes[index].Parent = NodeId.None;
        src.Nodes[index].Flags = NodeFlags.Live;

        Touch(ref src, id);
        return id;
    }

    /// <summary>Changes a node's kind. Its id, payload, children and references are untouched.</summary>
    public static void SetKind(ref Source src, NodeId id, KindId kind)
    {
        if (!src.IsLive(id))
        {
            Stir(ref src, -6);
            return;
        }

        Touch(ref src, id);
        src.Nodes[id.Index].Kind = kind;
    }

    /// <summary>
    /// Removes a containment subtree. Its nodes, payloads, children and references are gone;
    /// references pointing in from outside are left as they are, and so now dangle. The ids stay
    /// taken. Capture first with <see cref="Capture"/> if the deletion may be taken back.
    /// </summary>
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

        long[] ids = null;
        int count = Subtree(in src, id, ref ids);
        for (int i = 0; i < count; i++)
        {
            long at = ids[i];
            src.Nodes[at] = default;
            src.Nodes[at].Parent = NodeId.None;
            src.Capacities[at] = default;
            src.NodeRevision[at]++;
        }
    }

    /// <summary>Everything a deletion of this subtree would remove, so it can be restored later.</summary>
    public static Removed Capture(in Source src, NodeId root)
    {
        long[] ids = null;
        int count = Subtree(in src, root, ref ids);

        Removed removed = new Removed();
        removed.Nodes = new RemovedNode[count];
        for (int i = 0; i < count; i++)
        {
            NodeId node = new NodeId(ids[i]);
            ref RemovedNode into = ref removed.Nodes[i];
            into.Id = node;
            into.Kind = src.Nodes[ids[i]].Kind;
            if (SourceWalk.PayloadLength(in src, node) > Array.MaxLength)
            {
                removed.Whole = false;
            }
            else
            {
                into.Payload = SourceWalk.Payload(in src, node).ToArray();
            }

            ReadOnlySpan<long> children = SourceWalk.Children(in src, node);
            into.Children = new NodeId[children.Length];
            for (int c = 0; c < children.Length; c++)
            {
                into.Children[c] = new NodeId(children[c]);
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
            into.Refs = new RemovedRef[refs.Length];
            for (int r = 0; r < refs.Length; r++)
            {
                into.Refs[r].Relation = refs[r].Relation;
                into.Refs[r].Target = refs[r].Target;
                into.Refs[r].Data = SourceWalk.EdgeData(in src, in refs[r]).ToArray();
            }
        }
        return removed;
    }

    /// <summary>
    /// Puts a captured subtree back under its original ids, detached. The caller attaches its
    /// root. Every id it names must be free in this source.
    /// </summary>
    public static NodeId Restore(ref Source src, Removed removed)
    {
        for (int i = 0; i < removed.Nodes.Length; i++)
        {
            ref RemovedNode node = ref removed.Nodes[i];
            AddNodeAt(ref src, node.Id, node.Kind);
            if (node.Payload.Length > 0)
            {
                SetPayload(ref src, node.Id, node.Payload);
            }
            for (int r = 0; r < node.Refs.Length; r++)
            {
                ref RemovedRef edge = ref node.Refs[r];
                if (edge.Target.IsSome)
                {
                    AddRef(ref src, node.Id, edge.Relation, edge.Target, edge.Data);
                }
                else
                {
                    AddUnboundRef(ref src, node.Id, edge.Relation, edge.Data);
                }
            }
        }

        // Containment after every node exists. Written directly rather than child by child: the
        // capture is a tree already, so there is no cycle to check, and touching each child's
        // ancestors as it is appended would make a deep subtree quadratic.
        for (int i = 0; i < removed.Nodes.Length; i++)
        {
            ref RemovedNode node = ref removed.Nodes[i];
            int count = node.Children.Length;
            if (count == 0)
            {
                continue;
            }

            GrowChildRun(ref src, node.Id, count);
            int offset = src.Nodes[node.Id.Index].ChildOffset;
            for (int c = 0; c < count; c++)
            {
                src.Children[offset + c] = node.Children[c].Index;
                src.Nodes[node.Children[c].Index].Parent = node.Id;
            }
            src.Nodes[node.Id.Index].ChildCount = count;
        }

        return removed.Nodes.Length > 0 ? removed.Nodes[0].Id : NodeId.None;
    }

    /// <summary>The live subtree under a root, root first, depth first. Iterative for deep containment.</summary>
    static int Subtree(in Source src, NodeId root, ref long[] into)
    {
        into ??= new long[16];
        int found = 0;

        // Most subtrees are small, so the common case never touches the heap.
        Span<long> inline = stackalloc long[64];
        Span<long> work = inline;
        int depth = 0;

        work[depth] = root.Index;
        depth++;

        while (depth > 0)
        {
            depth--;
            long at = work[depth];

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length * 2);
            }
            into[found] = at;
            found++;

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
                work = grown;
            }

            // Pushed in reverse so they come off the stack in order.
            for (int i = count - 1; i >= 0; i--)
            {
                work[depth] = src.Children[offset + i];
                depth++;
            }
        }

        return found;
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

        // A deleted node has no parent to leave.
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
            edge.DataOffset = Source.ReserveEdgeData(ref src, data.Length);
            edge.DataLength = data.Length;
            data.CopyTo(new Span<byte>(src.EdgeData, edge.DataOffset, data.Length));
        }

        return PushRef(ref src, from, edge);
    }

    /// <summary>Adds an unbound reference carrying its label.</summary>
    public static int AddUnboundRef(ref Source src, NodeId from, RelationId relation, ReadOnlySpan<byte> label)
    {
        Touch(ref src, from);
        int offset = 0;
        int length = label.Length;
        if (length > 0)
        {
            offset = Source.ReserveEdgeData(ref src, length);
            label.CopyTo(new Span<byte>(src.EdgeData, offset, length));
        }
        return PushRef(ref src, from, Ref.Unbound(relation, offset, length));
    }

    /// <summary>Points a reference at a node, dropping its edge data: an unbound one's label goes.</summary>
    public static void BindRef(ref Source src, NodeId from, int refIndex, NodeId target)
    {
        Touch(ref src, from);
        int slot = RefSlot(ref src, from, refIndex);
        src.Refs[slot].Target = target;
        src.Refs[slot].DataOffset = 0;
        src.Refs[slot].DataLength = 0;
    }

    /// <summary>Clears the target and stores a label. Deletion never unbinds the references into what it removed.</summary>
    public static void UnbindRef(ref Source src, NodeId from, int refIndex, ReadOnlySpan<byte> label)
    {
        Touch(ref src, from);
        int offset = 0;
        int length = label.Length;
        if (length > 0)
        {
            offset = Source.ReserveEdgeData(ref src, length);
            label.CopyTo(new Span<byte>(src.EdgeData, offset, length));
        }

        int slot = RefSlot(ref src, from, refIndex);
        src.Refs[slot].Target = NodeId.None;
        src.Refs[slot].DataOffset = offset;
        src.Refs[slot].DataLength = length;
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
    /// Creates an edge with its data copied into this source’s edge-data arena. Arena offsets cannot
    /// be transferred between sources.
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
            edge.DataOffset = Source.ReserveEdgeData(ref src, data.Length);
            edge.DataLength = data.Length;
            data.CopyTo(new Span<byte>(src.EdgeData, edge.DataOffset, data.Length));
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
            offset = Source.ReserveEdgeData(ref src, length);
            label.CopyTo(new Span<byte>(src.EdgeData, offset, length));
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
        long at = Room(ref src, id, payload.Length);
        if (payload.Length > 0) Source.Write(ref src, at, 0, payload);
        src.Nodes[id.Index].PayloadLength = payload.Length;
    }

    /// <summary>
    /// Replaces a node’s payload with <paramref name="length"/> bytes read from a stream: how a payload
    /// larger than one span is written. Reads in windows, and fails when the stream ends first.
    /// </summary>
    public static void SetPayload(ref Source src, NodeId id, System.IO.Stream from, long length)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Touch(ref src, id);
        long at = Room(ref src, id, length);
        var window = new byte[(int)Math.Min(Math.Max(length, 1), Window)];
        long done = 0;
        while (done < length)
        {
            int got = from.Read(window, 0, (int)Math.Min(window.Length, length - done));
            if (got <= 0)
                throw new System.IO.EndOfStreamException("the stream ended " + (length - done) + " bytes before the payload did");
            Source.Write(ref src, at, done, new ReadOnlySpan<byte>(window, 0, got));
            done += got;
        }
        src.Nodes[id.Index].PayloadLength = length;
    }

    /// <summary>The largest piece a payload is copied in.</summary>
    const int Window = 1 << 20;

    /// <summary>
    /// Owned room for a payload of <paramref name="length"/> bytes: the node’s own when it has that
    /// much and it is not a mapping’s, otherwise new.
    /// </summary>
    static long Room(ref Source src, NodeId id, long length)
    {
        long address = src.Nodes[id.Index].PayloadOffset;
        if (length == 0) return address;
        if (src.Capacities[id.Index].Payload >= length && !src.Blob[Source.SegmentOf(address)].IsMapped) return address;
        long capacity = Grown(src.Capacities[id.Index].Payload, length, src.ChunkLimit);
        address = Source.ReserveBlob(ref src, capacity);
        src.Nodes[id.Index].PayloadOffset = address;
        src.Capacities[id.Index].Payload = capacity;
        return address;
    }

    /// <summary>
    /// A payload’s new capacity: doubling, as runs grow, up to one chunk; past one chunk exactly
    /// what it needs, since it then has a segment of its own.
    /// </summary>
    static long Grown(long capacity, long wanted, int chunk)
    {
        if (wanted > chunk) return wanted;
        long grown = capacity < 8 ? 8 : capacity;
        while (grown < wanted) grown *= 2;
        return Math.Min(grown, chunk);
    }

    /// <summary>
    /// Replaces a byte range and copies the removed bytes into removed for undo. Offsets and counts
    /// are clamped to the payload and are 64-bit, so any payload can be spliced. Reuses owned
    /// capacity when possible; mapped and chunked payloads are assembled afresh in windows.
    /// </summary>
    public static void Splice(
        ref Source src,
        NodeId id,
        long at,
        long removing,
        ReadOnlySpan<byte> inserting,
        Span<byte> removed,
        out long removedLength)
    {
        Touch(ref src, id);

        long length = src.Nodes[id.Index].PayloadLength;
        at = Math.Clamp(at, 0, length);
        removing = Math.Clamp(removing, 0, length - at);

        long address = src.Nodes[id.Index].PayloadOffset;

        removedLength = removing;
        if (removing > 0 && removed.Length >= removing)
        {
            Source.Read(in src, address, at, removed.Slice(0, (int)removing));
        }

        long wanted = length - removing + inserting.Length;

        // In place when the payload is an owned array with room: one memmove and one copy.
        byte[] array = src.Blob[Source.SegmentOf(address)].Array;
        if (wanted <= src.Capacities[id.Index].Payload && array != null)
        {
            long offset = Source.OffsetOf(address);
            long tail = length - at - removing;
            if (tail > 0)
            {
                Array.Copy(array, offset + at + removing, array, offset + at + inserting.Length, tail);
            }
            inserting.CopyTo(new Span<byte>(array, (int)(offset + at), inserting.Length));
            src.Nodes[id.Index].PayloadLength = wanted;
            return;
        }

        // Otherwise a new home, assembled in windows: what comes before, what is inserted, what
        // follows. Read through the accessors, since the old bytes may be a file’s.
        long capacity = Grown(src.Capacities[id.Index].Payload, wanted, src.ChunkLimit);
        long fresh = Source.ReserveBlob(ref src, capacity);
        Copy(ref src, address, 0, fresh, 0, at);
        if (inserting.Length > 0) Source.Write(ref src, fresh, at, inserting);
        Copy(ref src, address, at + removing, fresh, at + inserting.Length, length - at - removing);

        src.Nodes[id.Index].PayloadOffset = fresh;
        src.Capacities[id.Index].Payload = capacity;
        src.Nodes[id.Index].PayloadLength = wanted;
    }

    /// <summary>Copy payload bytes from one home to another in windows, so neither need fit a span.</summary>
    static void Copy(ref Source src, long from, long fromOffset, long to, long toOffset, long count)
    {
        if (count <= 0) return;
        var window = new byte[(int)Math.Min(count, Window)];
        long done = 0;
        while (done < count)
        {
            int n = (int)Math.Min(window.Length, count - done);
            Source.Read(in src, from, fromOffset + done, window.AsSpan(0, n));
            Source.Write(ref src, to, toOffset + done, window.AsSpan(0, n));
            done += n;
        }
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
