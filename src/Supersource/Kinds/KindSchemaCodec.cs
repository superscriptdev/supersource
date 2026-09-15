using System;
using System.Buffers.Binary;
using System.Text;

namespace Supersource.Kinds;
/// <summary>
/// Encodes embedded kind and relation schemas. Layouts are little-endian. Optional facts follow the
/// length-prefixed name so older readers can preserve trailing bytes and newer readers can default
/// absent facts to zero.
///
/// <code>
/// schema-set
/// ├── role-decl     payload: library(16) nameLen(u16) name(utf8)
/// │                            [ payloadFormat(u8) strideHeader(u16) strideUnit(u16) ]
/// ├── role-decl     ...
/// └── kind-schema   payload: library(16) facts(u16) payloadFormat(u8) shape(u8)
///     │                      color(u32) nameLen(u16) name(utf8)
///     │                      [ strideHeader(u16) strideUnit(u16) ]
///     └── refs: uses-role -> role-decl, one per relation
/// </code>
/// </summary>
public static class KindSchemaCodec
{
    const int LibraryBytes = 16;

    /// <summary>
    /// Creates a schema-set containing the schemas used by allocated nodes, plus the bootstrap
    /// schemas. Returns the schema-set node.
    /// </summary>
    public static NodeId Write(ref Source src, in KindTable table, in KindBootstrap.Ids ids, NodeId parent)
    {
        NodeId set = SourceBuild.AddChild(ref src, parent, ids.SchemaSet);

        // Include kinds used by the source, not every kind installed by the host.
        bool[] usedKind = new bool[table.KindCount];
        bool[] usedRelation = new bool[table.RelationCount];

        // Include bootstrap kinds before creating their schema nodes below.
        Mark(usedKind, ids.SchemaSet.Value);
        Mark(usedKind, ids.KindSchema.Value);
        Mark(usedKind, ids.RelationDecl.Value);
        Mark(usedKind, ids.Hole.Value);
        Mark(usedRelation, ids.UsesRelation.Value);

        for (long i = 0; i < src.NodeCount; i++)
        {
            // Retain schemas needed by deleted slots.
            int kind = src.Nodes[i].Kind.Value;
            if (kind >= 0 && kind < usedKind.Length)
            {
                usedKind[kind] = true;
            }

            int offset = src.Nodes[i].RefOffset;
            for (int r = 0; r < src.Nodes[i].RefCount; r++)
            {
                int relation = src.Refs[offset + r].Relation.Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        // Include relations declared by used schemas, even if no edge uses them yet.
        for (int i = 0; i < table.KindCount; i++)
        {
            if (!usedKind[i] || !table.SchemaDefined[i])
            {
                continue;
            }
            KindSchema schema = table.Schemas[i];
            for (int r = 0; r < schema.RelationCount; r++)
            {
                int relation = table.RelationArena[schema.RelationOffset + r].Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        // Relations first, so a kind-schema can reference one that already exists.
        NodeId[] relationNodes = new NodeId[table.RelationCount];
        for (int i = 0; i < table.RelationCount; i++)
        {
            if (!usedRelation[i])
            {
                continue;
            }
            QualifiedName name = table.RelationNames[i];
            RelationSchema carries = KindTable.RelationSchemaOf(in table, new RelationId(i));
            NodeId node = SourceBuild.AddChild(ref src, set, ids.RelationDecl);
            SourceBuild.SetPayload(ref src, node, EncodeRelation(name, in carries));
            relationNodes[i] = node;
        }

        for (int i = 0; i < table.KindCount; i++)
        {
            if (!usedKind[i])
            {
                continue;
            }
            if (!table.SchemaDefined[i])
            {
                // Do not turn an undefined schema into a definition with all facts false.
                continue;
            }

            QualifiedName name = table.KindNames[i];
            KindSchema schema = table.Schemas[i];

            NodeId node = SourceBuild.AddChild(ref src, set, ids.KindSchema);
            SourceBuild.SetPayload(ref src, node, EncodeKind(name, in schema));

            for (int r = 0; r < schema.RelationCount; r++)
            {
                RelationId used = table.RelationArena[schema.RelationOffset + r];
                if (used.Value >= 0 && used.Value < relationNodes.Length
                    && relationNodes[used.Value].IsSome)
                {
                    SourceBuild.AddRef(ref src, node, ids.UsesRelation, relationNodes[used.Value]);
                }
            }
        }

        return set;
    }

    /// <summary>
    /// Loads embedded schemas into a table with the bootstrap already installed. Returns the number
    /// of kind schemas defined; kinds without definitions remain unresolved.
    /// </summary>
    public static int Read(in Source src, ref KindTable table, in KindBootstrap.Ids ids)
    {
        int defined = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != ids.SchemaSet)
            {
                continue;
            }

            // Relations first: a kind-schema's uses-role edges point at role-decl nodes, and
            // resolving one means having interned it.
            ReadOnlySpan<long> children = SourceWalk.Children(in src, node);
            for (int c = 0; c < children.Length; c++)
            {
                NodeId child = new NodeId(children[c]);
                if (src.Nodes[child.Index].Kind == ids.RelationDecl)
                {
                    QualifiedName name = DecodeRelation(
                        SourceWalk.Payload(in src, child), out RelationSchema carries);

                    RelationId relation = KindTable.InternRelation(ref table, name);
                    if (carries.Payload != PayloadFormat.None)
                    {
                        KindTable.DefineRelation(ref table, relation, carries.Payload, carries.Stride);
                    }
                }
            }

            for (int c = 0; c < children.Length; c++)
            {
                NodeId child = new NodeId(children[c]);
                if (src.Nodes[child.Index].Kind != ids.KindSchema)
                {
                    continue;
                }

                ReadKindSchema(in src, ref table, in ids, child);
                defined++;
            }
        }

        return defined;
    }

    /// <summary>
    /// Appends missing declarations to an existing schema-set, preserving existing schema IDs and
    /// facts. Returns the number added, or zero if no schema-set exists; use Write to create one.
    /// </summary>
    public static int Sync(ref Source src, in KindTable table, in KindBootstrap.Ids ids)
    {
        NodeId set = NodeId.None;
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (src.IsLive(node) && src.Nodes[i].Kind == ids.SchemaSet)
            {
                set = node;
                break;
            }
        }

        if (set.IsNone)
        {
            return 0;
        }

        // Collect existing declarations for name-based lookup.
        ReadOnlySpan<long> children = SourceWalk.Children(in src, set);

        QualifiedName[] haveKinds = new QualifiedName[children.Length];
        int haveKindCount = 0;

        QualifiedName[] haveRelationNames = new QualifiedName[children.Length];
        NodeId[] haveRelationNodes = new NodeId[children.Length];
        int haveRelationCount = 0;

        for (int c = 0; c < children.Length; c++)
        {
            NodeId child = new NodeId(children[c]);
            if (!src.IsLive(child))
            {
                continue;
            }

            if (src.Nodes[child.Index].Kind == ids.RelationDecl)
            {
                QualifiedName declared = DecodeRelation(SourceWalk.Payload(in src, child));
                int at = IndexOf(haveRelationNames, haveRelationCount, declared);
                if (at < 0)
                {
                    at = haveRelationCount;
                    haveRelationCount++;
                }
                haveRelationNames[at] = declared;
                haveRelationNodes[at] = child;
            }
            else if (src.Nodes[child.Index].Kind == ids.KindSchema)
            {
                QualifiedName name;
                StructuralFact facts;
                PayloadFormat format;
                DisplayShape shape;
                uint color;
                DecodeKind(SourceWalk.Payload(in src, child),
                    out name, out facts, out format, out shape, out color, out _);
                if (IndexOf(haveKinds, haveKindCount, name) < 0)
                {
                    haveKinds[haveKindCount] = name;
                    haveKindCount++;
                }
            }
        }

        int added = 0;

        // Only include schemas used by allocated nodes and their references.
        bool[] usedKind = new bool[table.KindCount];
        bool[] usedRelation = new bool[table.RelationCount];

        for (long i = 0; i < src.NodeCount; i++)
        {
            // Deleted slots still serialize, so they retain their schemas.
            int kind = src.Nodes[i].Kind.Value;
            if (kind >= 0 && kind < usedKind.Length)
            {
                usedKind[kind] = true;
            }

            int offset = src.Nodes[i].RefOffset;
            for (int r = 0; r < src.Nodes[i].RefCount; r++)
            {
                int relation = src.Refs[offset + r].Relation.Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        for (int i = 0; i < table.KindCount; i++)
        {
            if (!usedKind[i] || !table.SchemaDefined[i])
            {
                continue;
            }

            KindSchema schema = table.Schemas[i];
            for (int r = 0; r < schema.RelationCount; r++)
            {
                int relation = table.RelationArena[schema.RelationOffset + r].Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        // Relations first, so a kind added below can point at one.
        for (int i = 0; i < table.RelationCount; i++)
        {
            QualifiedName name = table.RelationNames[i];
            if (name.IsNone || !usedRelation[i]
                || IndexOf(haveRelationNames, haveRelationCount, name) >= 0)
            {
                continue;
            }

            RelationSchema carries = KindTable.RelationSchemaOf(in table, new RelationId(i));
            NodeId node = SourceBuild.AddChild(ref src, set, ids.RelationDecl);
            SourceBuild.SetPayload(ref src, node, EncodeRelation(name, in carries));
            if (haveRelationCount == haveRelationNames.Length)
            {
                Array.Resize(ref haveRelationNames, haveRelationCount * 2 + 4);
                Array.Resize(ref haveRelationNodes, haveRelationCount * 2 + 4);
            }
            haveRelationNames[haveRelationCount] = name;
            haveRelationNodes[haveRelationCount] = node;
            haveRelationCount++;
            added++;
        }

        for (int i = 0; i < table.KindCount; i++)
        {
            if (!table.SchemaDefined[i] || !usedKind[i])
            {
                continue;
            }

            QualifiedName name = table.KindNames[i];
            if (name.IsNone || IndexOf(haveKinds, haveKindCount, name) >= 0)
            {
                continue;
            }

            KindSchema schema = table.Schemas[i];
            NodeId node = SourceBuild.AddChild(ref src, set, ids.KindSchema);
            SourceBuild.SetPayload(ref src, node, EncodeKind(name, in schema));

            for (int r = 0; r < schema.RelationCount; r++)
            {
                RelationId used = table.RelationArena[schema.RelationOffset + r];
                QualifiedName relationName = KindTable.RelationNameOf(in table, used);
                int decl = relationName.IsNone
                    ? -1
                    : IndexOf(haveRelationNames, haveRelationCount, relationName);

                if (decl >= 0)
                {
                    SourceBuild.AddRef(ref src, node, ids.UsesRelation, haveRelationNodes[decl]);
                }
            }

            added++;
        }

        return added;
    }

    /// <summary>Returns the matching index within count entries, or -1.</summary>
    static int IndexOf(QualifiedName[] names, int count, QualifiedName name)
    {
        for (int i = 0; i < count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Removes unused schema declarations and returns the number retired. Retains bootstrap schemas
    /// and schemas needed by deleted slots.
    /// </summary>
    public static int Prune(ref Source src, in KindTable table, in KindBootstrap.Ids ids)
    {
        bool[] usedKind = new bool[table.KindCount];
        bool[] usedRelation = new bool[table.RelationCount];

        Mark(usedKind, ids.SchemaSet.Value);
        Mark(usedKind, ids.KindSchema.Value);
        Mark(usedKind, ids.RelationDecl.Value);
        Mark(usedKind, ids.Hole.Value);
        Mark(usedRelation, ids.UsesRelation.Value);

        for (long i = 0; i < src.NodeCount; i++)
        {
            int kind = src.Nodes[i].Kind.Value;
            if (kind >= 0 && kind < usedKind.Length)
            {
                usedKind[kind] = true;
            }

            int offset = src.Nodes[i].RefOffset;
            for (int r = 0; r < src.Nodes[i].RefCount; r++)
            {
                int relation = src.Refs[offset + r].Relation.Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        for (int i = 0; i < table.KindCount; i++)
        {
            if (!usedKind[i] || !table.SchemaDefined[i])
            {
                continue;
            }

            KindSchema schema = table.Schemas[i];
            for (int r = 0; r < schema.RelationCount; r++)
            {
                int relation = table.RelationArena[schema.RelationOffset + r].Value;
                if (relation >= 0 && relation < usedRelation.Length)
                {
                    usedRelation[relation] = true;
                }
            }
        }

        int retired = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            QualifiedName name;
            bool keep;

            if (src.Nodes[i].Kind == ids.KindSchema)
            {
                StructuralFact facts;
                PayloadFormat format;
                DisplayShape shape;
                uint color;
                DecodeKind(SourceWalk.Payload(in src, node),
                    out name, out facts, out format, out shape, out color, out _);

                KindId kind = KindTable.Lookup(in table, name);
                keep = kind.IsUnknown
                    || kind.Value >= usedKind.Length
                    || usedKind[kind.Value];
            }
            else if (src.Nodes[i].Kind == ids.RelationDecl)
            {
                name = DecodeRelation(SourceWalk.Payload(in src, node));
                RelationId relation = KindTable.LookupRelation(in table, name);
                keep = relation.Value < 0
                    || relation.Value >= usedRelation.Length
                    || usedRelation[relation.Value];
            }
            else
            {
                continue;
            }

            if (!keep)
            {
                SourceBuild.Delete(ref src, node);
                retired++;
            }
        }

        return retired;
    }

    /// <summary>
    /// Updates existing schema facts from the table while preserving schema node IDs. Returns the
    /// number rewritten. Does not add missing declarations or update uses-role edges.
    /// </summary>
    public static int Refresh(ref Source src, in KindTable table, in KindBootstrap.Ids ids)
    {
        int fixedUp = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != ids.KindSchema)
            {
                continue;
            }

            QualifiedName name;
            StructuralFact facts;
            PayloadFormat format;
            DisplayShape shape;
            uint color;
            PayloadStride stride;
            DecodeKind(SourceWalk.Payload(in src, node),
                out name, out facts, out format, out shape, out color, out stride);

            if (name.IsNone)
            {
                continue;
            }

            KindId kind = KindTable.Lookup(in table, name);
            if (kind.IsUnknown || !KindTable.HasSchema(in table, kind))
            {
                // Preserve declarations absent from this table.
                continue;
            }

            KindSchema current = KindTable.SchemaOf(in table, kind);
            if (current.Facts == facts
                && current.Payload == format
                && current.Shape == shape
                && current.DisplayColor == color
                && current.Stride.Header == stride.Header
                && current.Stride.Unit == stride.Unit)
            {
                continue;
            }

            SourceBuild.SetPayload(ref src, node, EncodeKind(name, in current));
            fixedUp++;
        }

        return fixedUp;
    }

    /// <summary>
    /// Renames embedded schema payloads without replacing their nodes. Call before renaming the
    /// table, since matching uses the old names. Returns the number changed.
    /// </summary>
    public static int Rename(
        ref Source src,
        in KindBootstrap.Ids ids,
        ReadOnlySpan<QualifiedName> from,
        ReadOnlySpan<QualifiedName> to)
    {
        int changed = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            KindId kind = src.Nodes[i].Kind;
            bool isRelation = kind == ids.RelationDecl;
            bool isKind = kind == ids.KindSchema;
            if (!isRelation && !isKind)
            {
                continue;
            }

            ReadOnlySpan<byte> payload = SourceWalk.Payload(in src, node);

            if (isRelation)
            {
                QualifiedName name = DecodeRelation(payload, out RelationSchema carried);
                int at = IndexOf(from, name);
                if (at < 0)
                {
                    continue;
                }

                // Renaming a relation says nothing about what its edges carry, so whatever
                // the declaration already said comes along unchanged.
                SourceBuild.SetPayload(ref src, node, EncodeRelation(to[at], in carried));
                changed++;
                continue;
            }

            QualifiedName kindName;
            StructuralFact facts;
            PayloadFormat format;
            DisplayShape shape;
            uint color;
            PayloadStride stride;
            DecodeKind(payload, out kindName, out facts, out format, out shape, out color,
                out stride);

            int found = IndexOf(from, kindName);
            if (found < 0)
            {
                continue;
            }

            KindSchema schema = default;
            schema.Facts = facts;
            schema.Payload = format;
            schema.Shape = shape;
            schema.DisplayColor = color;
            schema.Stride = stride;

            SourceBuild.SetPayload(ref src, node, EncodeKind(to[found], in schema));
            changed++;
        }

        return changed;
    }

    static void Mark(bool[] flags, int at)
    {
        if (at >= 0 && at < flags.Length)
        {
            flags[at] = true;
        }
    }

    static int IndexOf(ReadOnlySpan<QualifiedName> names, QualifiedName name)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }
        return -1;
    }

    static void ReadKindSchema(in Source src, ref KindTable table, in KindBootstrap.Ids ids, NodeId node)
    {
        ReadOnlySpan<byte> payload = SourceWalk.Payload(in src, node);

        QualifiedName name;
        StructuralFact facts;
        PayloadFormat format;
        DisplayShape shape;
        uint color;
        PayloadStride stride;
        DecodeKind(payload, out name, out facts, out format, out shape, out color, out stride);

        KindId kind = KindTable.Intern(ref table, name);

        // Resolve relations through role-decl names, independent of either table’s intern order.
        ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
        RelationId[] used = new RelationId[refs.Length];
        int count = 0;

        for (int i = 0; i < refs.Length; i++)
        {
            if (refs[i].Relation != ids.UsesRelation || refs[i].IsUnbound)
            {
                continue;
            }

            NodeId target = refs[i].Target;
            if (!src.IsLive(target) || src.Nodes[target.Index].Kind != ids.RelationDecl)
            {
                continue;
            }

            used[count] = KindTable.InternRelation(ref table, DecodeRelation(SourceWalk.Payload(in src, target)));
            count++;
        }

        KindTable.DefineSchema(
            ref table, kind, facts, format, shape, color,
            new ReadOnlySpan<RelationId>(used, 0, count), stride);
    }

    static byte[] EncodeRelation(QualifiedName name, in RelationSchema schema)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name.Name ?? string.Empty);

        // Append optional edge-data facts after the name, preserving older no-data encodings.
        bool carries = schema.Payload != PayloadFormat.None;
        byte[] buffer = new byte[LibraryBytes + 2 + nameBytes.Length + (carries ? 5 : 0)];

        WriteLibrary(buffer, name.Library);
        BinaryPrimitives.WriteUInt16LittleEndian(
            new Span<byte>(buffer, LibraryBytes, 2), (ushort)nameBytes.Length);
        Array.Copy(nameBytes, 0, buffer, LibraryBytes + 2, nameBytes.Length);

        if (carries)
        {
            int at = LibraryBytes + 2 + nameBytes.Length;
            buffer[at] = (byte)schema.Payload;
            BinaryPrimitives.WriteUInt16LittleEndian(
                new Span<byte>(buffer, at + 1, 2), schema.Stride.Header);
            BinaryPrimitives.WriteUInt16LittleEndian(
                new Span<byte>(buffer, at + 3, 2), schema.Stride.Unit);
        }

        return buffer;
    }

    /// <summary>Decodes the qualified name from a role-decl payload.</summary>
    public static bool ReadRelation(ReadOnlySpan<byte> payload, out QualifiedName name)
    {
        name = DecodeRelation(payload);
        return !string.IsNullOrEmpty(name.Name);
    }

    public static bool ReadRelation(ReadOnlySpan<byte> payload, out QualifiedName name, out RelationSchema schema)
    {
        name = DecodeRelation(payload, out schema);
        return !string.IsNullOrEmpty(name.Name);
    }

    /// <summary>Update known fields without changing identity or discarding extension bytes.</summary>
    public static byte[] UpdateRelation(ReadOnlySpan<byte> original, in RelationSchema schema)
    {
        if (!ReadRelation(original, out _)) throw new ArgumentException("Invalid relation declaration.", nameof(original));
        int tail = LibraryBytes + 2 + BinaryPrimitives.ReadUInt16LittleEndian(original.Slice(LibraryBytes, 2));
        byte[] bytes = new byte[Math.Max(original.Length, tail + 5)];
        original.CopyTo(bytes);
        bytes[tail] = (byte)schema.Payload;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail + 1, 2), schema.Stride.Header);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail + 3, 2), schema.Stride.Unit);
        return bytes;
    }

    /// <summary>Update known fields without changing identity or discarding extension bytes.</summary>
    public static byte[] UpdateKind(ReadOnlySpan<byte> original, in StoredSchema schema)
    {
        if (!ReadKind(original, out _)) throw new ArgumentException("Invalid kind declaration.", nameof(original));
        int tail = LibraryBytes + 10 + BinaryPrimitives.ReadUInt16LittleEndian(original.Slice(LibraryBytes + 8, 2));
        byte[] bytes = new byte[Math.Max(original.Length, tail + 4)];
        original.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16, 2), (ushort)schema.Facts);
        bytes[18] = (byte)schema.Payload; bytes[19] = (byte)schema.Shape;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), schema.Colour);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail, 2), schema.Stride.Header);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(tail + 2, 2), schema.Stride.Unit);
        return bytes;
    }

    static QualifiedName DecodeRelation(ReadOnlySpan<byte> payload)
    {
        return DecodeRelation(payload, out RelationSchema _);
    }

    static QualifiedName DecodeRelation(ReadOnlySpan<byte> payload, out RelationSchema schema)
    {
        schema = default;

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

        // Older relation declarations end after the name; absent edge-data facts default to None.
        int at = LibraryBytes + 2 + length;
        if (at + 5 <= payload.Length)
        {
            schema.Payload = (PayloadFormat)payload[at];
            schema.Stride.Header = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 1, 2));
            schema.Stride.Unit = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 3, 2));
        }

        return new QualifiedName(library, name);
    }

    static byte[] EncodeKind(QualifiedName name, in KindSchema schema)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name.Name ?? string.Empty);

        // Omit an all-zero stride to preserve the encoding used before stride metadata was added.
        int strideBytes = schema.Stride.Unit == 0 && schema.Stride.Header == 0 ? 0 : 4;
        byte[] buffer = new byte[LibraryBytes + 2 + 1 + 1 + 4 + 2 + nameBytes.Length + strideBytes];

        int at = 0;
        WriteLibrary(buffer, name.Library);
        at += LibraryBytes;

        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)schema.Facts);
        at += 2;

        buffer[at] = (byte)schema.Payload;
        at += 1;

        buffer[at] = (byte)schema.Shape;
        at += 1;

        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buffer, at, 4), schema.DisplayColor);
        at += 4;

        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)nameBytes.Length);
        at += 2;

        Array.Copy(nameBytes, 0, buffer, at, nameBytes.Length);
        at += nameBytes.Length;

        if (strideBytes > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), schema.Stride.Header);
            at += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), schema.Stride.Unit);
        }

        return buffer;
    }

    /// <summary>Facts decoded directly from a stored kind-schema payload.</summary>
    public struct StoredSchema
    {
        public QualifiedName Name;
        public StructuralFact Facts;
        public PayloadFormat Payload;
        public DisplayShape Shape;
        public uint Colour;
        public PayloadStride Stride;
    }

    /// <summary>Decodes a kind-schema payload. Returns false for truncated input.</summary>
    public static bool ReadKind(ReadOnlySpan<byte> payload, out StoredSchema schema)
    {
        schema = default;

        DecodeKind(payload, out QualifiedName name, out StructuralFact facts,
            out PayloadFormat format, out DisplayShape shape, out uint colour,
            out PayloadStride stride);

        if (string.IsNullOrEmpty(name.Name))
        {
            return false;
        }

        schema.Name = name;
        schema.Facts = facts;
        schema.Payload = format;
        schema.Shape = shape;
        schema.Colour = colour;
        schema.Stride = stride;
        return true;
    }

    static void DecodeKind(
        ReadOnlySpan<byte> payload,
        out QualifiedName name,
        out StructuralFact facts,
        out PayloadFormat format,
        out DisplayShape shape,
        out uint color,
        out PayloadStride stride)
    {
        name = default;
        facts = StructuralFact.None;
        format = PayloadFormat.None;
        shape = DisplayShape.Default;
        color = 0;
        stride = default;

        const int Fixed = LibraryBytes + 2 + 1 + 1 + 4 + 2;
        if (payload.Length < Fixed)
        {
            return;
        }

        SourceId library = ReadLibrary(payload);
        int at = LibraryBytes;

        facts = (StructuralFact)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
        at += 2;

        format = (PayloadFormat)payload[at];
        at += 1;

        shape = (DisplayShape)payload[at];
        at += 1;

        color = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(at, 4));
        at += 4;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
        at += 2;

        if (at + length > payload.Length)
        {
            return;
        }

        name = new QualifiedName(library, Encoding.UTF8.GetString(payload.Slice(at, length)));
        at += length;

        // Older kind schemas omit stride metadata; absent values default to zero.
        if (at + 4 <= payload.Length)
        {
            stride.Header = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
            stride.Unit = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at + 2, 2));
        }
    }

    static void WriteLibrary(byte[] buffer, SourceId library)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, 0, 8), library.Hi);
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, 8, 8), library.Lo);
    }

    static SourceId ReadLibrary(ReadOnlySpan<byte> payload)
    {
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8));
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8, 8));
        return new SourceId(hi, lo);
    }
}
