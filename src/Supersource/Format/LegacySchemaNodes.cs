using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Format;

/// <summary>
/// How a file of major version 4 or older carried its schemas: as nodes inside its own graph, a
/// `schema-set` holding `kind-schema` and `role-decl` nodes joined by `uses-role` edges. Reading
/// such a file decodes them into the kind table and removes them, so the graph holds only what the
/// application put there and the next full write stores them as a schema section.
///
/// <code>
/// role-decl     payload: library(16) nameLen(u16) name(utf8) [ format(u8) strideHeader(u16) strideUnit(u16) ]
/// kind-schema   payload: library(16) facts(u16) format(u8) shape(u8) colour(u32) nameLen(u16) name(utf8)
///                        [ strideHeader(u16) strideUnit(u16) ]
/// </code>
///
/// The old formats were none, opaque, utf8, int64, float64, bool and record (numbers 0 to 6).
/// A record with a stride is a *many* of sized elements after a sized header; one without is
/// opaque, which is how every tool already treated it. Display shape and colour are dropped.
/// </summary>
static class LegacySchemaNodes
{
    const int LibraryBytes = 16;

    /// <summary>The bootstrap identity those kinds were named under, ASCII "Superscript_boot".</summary>
    static readonly SourceId Library = new SourceId(0x5375706572736372UL, 0x6970745F626F6F74UL);

    static readonly QualifiedName SchemaSet = new QualifiedName(Library, "schema-set");
    static readonly QualifiedName KindSchema = new QualifiedName(Library, "kind-schema");
    static readonly QualifiedName RelationDecl = new QualifiedName(Library, "role-decl");
    static readonly QualifiedName UsesRelation = new QualifiedName(Library, "uses-role");

    /// <summary>Decodes every schema node into the table, then removes them from the graph.</summary>
    public static void Convert(ref SourceFile file)
    {
        KindId set = KindTable.Lookup(in file.Table, SchemaSet);
        if (set.IsUnknown)
        {
            return;
        }

        KindId kindSchema = KindTable.Lookup(in file.Table, KindSchema);
        KindId relationDecl = KindTable.Lookup(in file.Table, RelationDecl);
        RelationId uses = KindTable.LookupRelation(in file.Table, UsesRelation);

        NodeId[] sets = new NodeId[4];
        int setCount = 0;

        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!file.Src.IsLive(node) || file.Src.Nodes[i].Kind != set)
            {
                continue;
            }

            // Relations first: a kind-schema's uses-role edges point at role-decl nodes.
            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, node);
            for (int c = 0; c < children.Length; c++)
            {
                NodeId child = new NodeId(children[c]);
                if (file.Src.Nodes[child.Index].Kind == relationDecl)
                {
                    QualifiedName name = DecodeRelation(
                        SourceWalk.Payload(in file.Src, child), out PayloadFormat carries);
                    RelationId relation = KindTable.InternRelation(ref file.Table, name);
                    if (carries.Form != PayloadForm.None)
                    {
                        KindTable.DefineRelation(ref file.Table, relation, carries);
                    }
                }
            }

            for (int c = 0; c < children.Length; c++)
            {
                NodeId child = new NodeId(children[c]);
                if (file.Src.Nodes[child.Index].Kind == kindSchema)
                {
                    DefineKind(ref file, child, relationDecl, uses);
                }
            }

            if (setCount == sets.Length)
            {
                Array.Resize(ref sets, setCount * 2);
            }
            sets[setCount] = node;
            setCount++;
        }

        for (int i = 0; i < setCount; i++)
        {
            SourceBuild.Delete(ref file.Src, sets[i]);
        }
    }

    static void DefineKind(ref SourceFile file, NodeId node, KindId relationDecl, RelationId uses)
    {
        ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, node);
        if (!DecodeKind(payload, out QualifiedName name, out StructuralFact facts, out PayloadFormat format))
        {
            return;
        }

        KindId kind = KindTable.Intern(ref file.Table, name);

        // Resolve relations through role-decl names, independent of either table's intern order.
        ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, node);
        RelationId[] used = new RelationId[refs.Length];
        int count = 0;
        for (int i = 0; i < refs.Length; i++)
        {
            if (refs[i].Relation != uses || refs[i].IsUnbound)
            {
                continue;
            }

            NodeId target = refs[i].Target;
            if (!file.Src.IsLive(target) || file.Src.Nodes[target.Index].Kind != relationDecl)
            {
                continue;
            }

            used[count] = KindTable.InternRelation(ref file.Table,
                DecodeRelation(SourceWalk.Payload(in file.Src, target), out _));
            count++;
        }

        KindTable.DefineSchema(ref file.Table, kind, facts, format,
            new ReadOnlySpan<RelationId>(used, 0, count));
    }

    static QualifiedName DecodeRelation(ReadOnlySpan<byte> payload, out PayloadFormat format)
    {
        format = PayloadFormat.None;
        if (payload.Length < LibraryBytes + 2)
        {
            return default;
        }

        SourceId library = ReadLibrary(payload);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(LibraryBytes, 2));
        if (LibraryBytes + 2 + length > payload.Length)
        {
            return default;
        }

        string name = Encoding.UTF8.GetString(payload.Slice(LibraryBytes + 2, length));

        int at = LibraryBytes + 2 + length;
        if (at + 5 <= payload.Length)
        {
            format = Translate(payload[at],
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 1, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 3, 2)));
        }

        return new QualifiedName(library, name);
    }

    static bool DecodeKind(
        ReadOnlySpan<byte> payload,
        out QualifiedName name,
        out StructuralFact facts,
        out PayloadFormat format)
    {
        name = default;
        facts = StructuralFact.None;
        format = PayloadFormat.None;

        const int Fixed = LibraryBytes + 2 + 1 + 1 + 4 + 2;
        if (payload.Length < Fixed)
        {
            return false;
        }

        SourceId library = ReadLibrary(payload);
        int at = LibraryBytes;

        facts = (StructuralFact)(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2)) & ~SchemaSection.RetiredFacts);
        at += 2;

        byte old = payload[at];
        at += 1;

        // Display shape and colour: how a kind looks is not the format's to say.
        at += 1 + 4;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
        at += 2;
        if (at + length > payload.Length)
        {
            return false;
        }

        name = new QualifiedName(library, Encoding.UTF8.GetString(payload.Slice(at, length)));
        at += length;

        ushort header = 0;
        ushort unit = 0;
        if (at + 4 <= payload.Length)
        {
            header = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
            unit = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 2, 2));
        }

        format = Translate(old, header, unit);
        return !string.IsNullOrEmpty(name.Name);
    }

    /// <summary>An old format number and stride, as a payload format.</summary>
    static PayloadFormat Translate(byte old, ushort header, ushort unit)
    {
        switch (old)
        {
            case 0: return PayloadFormat.None;
            case 2: return PayloadFormat.Text;
            case 3: return PayloadFormat.One(PayloadScalar.I64);
            case 4: return PayloadFormat.One(PayloadScalar.F64);
            case 5: return PayloadFormat.One(PayloadScalar.Bool);
            case 6:
                return unit != 0
                    ? PayloadFormat.Many(PayloadElement.Sized(unit), PayloadElement.Sized(header))
                    : PayloadFormat.Opaque;
            default: return PayloadFormat.Opaque;
        }
    }

    static SourceId ReadLibrary(ReadOnlySpan<byte> payload)
    {
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8));
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8, 8));
        return new SourceId(hi, lo);
    }
}
