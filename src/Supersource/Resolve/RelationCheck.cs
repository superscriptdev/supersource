using System;
using Supersource.Kinds;

namespace Supersource.Resolve;
public enum FindingKind
{
    /// <summary>Relation absent from both the owner kind’s schema and caller-permitted extensions.</summary>
    UndeclaredRelation,

    /// <summary>Node kind has no defined schema.</summary>
    UnresolvedKind,

    /// <summary>Bound target is not live.</summary>
    DanglingReference,

    /// <summary>Reference has no target.</summary>
    Hole,
}

public struct Finding
{
    public FindingKind Kind;
    public NodeId Node;

    /// <summary>Index of the reference on that node, or -1 when the finding is about the node itself.</summary>
    public int RefIndex;

    public string Detail;
}

/// <summary>
/// Reports unresolved kinds, undeclared relations and unresolved references without rejecting or
/// modifying the source. Callers can explicitly permit additional relations.
/// </summary>
public static class RelationCheck
{
    public static int Check(
        in Source src,
        in KindTable table,
        ref Finding[] into,
        ReadOnlySpan<RelationId> permittedRelations = default)
    {
        if (into == null)
        {
            into = new Finding[16];
        }

        int count = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId id = new NodeId(i);
            if (!src.IsLive(id))
            {
                continue;
            }

            KindId kind = src.Nodes[i].Kind;

            if (!KindTable.HasSchema(in table, kind))
            {
                QualifiedName name = KindTable.NameOf(in table, kind);
                Add(ref into, ref count, FindingKind.UnresolvedKind, id, -1,
                    "no embedded schema for " + (name.IsNone ? "an unnamed kind" : name.ToString()));
                // Without a schema, there is no declared relation list to check.
                continue;
            }

            ReadOnlySpan<RelationId> declared = KindTable.UsedRelations(in table, kind);
            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, id);

            for (int r = 0; r < refs.Length; r++)
            {
                if (!Declares(declared, refs[r].Relation)
                    && !Declares(permittedRelations, refs[r].Relation))
                {
                    QualifiedName relation = KindTable.RelationNameOf(in table, refs[r].Relation);
                    Add(ref into, ref count, FindingKind.UndeclaredRelation, id, r,
                        "uses relation " + (relation.IsNone ? "<unknown>" : relation.Name) +
                        ", which its kind's schema does not declare");
                }

                Resolution state = Linker.Resolve(in src, in refs[r]);
                if (state == Resolution.Hole)
                {
                    Add(ref into, ref count, FindingKind.Hole, id, r,
                        "unbound: \"" + Text(SourceWalk.Label(in src, in refs[r])) + "\"");
                }
                else if (state != Resolution.Bound)
                {
                    Add(ref into, ref count, FindingKind.DanglingReference, id, r,
                        "names " + refs[r].Target + ", which is " + state.ToString().ToLowerInvariant());
                }
            }
        }

        return count;
    }

    static bool Declares(ReadOnlySpan<RelationId> declared, RelationId relation)
    {
        for (int i = 0; i < declared.Length; i++)
        {
            if (declared[i] == relation)
            {
                return true;
            }
        }
        return false;
    }

    static string Text(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(bytes);
    }

    static void Add(
        ref Finding[] into,
        ref int count,
        FindingKind kind,
        NodeId node,
        int refIndex,
        string detail)
    {
        if (count == into.Length)
        {
            Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
        }
        into[count].Kind = kind;
        into[count].Node = node;
        into[count].RefIndex = refIndex;
        into[count].Detail = detail;
        count++;
    }
}
