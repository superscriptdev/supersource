using System;

namespace Supersource.Kinds;
/// <summary>
/// Interns qualified kind and relation names into local handles. Handles are meaningful only within
/// this table. An interned kind may have no defined schema.
/// </summary>
public struct KindTable
{
    /// <summary>Indexed by KindId.Value.</summary>
    public KindSchema[] Schemas;
    public QualifiedName[] KindNames;
    public bool[] SchemaDefined;
    public int KindCount;

    /// <summary>Indexed by RelationId.Value.</summary>
    public QualifiedName[] RelationNames;

    /// <summary>What an edge of each relation carries, parallel to <see cref="RelationNames"/>.</summary>
    public RelationSchema[] RelationSchemas;

    public int RelationCount;

    /// <summary>Backing store for each schema's used-relations run.</summary>
    public RelationId[] RelationArena;
    public int RelationArenaUsed;

    int[] kindBuckets;
    int[] relationBuckets;

    public static KindTable Create(int capacity = 64)
    {
        if (capacity < 8)
        {
            capacity = 8;
        }

        KindTable t = default;
        t.Schemas = new KindSchema[capacity];
        t.KindNames = new QualifiedName[capacity];
        t.SchemaDefined = new bool[capacity];
        t.KindCount = 0;
        t.RelationNames = new QualifiedName[capacity];
        t.RelationSchemas = new RelationSchema[capacity];
        t.RelationCount = 0;
        t.RelationArena = new RelationId[capacity];
        t.RelationArenaUsed = 0;
        t.kindBuckets = new int[NextPowerOfTwo(capacity * 2)];
        t.relationBuckets = new int[NextPowerOfTwo(capacity * 2)];
        return t;
    }

    /// <summary>Interns a name, allocating a handle if needed. Does not define a schema.</summary>
    public static KindId Intern(ref KindTable t, QualifiedName name)
    {
        int bucket = FindBucket(t.kindBuckets, t.KindNames, name);
        if (t.kindBuckets[bucket] != 0)
        {
            return new KindId(t.kindBuckets[bucket] - 1);
        }

        EnsureKindCapacity(ref t, t.KindCount + 1);

        int index = t.KindCount;
        t.KindCount++;
        t.KindNames[index] = name;
        t.SchemaDefined[index] = false;
        t.Schemas[index] = default;
        t.Schemas[index].Kind = new KindId(index);

        if ((t.KindCount + 1) * 2 > t.kindBuckets.Length)
        {
            Rehash(ref t.kindBuckets, t.KindNames, t.KindCount);
        }
        else
        {
            t.kindBuckets[FindBucket(t.kindBuckets, t.KindNames, name)] = index + 1;
        }

        return new KindId(index);
    }

    /// <summary>Handle for this name if it has one, KindId.Unknown otherwise. Never allocates.</summary>
    public static KindId Lookup(in KindTable t, QualifiedName name)
    {
        int bucket = FindBucket(t.kindBuckets, t.KindNames, name);
        int slot = t.kindBuckets[bucket];
        return slot == 0 ? KindId.Unknown : new KindId(slot - 1);
    }

    public static QualifiedName NameOf(in KindTable t, KindId kind)
    {
        if (kind.Value < 0 || kind.Value >= t.KindCount)
        {
            return default;
        }
        return t.KindNames[kind.Value];
    }

    /// <summary>Returns whether the table has a defined schema for the kind.</summary>
    public static bool HasSchema(in KindTable t, KindId kind)
    {
        return kind.Value >= 0 && kind.Value < t.KindCount && t.SchemaDefined[kind.Value];
    }

    /// <summary>
    /// Returns the schema, or all-zero facts when undefined. Use HasSchema to distinguish an absent
    /// definition from declared zero values.
    /// </summary>
    public static KindSchema SchemaOf(in KindTable t, KindId kind)
    {
        if (kind.Value < 0 || kind.Value >= t.KindCount)
        {
            return default;
        }
        return t.Schemas[kind.Value];
    }

    /// <summary>Defines a kind’s facts and used relations. A zero stride means no array layout is declared.</summary>
    public static void DefineSchema(
        ref KindTable t,
        KindId kind,
        StructuralFact facts,
        PayloadFormat payload,
        DisplayShape shape,
        uint displayColor,
        ReadOnlySpan<RelationId> usesRelations,
        PayloadStride stride = default)
    {
        if (kind.Value < 0 || kind.Value >= t.KindCount)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        int offset = 0;
        if (usesRelations.Length > 0)
        {
            EnsureRelationArenaCapacity(ref t, t.RelationArenaUsed + usesRelations.Length);
            offset = t.RelationArenaUsed;
            for (int i = 0; i < usesRelations.Length; i++)
            {
                t.RelationArena[offset + i] = usesRelations[i];
            }
            t.RelationArenaUsed += usesRelations.Length;
        }

        t.Schemas[kind.Value].Kind = kind;
        t.Schemas[kind.Value].Facts = facts;
        t.Schemas[kind.Value].Payload = payload;
        t.Schemas[kind.Value].Shape = shape;
        t.Schemas[kind.Value].Stride = stride;
        t.Schemas[kind.Value].DisplayColor = displayColor;
        t.Schemas[kind.Value].RelationOffset = offset;
        t.Schemas[kind.Value].RelationCount = usesRelations.Length;
        t.SchemaDefined[kind.Value] = true;
    }

    /// <summary>
    /// State what an edge of this relation carries. Most carry nothing, which is the
    /// default and needs no call.
    /// </summary>
    public static void DefineRelation(
        ref KindTable t,
        RelationId relation,
        PayloadFormat payload,
        PayloadStride stride = default)
    {
        if (relation.Value < 0 || relation.Value >= t.RelationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(relation));
        }

        t.RelationSchemas[relation.Value].Payload = payload;
        t.RelationSchemas[relation.Value].Stride = stride;
    }

    /// <summary>What an edge of this relation carries. None for a relation nobody described.</summary>
    public static RelationSchema RelationSchemaOf(in KindTable t, RelationId relation)
    {
        if (relation.Value < 0 || relation.Value >= t.RelationCount)
        {
            return default;
        }
        return t.RelationSchemas[relation.Value];
    }

    /// <summary>Returns the relation handles declared by the kind’s schema.</summary>
    public static ReadOnlySpan<RelationId> UsedRelations(in KindTable t, KindId kind)
    {
        if (kind.Value < 0 || kind.Value >= t.KindCount)
        {
            return ReadOnlySpan<RelationId>.Empty;
        }
        return new ReadOnlySpan<RelationId>(
            t.RelationArena,
            t.Schemas[kind.Value].RelationOffset,
            t.Schemas[kind.Value].RelationCount);
    }

    /// <summary>
    /// Renames a handle in place, or reports the existing handle under the new name. In the latter
    /// case, KindRename must remap affected nodes. Retired slots retain their old names.
    /// </summary>
    public static bool Rename(
        ref KindTable t,
        QualifiedName from,
        QualifiedName to,
        out KindId survivor,
        out KindId retired)
    {
        survivor = KindId.Unknown;
        retired = KindId.Unknown;

        KindId old = Lookup(in t, from);
        if (old.IsUnknown)
        {
            return false;
        }

        KindId existing = Lookup(in t, to);
        if (!existing.IsUnknown)
        {
            if (existing == old)
            {
                survivor = old;
                return true;
            }
            survivor = existing;
            retired = old;
            return true;
        }

        t.KindNames[old.Value] = to;
        Rehash(ref t.kindBuckets, t.KindNames, t.KindCount);
        survivor = old;
        return true;
    }

    /// <summary>The relation counterpart of <see cref="Rename"/>, with the same two outcomes.</summary>
    public static bool RenameRelation(
        ref KindTable t,
        QualifiedName from,
        QualifiedName to,
        out RelationId survivor,
        out RelationId retired)
    {
        survivor = RelationId.Unknown;
        retired = RelationId.Unknown;

        RelationId old = LookupRelation(in t, from);
        if (old.IsUnknown)
        {
            return false;
        }

        RelationId existing = LookupRelation(in t, to);
        if (!existing.IsUnknown)
        {
            if (existing == old)
            {
                survivor = old;
                return true;
            }
            survivor = existing;
            retired = old;
            return true;
        }

        t.RelationNames[old.Value] = to;
        Rehash(ref t.relationBuckets, t.RelationNames, t.RelationCount);
        survivor = old;
        return true;
    }

    public static RelationId InternRelation(ref KindTable t, QualifiedName name)
    {
        int bucket = FindBucket(t.relationBuckets, t.RelationNames, name);
        if (t.relationBuckets[bucket] != 0)
        {
            return new RelationId(t.relationBuckets[bucket] - 1);
        }

        EnsureRelationCapacity(ref t, t.RelationCount + 1);

        int index = t.RelationCount;
        t.RelationCount++;
        t.RelationNames[index] = name;
        t.RelationSchemas[index] = default;

        if ((t.RelationCount + 1) * 2 > t.relationBuckets.Length)
        {
            Rehash(ref t.relationBuckets, t.RelationNames, t.RelationCount);
        }
        else
        {
            t.relationBuckets[FindBucket(t.relationBuckets, t.RelationNames, name)] = index + 1;
        }

        return new RelationId(index);
    }

    public static RelationId LookupRelation(in KindTable t, QualifiedName name)
    {
        int slot = t.relationBuckets[FindBucket(t.relationBuckets, t.RelationNames, name)];
        return slot == 0 ? RelationId.Unknown : new RelationId(slot - 1);
    }

    public static QualifiedName RelationNameOf(in KindTable t, RelationId relation)
    {
        if (relation.Value < 0 || relation.Value >= t.RelationCount)
        {
            return default;
        }
        return t.RelationNames[relation.Value];
    }

    // Linear probing in a power-of-two table.

    static int FindBucket(int[] buckets, QualifiedName[] names, QualifiedName name)
    {
        int mask = buckets.Length - 1;
        int at = name.GetHashCode() & mask;

        while (true)
        {
            int slot = buckets[at];
            if (slot == 0)
            {
                return at;
            }
            if (names[slot - 1] == name)
            {
                return at;
            }
            at = (at + 1) & mask;
        }
    }

    static void Rehash(ref int[] buckets, QualifiedName[] names, int count)
    {
        buckets = new int[buckets.Length * 2];
        for (int i = 0; i < count; i++)
        {
            buckets[FindBucket(buckets, names, names[i])] = i + 1;
        }
    }

    static void EnsureKindCapacity(ref KindTable t, int needed)
    {
        if (t.Schemas.Length >= needed)
        {
            return;
        }

        int size = t.Schemas.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref t.Schemas, size);
        Array.Resize(ref t.KindNames, size);
        Array.Resize(ref t.SchemaDefined, size);
    }

    static void EnsureRelationCapacity(ref KindTable t, int needed)
    {
        if (t.RelationNames.Length >= needed)
        {
            return;
        }

        int size = t.RelationNames.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref t.RelationNames, size);
        Array.Resize(ref t.RelationSchemas, size);
    }

    static void EnsureRelationArenaCapacity(ref KindTable t, int needed)
    {
        if (t.RelationArena.Length >= needed)
        {
            return;
        }

        int size = t.RelationArena.Length;
        while (size < needed)
        {
            size *= 2;
        }
        Array.Resize(ref t.RelationArena, size);
    }

    static int NextPowerOfTwo(int n)
    {
        int p = 8;
        while (p < n)
        {
            p *= 2;
        }
        return p;
    }
}
