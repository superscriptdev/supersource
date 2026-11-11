using System;
using Supersource.Format;
using Supersource.Timeline;

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


    /// <summary>Nodes moved from a retired handle onto the surviving one.</summary>
    public int NodesMoved;

    /// <summary>Reference edges moved from a retired relation onto the surviving one.</summary>
    public int EdgesMoved;
}

/// <summary>
/// Renames kinds and relations in the table, which holds a file's schemas, and moves the nodes
/// and edges of a retired handle onto the survivor. Does not migrate structure or record timeline
/// operations.
/// </summary>
public static class KindRename
{
    /// <summary>Applies renames to the table, nodes and edges. Unused names are skipped.</summary>
    public static RenameResult Apply(ref SourceFile file, ReadOnlySpan<Rename> renames)
    {
        RenameResult result = default;
        if (renames.Length == 0)
        {
            return result;
        }

        // The table, which is where a file's schemas live. Where the new name was already
        // interned the two handles have to become one, and the nodes carrying the retired
        // handle move onto the survivor.
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
                    MoveRelation(ref file.Log, retired, survivor);
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
                    MoveKind(ref file.Log, retired, survivor);
                }
            }
        }

        return result;
    }

    /// <summary>Moves every node carrying the retired handle onto the survivor. A deleted slot holds no kind.</summary>
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

    /// <summary>
    /// The log names kinds by handle too: in the operations a redo replays and in what a deletion
    /// captured for its undo. A capture is replaced rather than written to, since a cloned log
    /// shares it.
    /// </summary>
    static void MoveKind(ref Log log, KindId retired, KindId survivor)
    {
        for (int i = 0; i < log.Count; i++)
        {
            ref Operation op = ref log.Entries[i].Op;
            if (op.NodeKind == retired) op.NodeKind = survivor;
            if (op.OldKind == retired) op.OldKind = survivor;

            Removed removed = op.Removed;
            if (removed == null || !Carries(removed, retired))
            {
                continue;
            }

            Removed moved = new Removed();
            moved.Whole = removed.Whole;
            moved.Nodes = (RemovedNode[])removed.Nodes.Clone();
            for (int n = 0; n < moved.Nodes.Length; n++)
            {
                if (moved.Nodes[n].Kind == retired) moved.Nodes[n].Kind = survivor;
            }
            op.Removed = moved;
        }
    }

    /// <summary>The relation counterpart of <see cref="MoveKind(ref Log, KindId, KindId)"/>.</summary>
    static void MoveRelation(ref Log log, RelationId retired, RelationId survivor)
    {
        for (int i = 0; i < log.Count; i++)
        {
            ref Operation op = ref log.Entries[i].Op;
            if (op.Relation == retired) op.Relation = survivor;

            Removed removed = op.Removed;
            if (removed == null || !Carries(removed, retired))
            {
                continue;
            }

            Removed moved = new Removed();
            moved.Whole = removed.Whole;
            moved.Nodes = (RemovedNode[])removed.Nodes.Clone();
            for (int n = 0; n < moved.Nodes.Length; n++)
            {
                RemovedRef[] refs = (RemovedRef[])moved.Nodes[n].Refs.Clone();
                for (int r = 0; r < refs.Length; r++)
                {
                    if (refs[r].Relation == retired) refs[r].Relation = survivor;
                }
                moved.Nodes[n].Refs = refs;
            }
            op.Removed = moved;
        }
    }

    static bool Carries(Removed removed, KindId kind)
    {
        for (int n = 0; n < removed.Nodes.Length; n++)
        {
            if (removed.Nodes[n].Kind == kind) return true;
        }
        return false;
    }

    static bool Carries(Removed removed, RelationId relation)
    {
        for (int n = 0; n < removed.Nodes.Length; n++)
        {
            for (int r = 0; r < removed.Nodes[n].Refs.Length; r++)
            {
                if (removed.Nodes[n].Refs[r].Relation == relation) return true;
            }
        }
        return false;
    }
}
