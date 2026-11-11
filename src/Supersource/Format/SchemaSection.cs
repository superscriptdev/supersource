using System;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Format;

/// <summary>
/// The embedded schemas: one entry per kind the file names that has a schema, and one per relation
/// whose edges carry data. Kinds and relations are named by their slot in the file's name tables,
/// so this section is read after them and before the nodes, whose payload lengths it can fix.
///
/// <code>
/// uvar kindCount
///   uvar kindSlot · u16 facts · format · uvar relationCount · uvar relationSlot*
/// uvar relationCount
///   uvar relationSlot · format
///
/// format   u8 form (none 0, opaque 1, text 2, one 3, many 4, text after a header 5)
///          one, many: element
///          many:      header (an element; size 0 for none)
///          text after a header: header
/// element  uvar size · uvar fieldCount · fields
/// field    uvar nameLength · name (utf8) · u8 scalar · uvar offset · uvar repeat
/// </code>
/// </summary>
static class SchemaSection
{
    public static byte[] Write(in KindTable table, int[] kindOrder, int[] relationOrder, int[] relationSlot)
    {
        ByteWriter w = ByteWriter.Create(256);

        int kinds = 0;
        for (int i = 0; i < kindOrder.Length; i++)
        {
            if (KindTable.HasSchema(in table, new KindId(kindOrder[i])))
            {
                kinds++;
            }
        }

        w.UVar((ulong)kinds);
        for (int slot = 0; slot < kindOrder.Length; slot++)
        {
            KindId kind = new KindId(kindOrder[slot]);
            if (!KindTable.HasSchema(in table, kind))
            {
                continue;
            }

            KindSchema schema = KindTable.SchemaOf(in table, kind);
            w.UVar((ulong)slot);
            w.U16((ushort)schema.Facts);
            WriteFormat(ref w, in schema.Payload);

            ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in table, kind);
            w.UVar((ulong)used.Length);
            for (int r = 0; r < used.Length; r++)
            {
                w.UVar((ulong)relationSlot[used[r].Value]);
            }
        }

        int relations = 0;
        for (int i = 0; i < relationOrder.Length; i++)
        {
            if (KindTable.RelationSchemaOf(in table, new RelationId(relationOrder[i])).Payload.Form != PayloadForm.None)
            {
                relations++;
            }
        }

        w.UVar((ulong)relations);
        for (int slot = 0; slot < relationOrder.Length; slot++)
        {
            RelationSchema schema = KindTable.RelationSchemaOf(in table, new RelationId(relationOrder[slot]));
            if (schema.Payload.Form == PayloadForm.None)
            {
                continue;
            }

            w.UVar((ulong)slot);
            WriteFormat(ref w, in schema.Payload);
        }

        return w.ToArray();
    }

    /// <summary>Defines every schema the section carries in the table, through the file's slots.</summary>
    public static void Read(ByteReader r, ref KindTable table, KindId[] kinds, RelationId[] relations)
    {
        int kindCount = r.UVarCount(5, "kind schemas");
        for (int i = 0; i < kindCount; i++)
        {
            int slot = r.UVarInt();
            StructuralFact facts = (StructuralFact)(r.U16() & ~RetiredFacts);
            PayloadFormat format = ReadFormat(ref r);

            int usedCount = r.UVarCount(1, "relations a kind uses");
            RelationId[] used = new RelationId[usedCount];
            for (int u = 0; u < usedCount; u++)
            {
                used[u] = Slot(relations, r.UVarInt(), "relation");
            }

            KindTable.DefineSchema(ref table, Slot(kinds, slot, "kind"), facts, format, used);
        }

        int relationCount = r.UVarCount(2, "relation schemas");
        for (int i = 0; i < relationCount; i++)
        {
            RelationId relation = Slot(relations, r.UVarInt(), "relation");
            KindTable.DefineRelation(ref table, relation, ReadFormat(ref r));
        }
    }

    static KindId Slot(KindId[] kinds, int slot, string what)
    {
        if (slot < 0 || slot >= kinds.Length)
        {
            throw new FormatCorruptException("a schema names " + what + " slot " + slot + ", which the file does not have");
        }
        return kinds[slot];
    }

    static RelationId Slot(RelationId[] relations, int slot, string what)
    {
        if (slot < 0 || slot >= relations.Length)
        {
            throw new FormatCorruptException("a schema names " + what + " slot " + slot + ", which the file does not have");
        }
        return relations[slot];
    }

    /// <summary>Fact bits 0 and 1, declaration and opens-scope: a language's, not the format's.</summary>
    internal const int RetiredFacts = 0b11;

    /// <summary>Text after a header, on the wire; in memory it is text with a header.</summary>
    const byte TextAfterHeader = 5;

    internal static void WriteFormat(ref ByteWriter w, in PayloadFormat format)
    {
        if (format.Form == PayloadForm.Text && format.Header.Size > 0)
        {
            w.U8(TextAfterHeader);
            WriteElement(ref w, in format.Header);
            return;
        }

        w.U8((byte)format.Form);
        if (format.Form == PayloadForm.One || format.Form == PayloadForm.Many)
        {
            WriteElement(ref w, in format.Element);
        }
        if (format.Form == PayloadForm.Many)
        {
            WriteElement(ref w, in format.Header);
        }
    }

    static PayloadFormat ReadFormat(ref ByteReader r)
    {
        byte wire = r.U8();
        if (wire == TextAfterHeader)
        {
            return PayloadFormat.TextAfter(ReadElement(ref r));
        }

        PayloadForm form = (PayloadForm)wire;
        switch (form)
        {
            case PayloadForm.None: return PayloadFormat.None;
            case PayloadForm.Opaque: return PayloadFormat.Opaque;
            case PayloadForm.Text: return PayloadFormat.Text;
            case PayloadForm.One: return PayloadFormat.One(ReadElement(ref r));
            case PayloadForm.Many:
                PayloadElement element = ReadElement(ref r);
                return PayloadFormat.Many(element, ReadElement(ref r));
            default:
                throw new FormatCorruptException("a schema names payload form " + (int)form + ", which this build does not know");
        }
    }

    static void WriteElement(ref ByteWriter w, in PayloadElement element)
    {
        w.UVar(element.Size);
        int count = element.Fields == null ? 0 : element.Fields.Length;
        w.UVar((ulong)count);
        for (int i = 0; i < count; i++)
        {
            PayloadField field = element.Fields[i];
            byte[] name = Encoding.UTF8.GetBytes(field.Name ?? string.Empty);
            w.UVar((ulong)name.Length);
            w.Bytes(name);
            w.U8((byte)field.Type);
            w.UVar(field.Offset);
            w.UVar(field.Repeat);
        }
    }

    static PayloadElement ReadElement(ref ByteReader r)
    {
        ushort size = (ushort)r.UVarInt();
        int count = r.UVarCount(4, "fields");
        PayloadField[] fields = new PayloadField[count];
        for (int i = 0; i < count; i++)
        {
            int length = r.UVarCount(1, "field name");
            string name = Encoding.UTF8.GetString(r.Bytes(length));
            PayloadScalar type = (PayloadScalar)r.U8();
            ushort offset = (ushort)r.UVarInt();
            ushort repeat = (ushort)r.UVarInt();
            fields[i] = PayloadField.Of(name, type, offset, repeat);
        }
        return PayloadElement.Of(size, fields);
    }
}
