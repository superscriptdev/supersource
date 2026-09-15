using System;
using Supersource.Format;

namespace Supersource.Kinds;
/// <summary>One durable name becoming another. A kind or a relation — never both at once.</summary>
public struct Rename
{
    public QualifiedName From;
    public QualifiedName To;

    /// <summary>True when this renames a reference relation rather than a kind.</summary>
    public bool IsRelation;

    public static Rename Kind(QualifiedName from, QualifiedName to)
    {
        Rename r;
        r.From = from;
        r.To = to;
        r.IsRelation = false;
        return r;
    }

    public static Rename Relation(QualifiedName from, QualifiedName to)
    {
        Rename r;
        r.From = from;
        r.To = to;
        r.IsRelation = true;
        return r;
    }
}

/// <summary>Counts and mappings produced by renaming.</summary>
public struct RenameResult
{
    /// <summary>Names that were found and changed. A name the source never used is not an error.</summary>
    public int Renamed;

    /// <summary>Schema payloads rewritten, so the source says on disk what its table says in memory.</summary>
    public int SchemasRewritten;

    /// <summary>Nodes moved from a retired handle onto the surviving one.</summary>
    public int NodesMoved;

    /// <summary>Reference edges moved from a retired relation onto the surviving one.</summary>
    public int EdgesMoved;
}

/// <summary>
/// Renames kinds and relations in the table and embedded schemas. Rewrites embedded names before
/// changing the table. Does not migrate structure or record timeline operations.
/// </summary>
public static class KindRename
{
    /// <summary>Applies renames to embedded schemas, tables and nodes. Unused names are skipped.</summary>
    public static RenameResult Apply(ref SourceFile file, ReadOnlySpan<Rename> renames)
    {
        RenameResult result = default;
        if (renames.Length == 0)
        {
            return result;
        }

        // 1. The source's own account of itself, first — see the note on order above.
        QualifiedName[] from = new QualifiedName[renames.Length];
        QualifiedName[] to = new QualifiedName[renames.Length];
        for (int i = 0; i < renames.Length; i++)
        {
            from[i] = renames[i].From;
            to[i] = renames[i].To;
        }

        result.SchemasRewritten = KindSchemaCodec.Rename(
            ref file.Src, in file.Boot,
            new ReadOnlySpan<QualifiedName>(from),
            new ReadOnlySpan<QualifiedName>(to));

        // 2. The table. Where the new name was already interned the two handles have to
        //    become one, and the nodes carrying the retired handle are step 3's work.
        for (int i = 0; i < renames.Length; i++)
        {
            if (renames[i].IsRelation)
            {
                RelationId survivor, retired;
                if (!KindTable.RenameRelation(ref file.Table, renames[i].From, renames[i].To,
                        out survivor, out retired))
                {
                    continue;
                }
                result.Renamed++;

                if (!retired.IsUnknown)
                {
                    result.EdgesMoved += MoveRelation(ref file.Src, retired, survivor);
                }
            }
            else
            {
                KindId survivor, retired;
                if (!KindTable.Rename(ref file.Table, renames[i].From, renames[i].To,
                        out survivor, out retired))
                {
                    continue;
                }
                result.Renamed++;

                if (!retired.IsUnknown)
                {
                    result.NodesMoved += MoveKind(ref file.Src, retired, survivor);
                }
            }
        }

        return result;
    }

    /// <summary>Remap deleted slots too, since their handles are serialized and needed for undo.</summary>
    static int MoveKind(ref Source src, KindId retired, KindId survivor)
    {
        int moved = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            if (src.Nodes[i].Kind == retired)
            {
                src.Nodes[i].Kind = survivor;
                moved++;
            }
        }
        return moved;
    }

    static int MoveRelation(ref Source src, RelationId retired, RelationId survivor)
    {
        int moved = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                if (src.Refs[offset + r].Relation == retired)
                {
                    src.Refs[offset + r].Relation = survivor;
                    moved++;
                }
            }
        }
        return moved;
    }
}
