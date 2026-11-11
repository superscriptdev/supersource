using System;
using System.Security.Cryptography;
using Supersource.Timeline;

namespace Supersource.Format;
/// <summary>
/// Encodes a journal: the operations applied since the previous save, appended to the file
/// instead of rewriting it, and replayed onto the saved graph when the file is read.
///
/// A journal records operations forward. Applying an operation captures what it replaced and
/// replay captures it again, so nothing an edit replaced or removed is written. An insert records
/// the id it took, so replay re-creates the node at that id.
///
/// <code>
/// u8   version = 2
/// uvar count
/// then count times:
///   u8   kind        an OpKind wire value
///   svar node
///   svar parent · index · count
///   svar nodeKind    slot into the file's kind table; Insert, Wrap and Retype, else -1
///   svar relation    slot into the file's relation table; Connect, else -1
///   svar target      Connect, else -1
///   uvar valueLength then that many bytes; Insert, ModifyValue, Splice and Connect, else 0
/// </code>
///
/// Version 1 is still read. Its entries also carry an actor, an order, node flags and the replaced
/// data; replay ignores all four, and reads its wire value 3 as ModifyValue.
/// </summary>
public static class JournalCodec
{
    /// <summary>Journal entry encoding version. Change when an existing field’s layout changes.</summary>
    public const byte Version = 2;

    /// <summary>Size of the SHA-256 a journal section carries, last, of its own entries.</summary>
    public const int DigestBytes = 32;

    /// <summary>A version 2 entry at its smallest: every number one byte, no value.</summary>
    const int SmallestEntry = 1 + 1 + 3 + 1 + 1 + 1 + 1;

    /// <summary>A version 1 entry at its smallest.</summary>
    const int SmallestEntryV1 = 1 + 1 + 1 + 1 + (1 * 9) + 1 + 1;

    /// <summary>The wire value Rename had in version 1. It replaced a payload exactly as ModifyValue does.</summary>
    const byte RetiredRename = 3;

    /// <summary>The forward operations a journal of this log range records, in order.</summary>
    public static int Collect(in Log log, int from, int to, out Operation[] ops)
    {
        ops = new Operation[to - from < 4 ? 4 : to - from];
        int count = 0;
        for (int i = from; i < to; i++)
        {
            count = Forward(in log.Entries[i].Op, ref ops, count);
        }
        return count;
    }

    /// <summary>Writes operations as a journal, in order, and appends a digest of its content.</summary>
    public static void Write(
        ref ByteWriter w,
        Operation[] ops,
        int count,
        int[] kindSlot,
        int[] relationSlot)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        w.Hash = digest;
        w.Hashing = true;

        w.U8(Version);
        w.UVar((ulong)count);
        for (int i = 0; i < count; i++)
        {
            WriteEntry(ref w, in ops[i], kindSlot, relationSlot);
        }

        w.Flush();
        w.Hashing = false;
        w.Bytes(digest.GetCurrentHash());
    }

    /// <summary>
    /// The operations a journal records for one logged operation. Usually that one. An insert or a
    /// wrap that puts back a captured subtree — an undone delete or unwrap applied as a new edit —
    /// is spelled as the inserts, payloads and connections that rebuild it, because the capture
    /// itself is never written.
    /// </summary>
    public static int Forward(in Operation op, ref Operation[] into, int count)
    {
        if (op.Removed == null || (op.Kind != OpKind.Insert && op.Kind != OpKind.Wrap))
        {
            return Push(ref into, count, op);
        }

        RemovedNode[] nodes = op.Removed.Nodes;
        Operation head = op;
        head.Removed = null;
        head.Node = nodes[0].Id;
        head.NodeKind = nodes[0].Kind;
        head.Value = op.Kind == OpKind.Insert ? nodes[0].Payload : null;
        count = Push(ref into, count, head);

        if (op.Kind == OpKind.Wrap && nodes[0].Payload.Length > 0)
        {
            Operation value = default;
            value.Kind = OpKind.ModifyValue;
            value.Node = nodes[0].Id;
            value.Value = nodes[0].Payload;
            count = Push(ref into, count, value);
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            for (int c = 0; c < nodes[i].Children.Length; c++)
            {
                NodeId child = nodes[i].Children[c];
                RemovedNode captured = Find(nodes, child);

                Operation insert = default;
                insert.Kind = OpKind.Insert;
                insert.Node = child;
                insert.Parent = nodes[i].Id;
                insert.Index = c;
                insert.NodeKind = captured.Kind;
                insert.Value = captured.Payload;
                count = Push(ref into, count, insert);
            }
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            for (int r = 0; r < nodes[i].Refs.Length; r++)
            {
                Operation connect = default;
                connect.Kind = OpKind.Connect;
                connect.Node = nodes[i].Id;
                connect.Index = r;
                connect.Relation = nodes[i].Refs[r].Relation;
                connect.Target = nodes[i].Refs[r].Target;
                connect.Value = nodes[i].Refs[r].Data;
                count = Push(ref into, count, connect);
            }
        }

        return count;
    }

    static RemovedNode Find(RemovedNode[] nodes, NodeId id)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Id == id)
            {
                return nodes[i];
            }
        }
        return default;
    }

    static int Push(ref Operation[] into, int count, in Operation op)
    {
        if (count == into.Length)
        {
            Array.Resize(ref into, into.Length * 2);
        }
        into[count] = op;
        return count + 1;
    }

    /// <summary>Whether a journal entry for this operation names a kind, and so needs it in the file's table.</summary>
    public static bool NamesKind(OpKind kind)
    {
        return kind == OpKind.Insert || kind == OpKind.Wrap || kind == OpKind.Retype;
    }

    /// <summary>Whether a journal entry for this operation names a relation.</summary>
    public static bool NamesRelation(OpKind kind)
    {
        return kind == OpKind.Connect;
    }

    static bool CarriesValue(OpKind kind)
    {
        return kind == OpKind.Insert || kind == OpKind.ModifyValue
            || kind == OpKind.Splice || kind == OpKind.Connect;
    }

    /// <summary>
    /// Applies a journal's operations to the saved graph. They are folded into the state rather
    /// than recorded: what a session can undo starts at the file as it was opened. Checks that
    /// each replayed insert lands on the id the journal recorded.
    /// </summary>
    public static bool Replay(ByteReader r, ref Source src, KindId[] kinds, RelationId[] relations)
    {
        byte version = r.U8();
        if (version != Version && version != 1)
        {
            return false;
        }

        int count = r.UVarCount(version == 1 ? SmallestEntryV1 : SmallestEntry, "journal section");
        for (int i = 0; i < count; i++)
        {
            Operation op = version == 1
                ? ReadOperationV1(ref r, kinds, relations)
                : ReadOperation(ref r, kinds, relations);

            NodeId recorded = op.Node;
            OpApply.Apply(ref src, ref op);

            if (op.Kind == OpKind.Insert && recorded.IsSome && op.Node != recorded)
            {
                throw new FormatCorruptException(
                    "a journal entry inserted " + op.Node + " where the file says " + recorded +
                    " — the operations before it do not account for every id");
            }
        }

        return true;
    }

    static void WriteEntry(ref ByteWriter w, in Operation op, int[] kindSlot, int[] relationSlot)
    {
        w.U8((byte)op.Kind);
        w.SVar(op.Node.Index);
        w.SVar(op.Parent.Index);
        w.SVar(op.Index);
        w.SVar(op.Count);
        w.SVar(NamesKind(op.Kind) ? Map(kindSlot, op.NodeKind.Value) : -1);
        w.SVar(NamesRelation(op.Kind) ? Map(relationSlot, op.Relation.Value) : -1);
        w.SVar(op.Kind == OpKind.Connect ? op.Target.Index : NodeId.None.Index);
        WriteBytes(ref w, CarriesValue(op.Kind) ? op.Value : null);
    }

    static Operation ReadOperation(ref ByteReader r, KindId[] kinds, RelationId[] relations)
    {
        Operation op = default;
        op.Kind = (OpKind)r.U8();
        op.Node = new NodeId(r.SVarInt());
        op.Parent = new NodeId(r.SVarInt());
        op.Index = r.SVar();
        op.Count = r.SVar();
        op.NodeKind = Unmap(kinds, r.SVarInt());
        op.Relation = Unmap(relations, r.SVarInt());
        op.Target = new NodeId(r.SVarInt());
        op.Value = ReadBytes(ref r);
        return op;
    }

    static Operation ReadOperationV1(ref ByteReader r, KindId[] kinds, RelationId[] relations)
    {
        Operation op = default;

        r.SVarInt();
        r.UVar();

        byte kind = r.U8();
        op.Kind = kind == RetiredRename ? OpKind.ModifyValue : (OpKind)kind;
        r.U8();
        op.Node = new NodeId(r.SVarInt());
        op.Parent = new NodeId(r.SVarInt());
        op.Index = r.SVar();
        r.SVarInt();
        r.SVarInt();
        op.Count = r.SVar();

        op.NodeKind = Unmap(kinds, r.SVarInt());
        op.Relation = Unmap(relations, r.SVarInt());
        op.Target = new NodeId(r.SVarInt());

        op.Value = ReadBytes(ref r);
        ReadBytes(ref r);

        return op;
    }

    static void WriteBytes(ref ByteWriter w, byte[] value)
    {
        // Null and empty operation data share the same zero-length encoding.
        int length = value == null ? 0 : value.Length;
        w.UVar((ulong)length);
        if (length > 0)
        {
            w.Bytes(value);
        }
    }

    static byte[] ReadBytes(ref ByteReader r)
    {
        int length = r.UVarCount(1, "journal entry payload");
        return length == 0 ? Array.Empty<byte>() : r.BytesCopy(length);
    }

    static int Map(int[] slots, int handle)
    {
        return handle >= 0 && handle < slots.Length ? slots[handle] : -1;
    }

    static KindId Unmap(KindId[] kinds, int slot)
    {
        return slot >= 0 && slot < kinds.Length ? kinds[slot] : KindId.Unknown;
    }

    static RelationId Unmap(RelationId[] relations, int slot)
    {
        return slot >= 0 && slot < relations.Length ? relations[slot] : RelationId.Unknown;
    }
}
