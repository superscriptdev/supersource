using System;

namespace Supersource.Kinds;
/// <summary>
/// Remaps kind and relation handles between tables by qualified name. Unnameable handles become
/// Unknown. Use KindRename when changing the names themselves.
/// </summary>
public static class KindRemap
{
    /// <summary>
    /// Translates handles on live and deleted nodes, interning their names into the destination
    /// table as needed.
    /// </summary>
    public static void Apply(ref Source src, in KindTable from, ref KindTable to)
    {
        // Translation tables, built once. from.KindCount is small — one entry per distinct
        // kind in the source, not per node.
        KindId[] kinds = new KindId[from.KindCount];
        for (int i = 0; i < from.KindCount; i++)
        {
            QualifiedName name = from.KindNames[i];
            kinds[i] = name.IsNone ? KindId.Unknown : KindTable.Intern(ref to, name);
        }

        RelationId[] relations = new RelationId[from.RelationCount];
        for (int i = 0; i < from.RelationCount; i++)
        {
            QualifiedName name = from.RelationNames[i];
            relations[i] = name.IsNone ? RelationId.Unknown : KindTable.InternRelation(ref to, name);
        }

        for (long i = 0; i < src.NodeCount; i++)
        {
            int kind = src.Nodes[i].Kind.Value;
            src.Nodes[i].Kind = kind >= 0 && kind < kinds.Length ? kinds[kind] : KindId.Unknown;

            int offset = src.Nodes[i].RefOffset;
            int count = src.Nodes[i].RefCount;
            for (int r = 0; r < count; r++)
            {
                int relation = src.Refs[offset + r].Relation.Value;
                src.Refs[offset + r].Relation =
                    relation >= 0 && relation < relations.Length ? relations[relation] : RelationId.Unknown;
            }
        }
    }
}
