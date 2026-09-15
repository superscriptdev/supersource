using System;
using System.Security.Cryptography;
using Supersource.Timeline;

namespace Supersource.Format;
/// <summary>
/// Encodes the applied history prefix, including generated-node operations. Undo suffixes and view
/// state are not serialized. Unknown history versions are preserved without decoding.
///
/// <code>
/// u8   version = 1
/// uvar count
/// then count times:
///   svar actor
///   uvar order
///   u8   kind
///   u8   nodeFlags   Derived · Generated, for the node an Insert creates
///   svar node · parent · index · oldParent · oldIndex · count
///   svar nodeKind    slot into the file's kind table, or -1
///   svar relation    slot into the file's relation table, or -1
///   svar target
///   uvar valueLength    then that many bytes
///   uvar oldValueLength then that many bytes
/// </code>
/// </summary>
public static class HistoryCodec
{
    /// <summary>An entry at its smallest: every number one byte, both payloads empty.</summary>
    const int SmallestEntry = 1 + 1 + 1 + 1 + (1 * 9) + 1 + 1;

    /// <summary>History entry encoding version. Change when an existing field’s layout changes.</summary>
    public const byte Version = 1;

    public static void Write(ref ByteWriter w, in SourceFile file, int[] kindSlot, int[] relationSlot)
    {
        w.U8(Version);
        w.UVar((ulong)file.History.At);

        for (int i = 0; i < file.History.At; i++)
        {
            WriteEntry(ref w, in file.History.Entries[i], kindSlot, relationSlot);
        }
    }

    /// <summary>
    /// Writes a contiguous log range as a journal, including generated-node operations. Retains
    /// allocation order for replay and appends a digest of this journal’s content.
    /// </summary>
    public static void WriteJournal(
        ref ByteWriter w,
        in Log log,
        int from,
        int to,
        int[] kindSlot,
        int[] relationSlot)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        w.Hash = digest;
        w.Hashing = true;

        w.U8(Version);
        w.UVar((ulong)(to - from));
        for (int i = from; i < to; i++)
        {
            WriteEntry(ref w, in log.Entries[i], kindSlot, relationSlot);
        }

        w.Flush();
        w.Hashing = false;
        w.Bytes(digest.GetCurrentHash());
    }

    /// <summary>
    /// Applies journal operations to the saved graph, then adopts their entries into history.
    /// Checks replayed insert IDs against recorded IDs.
    /// </summary>
    public static bool ReadJournal(
        ByteReader r,
        ref Source src,
        ref Log log,
        KindId[] kinds,
        RelationId[] relations)
    {
        if (r.U8() != Version)
        {
            return false;
        }

        int count = r.UVarCount(SmallestEntry, "journal section");
        for (int i = 0; i < count; i++)
        {
            Entry entry = ReadEntry(ref r, kinds, relations);

            NodeId recorded = entry.Op.Node;
            ulong before = src.Stamp;
            OpApply.Apply(ref src, ref entry.Op);

            if (entry.Op.Kind == OpKind.Insert && recorded.IsSome && entry.Op.Node != recorded)
            {
                throw new FormatCorruptException(
                    "a journal entry inserted " + entry.Op.Node + " where the file says " + recorded +
                    " — the operations before it do not account for every id");
            }

            entry.StampBefore = before;
            entry.StampAfter = src.Stamp;
            Log.Adopt(ref log, entry);
        }

        return true;
    }

    /// <summary>The digest a journal section carries of itself, and the bytes it covers.</summary>
    public const int JournalDigestBytes = 32;

    /// <summary>
    /// Decodes supported history versions. For an unknown version, returns an empty log and leaves
    /// preservation to the caller.
    /// </summary>
    public static bool Read(ByteReader r, KindId[] kinds, RelationId[] relations, out Log log)
    {
        log = Log.Create();

        if (r.U8() != Version)
        {
            return false;
        }

        log = ReadEntries(ref r, kinds, relations);
        return true;
    }

    static Log ReadEntries(ref ByteReader r, KindId[] kinds, RelationId[] relations)
    {
        int count = r.UVarCount(SmallestEntry, "history section");

        Log log = Log.Create(count < 4 ? 4 : count);
        for (int i = 0; i < count; i++)
        {
            Log.Adopt(ref log, ReadEntry(ref r, kinds, relations));
        }
        return log;
    }

    static void WriteEntry(ref ByteWriter w, in Entry entry, int[] kindSlot, int[] relationSlot)
    {
        w.SVar(entry.Actor);
        w.UVar((ulong)entry.Order);

        w.U8((byte)entry.Op.Kind);
        w.U8((byte)entry.Op.NodeFlags);
        w.SVar(entry.Op.Node.Index);
        w.SVar(entry.Op.Parent.Index);
        w.SVar(entry.Op.Index);
        w.SVar(entry.Op.OldParent.Index);
        w.SVar(entry.Op.OldIndex);
        w.SVar(entry.Op.Count);

        w.SVar(Map(kindSlot, entry.Op.NodeKind.Value));
        w.SVar(Map(relationSlot, entry.Op.Relation.Value));
        w.SVar(entry.Op.Target.Index);

        WriteBytes(ref w, entry.Op.Value);
        WriteBytes(ref w, entry.Op.OldValue);
    }

    static Entry ReadEntry(ref ByteReader r, KindId[] kinds, RelationId[] relations)
    {
        Entry entry = default;

        entry.Actor = r.SVarInt();
        entry.Order = (long)r.UVar();

        entry.Op.Kind = (OpKind)r.U8();
        entry.Op.NodeFlags = (NodeFlags)r.U8();
        entry.Op.Node = new NodeId(r.SVarInt());
        entry.Op.Parent = new NodeId(r.SVarInt());
        entry.Op.Index = r.SVarInt();
        entry.Op.OldParent = new NodeId(r.SVarInt());
        entry.Op.OldIndex = r.SVarInt();
        entry.Op.Count = r.SVarInt();

        entry.Op.NodeKind = Unmap(kinds, r.SVarInt());
        entry.Op.Relation = Unmap(relations, r.SVarInt());
        entry.Op.Target = new NodeId(r.SVarInt());

        entry.Op.Value = ReadBytes(ref r);
        entry.Op.OldValue = ReadBytes(ref r);

        return entry;
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
        int length = r.UVarCount(1, "history entry payload");
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
