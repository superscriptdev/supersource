using System;
using Supersource.Kinds;
using Supersource.Resolve;

namespace Supersource.Rules;

public enum Severity
{
    /// <summary>Reported, and does not stop anything.</summary>
    Warn,

    /// <summary>Reported, and severe enough that a change carrying it should not stand.</summary>
    Block,
}

/// <summary>The addressable subject of a diagnostic: source, node, reference, kind or relation.</summary>
public enum SubjectKind : byte
{
    /// <summary>The source as a whole. Nothing narrower is to blame.</summary>
    Source = 0,

    Node = 1,

    /// <summary>A reference identified by its owner node and index within that node’s current reference run.</summary>
    Reference = 2,

    Kind = 3,

    Relation = 4,
}

/// <summary>
/// One finding. The subject is a small closed union rather than a node, because a rule
/// that cannot name what is wrong is a rule an editor cannot act on.
/// </summary>
public struct RuleViolation
{
    public string Rule;
    public Severity Severity;

    /// <summary>Which of the four fields below carries the subject.</summary>
    public SubjectKind Subject;

    /// <summary>The node, for Node and Reference.</summary>
    public NodeId Node;

    /// <summary>Which of <see cref="Node"/>'s edges, for Reference.</summary>
    public int RefIndex;

    /// <summary>The kind, for Kind.</summary>
    public KindId Kind;

    /// <summary>The relation, for Relation.</summary>
    public RelationId Relation;

    public string Message;
}

/// <summary>Opt-in checks over the whole source, independent of domain libraries.</summary>
public enum StockRule : byte
{
    NoDanglingReferences = 0,
    NoHoles = 1,
    EveryDeclarationHasATextLabel = 2,
    EveryKindIsInterpretable = 3,
    PayloadMatchesTheSchema = 4,
    SchemaDeclaresOnlyWhatIsUsed = 5,
    EdgeDataMatchesTheRelation = 8,
}

public struct Rule
{
    public StockRule Which;
    public Severity Severity;
}

/// <summary>
/// Caller-selected checks and severities. SourceRead does not run these rules; structural validity
/// is checked separately by SourceValidate.
/// </summary>
public struct RuleSet
{
    public Rule[] Rules;
    public int Count;

    public static RuleSet Create()
    {
        RuleSet set;
        set.Rules = new Rule[8];
        set.Count = 0;
        return set;
    }

    public static void Add(ref RuleSet set, StockRule which, Severity severity)
    {
        if (set.Count == set.Rules.Length)
        {
            Array.Resize(ref set.Rules, set.Rules.Length == 0 ? 16 : set.Rules.Length * 2);
        }
        set.Rules[set.Count].Which = which;
        set.Rules[set.Count].Severity = severity;
        set.Count++;
    }

    /// <summary>Returns the diagnostic name for a stock rule.</summary>
    public static string NameOf(StockRule which)
    {
        switch (which)
        {
            case StockRule.NoDanglingReferences: return "no-dangling-references";
            case StockRule.NoHoles: return "no-unfilled-holes";
            case StockRule.EveryDeclarationHasATextLabel: return "every-declaration-has-a-text-label";
            case StockRule.EveryKindIsInterpretable: return "every-kind-is-interpretable";
            case StockRule.PayloadMatchesTheSchema: return "payload-matches-the-schema";
            case StockRule.SchemaDeclaresOnlyWhatIsUsed: return "schema-declares-only-what-is-used";
            case StockRule.EdgeDataMatchesTheRelation: return "edge-data-matches-the-relation";
            default: return "unknown-rule";
        }
    }

    /// <summary>
    /// Runs the selected rules and returns the number of diagnostics written, setting each rule’s
    /// name and severity.
    /// </summary>
    public static int Run(
        in RuleSet set,
        in Source src,
        in KindTable table,
        ref RuleViolation[] into)
    {
        if (into == null)
        {
            into = new RuleViolation[8];
        }

        int found = 0;
        for (int i = 0; i < set.Count; i++)
        {
            int before = found;
            found = Check(set.Rules[i].Which, in src, in table, ref into, found);

            for (int v = before; v < found; v++)
            {
                into[v].Rule = NameOf(set.Rules[i].Which);
                into[v].Severity = set.Rules[i].Severity;
            }
        }
        return found;
    }

    /// <summary>Runs one rule and returns the updated diagnostic count.</summary>
    public static int Check(
        StockRule which,
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        switch (which)
        {
            case StockRule.NoDanglingReferences:
                return StockRules.NoDanglingReferences(in src, in table, ref into, found);
            case StockRule.NoHoles:
                return StockRules.NoHoles(in src, in table, ref into, found);
            case StockRule.EveryDeclarationHasATextLabel:
                return StockRules.EveryDeclarationHasATextLabel(in src, in table, ref into, found);
            case StockRule.EveryKindIsInterpretable:
                return StockRules.EveryKindIsInterpretable(in src, in table, ref into, found);
            case StockRule.PayloadMatchesTheSchema:
                return StockRules.PayloadMatchesTheSchema(in src, in table, ref into, found);
            case StockRule.SchemaDeclaresOnlyWhatIsUsed:
                return StockRules.SchemaDeclaresOnlyWhatIsUsed(in src, in table, ref into, found);
            case StockRule.EdgeDataMatchesTheRelation:
                return StockRules.EdgeDataMatchesTheRelation(in src, in table, ref into, found);
            default:
                return found;
        }
    }

    /// <summary>Whether anything found would stop a change entering shared history.</summary>
    public static bool Blocks(RuleViolation[] violations, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (violations[i].Severity == Severity.Block)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Report against a node — or against the source, when the id is none. The two are one
    /// call because "nothing narrower is to blame" is exactly what a missing node meant
    /// before there was a way to say it.
    /// </summary>
    public static int Report(
        ref RuleViolation[] into,
        int found,
        NodeId node,
        string message)
    {
        found = Grow(ref into, found);
        into[found].Subject = node.IsNone ? SubjectKind.Source : SubjectKind.Node;
        into[found].Node = node;
        into[found].Message = message;
        return found + 1;
    }

    /// <summary>Report against one of a node's reference edges.</summary>
    public static int ReportReference(
        ref RuleViolation[] into,
        int found,
        NodeId node,
        int refIndex,
        string message)
    {
        found = Grow(ref into, found);
        into[found].Subject = SubjectKind.Reference;
        into[found].Node = node;
        into[found].RefIndex = refIndex;
        into[found].Message = message;
        return found + 1;
    }

    /// <summary>Report against a kind. No node is to blame — that is the finding.</summary>
    public static int ReportKind(
        ref RuleViolation[] into,
        int found,
        KindId kind,
        string message)
    {
        found = Grow(ref into, found);
        into[found].Subject = SubjectKind.Kind;
        into[found].Node = NodeId.None;
        into[found].Kind = kind;
        into[found].Message = message;
        return found + 1;
    }

    /// <summary>Report against a relation.</summary>
    public static int ReportRelation(
        ref RuleViolation[] into,
        int found,
        RelationId relation,
        string message)
    {
        found = Grow(ref into, found);
        into[found].Subject = SubjectKind.Relation;
        into[found].Node = NodeId.None;
        into[found].Relation = relation;
        into[found].Message = message;
        return found + 1;
    }

    /// <summary>
    /// The subject, as something to print. Durable names where there are any, because a
    /// handle is meaningful only within one load and a violation may outlive it.
    /// </summary>
    public static string Describe(in RuleViolation violation, in KindTable table)
    {
        switch (violation.Subject)
        {
            case SubjectKind.Node:
                return violation.Node.ToString();

            case SubjectKind.Reference:
                return violation.Node.ToString() + "#" + violation.RefIndex.ToString();

            case SubjectKind.Kind:
            {
                QualifiedName name = KindTable.NameOf(in table, violation.Kind);
                return name.IsNone ? violation.Kind.ToString() : name.Name;
            }

            case SubjectKind.Relation:
            {
                QualifiedName name = KindTable.RelationNameOf(in table, violation.Relation);
                return name.IsNone ? violation.Relation.ToString() : name.Name;
            }

            default:
                return "source";
        }
    }

    static int Grow(ref RuleViolation[] into, int found)
    {
        if (found == into.Length)
        {
            Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
        }

        // Slots are reused across runs, so a subject left over from a previous finding
        // would read as this one's.
        into[found] = default;
        return found;
    }
}

/// <summary>Generic diagnostic queries, independent of domain libraries. Callers select which rules to run.</summary>
public static class StockRules
{
    /// <summary>
    /// Reports bound references whose targets are unavailable. Does not reject or repair the
    /// source.
    /// </summary>
    public static int NoDanglingReferences(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
            for (int r = 0; r < refs.Length; r++)
            {
                if (refs[r].IsUnbound)
                {
                    continue;   // a hole is a different rule
                }
                if (!src.IsLive(refs[r].Target))
                {
                    found = RuleSet.ReportReference(ref into, found, node, r,
                        "references " + refs[r].Target + ", which no longer exists");
                }
            }
        }
        return found;
    }

    /// <summary>Reports unbound references. Structurally valid sources may contain holes.</summary>
    public static int NoHoles(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
            for (int r = 0; r < refs.Length; r++)
            {
                if (!refs[r].IsUnbound)
                {
                    continue;
                }

                ReadOnlySpan<byte> label = SourceWalk.Label(in src, in refs[r]);
                found = RuleSet.ReportReference(ref into, found, node, r,
                    label.Length > 0
                        ? "unfilled hole: \"" + System.Text.Encoding.UTF8.GetString(label) + "\""
                        : "unfilled hole");
            }
        }
        return found;
    }

    /// <summary>Reports empty UTF-8 labels on declarations. Declarations with non-text payloads are exempt.</summary>
    public static int EveryDeclarationHasATextLabel(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            KindSchema schema = KindTable.SchemaOf(in table, src.Nodes[i].Kind);
            if (!schema.IsDeclaration)
            {
                continue;
            }

            // Non-text declarations do not claim a text label.
            if (schema.Payload != PayloadFormat.Utf8)
            {
                continue;
            }

            if (SourceWalk.Payload(in src, node).Length == 0)
            {
                found = RuleSet.Report(ref into, found, node,
                    "declaration carries no text label");
            }
        }
        return found;
    }

    /// <summary>Reports live nodes whose kind has no defined schema.</summary>
    public static int EveryKindIsInterpretable(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            if (!KindTable.HasSchema(in table, src.Nodes[i].Kind))
            {
                QualifiedName name = KindTable.NameOf(in table, src.Nodes[i].Kind);
                found = RuleSet.Report(ref into, found, node,
                    "no embedded schema for " + (name.IsNone ? "an unnamed kind" : name.ToString()));
            }
        }
        return found;
    }

    /// <summary>
    /// Reports payloads that disagree with declared formats. This is separate from structural
    /// validation so malformed payloads can still be preserved.
    /// </summary>
    public static int PayloadMatchesTheSchema(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || !KindTable.HasSchema(in table, src.Nodes[i].Kind))
            {
                continue;
            }

            KindSchema schema = KindTable.SchemaOf(in table, src.Nodes[i].Kind);
            ReadOnlySpan<byte> payload = SourceWalk.Payload(in src, node);

            // Empty payloads represent incomplete content and are not reported by this rule.
            if (payload.Length == 0 && schema.Payload != PayloadFormat.None)
            {
                continue;
            }

            string wrong = Disagreement(schema.Payload, in schema.Stride, payload);
            if (wrong == null)
            {
                continue;
            }

            QualifiedName name = KindTable.NameOf(in table, src.Nodes[i].Kind);
            found = RuleSet.Report(ref into, found, node,
                (name.IsNone ? "an unnamed kind" : name.Name) + " " + wrong);
        }
        return found;
    }

    /// <summary>
    /// Checks bound edge data against its relation schema. Unbound edges carry unresolved labels
    /// and are exempt.
    /// </summary>
    public static int EdgeDataMatchesTheRelation(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
            for (int r = 0; r < refs.Length; r++)
            {
                if (!refs[r].IsBound)
                {
                    continue;
                }

                RelationSchema schema = KindTable.RelationSchemaOf(in table, refs[r].Relation);
                ReadOnlySpan<byte> data = SourceWalk.EdgeBytes(in src, in refs[r]);

                // None provides no edge-data format to check.
                if (schema.Payload == PayloadFormat.None)
                {
                    continue;
                }

                string wrong = Disagreement(schema.Payload, in schema.Stride, data);
                if (wrong == null)
                {
                    continue;
                }

                QualifiedName name = table.RelationNames[refs[r].Relation.Value];
                found = RuleSet.ReportReference(ref into, found, node, r,
                    (name.IsNone ? "an unnamed relation" : name.Name) + " " + wrong);
            }
        }
        return found;
    }

    /// <summary>
    /// Returns a format mismatch description, or null. Opaque payloads have no format constraints
    /// to check.
    /// </summary>
    static string Disagreement(PayloadFormat format, in PayloadStride stride, ReadOnlySpan<byte> payload)
    {
        switch (format)
        {
            case PayloadFormat.None:
                // The asymmetric case: something wrote bytes and nothing can read them.
                return payload.Length == 0
                    ? null
                    : "declares no payload and carries " + payload.Length + " byte(s)";

            case PayloadFormat.Utf8:
                return System.Text.Unicode.Utf8.IsValid(payload)
                    ? null
                    : "declares utf-8 and carries bytes that are not";

            case PayloadFormat.Int64:
            case PayloadFormat.Float64:
                return payload.Length == 8
                    ? null
                    : "declares a 64-bit value and carries " + payload.Length + " byte(s)";

            case PayloadFormat.Bool:
                if (payload.Length != 1)
                {
                    return "declares a bool and carries " + payload.Length + " byte(s)";
                }
                // Boolean payloads must be a single byte containing 0 or 1.
                return payload[0] <= 1 ? null : "declares a bool and carries " + payload[0];

            case PayloadFormat.Record:
                return RecordDisagreement(in stride, payload);

            default:
                return null;
        }
    }

    /// <summary>
    /// Checks declared record strides for complete elements. Records without a stride have no
    /// generic layout constraint.
    /// </summary>
    static string RecordDisagreement(in PayloadStride stride, ReadOnlySpan<byte> payload)
    {
        if (!stride.IsArray)
        {
            return null;
        }

        if (payload.Length < stride.Header)
        {
            return "declares a " + stride.Header + "-byte header and carries only "
                + payload.Length + " byte(s)";
        }

        int body = payload.Length - stride.Header;
        if (body % stride.Unit == 0)
        {
            return null;
        }

        return "declares " + stride.Unit + "-byte elements and carries " + body
            + " byte(s) after its header, which is not a whole number of them";
    }

    /// <summary>
    /// Reports unused embedded schemas. Counts deleted slots as users and retains bootstrap
    /// declarations.
    /// </summary>
    public static int SchemaDeclaresOnlyWhatIsUsed(
        in Source src,
        in KindTable table,
        ref RuleViolation[] into,
        int found)
    {
        bool[] usedKind = new bool[table.KindCount];
        bool[] usedRelation = new bool[table.RelationCount];

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

        // Relations declared by used schemas count as used, even without edges.
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

        // Bootstrap declarations are retained even when no content node uses them.
        Mark(usedKind, KindTable.Lookup(in table, KindBootstrap.SchemaSetName));
        Mark(usedKind, KindTable.Lookup(in table, KindBootstrap.KindSchemaName));
        Mark(usedKind, KindTable.Lookup(in table, KindBootstrap.RelationDeclName));
        Mark(usedKind, KindTable.Lookup(in table, KindBootstrap.HoleName));

        for (int i = 0; i < table.KindCount; i++)
        {
            if (usedKind[i] || !table.SchemaDefined[i] || table.KindNames[i].IsNone)
            {
                continue;
            }

            found = RuleSet.ReportKind(ref into, found, new KindId(i),
                "the schema declares " + table.KindNames[i].Name + " and no node is one");
        }

        for (int i = 0; i < table.RelationCount; i++)
        {
            if (usedRelation[i] || table.RelationNames[i].IsNone)
            {
                continue;
            }

            found = RuleSet.ReportRelation(ref into, found, new RelationId(i),
                "the schema declares the relation " + table.RelationNames[i].Name + " and no edge has it");
        }

        return found;
    }

    static void Mark(bool[] into, KindId kind)
    {
        if (!kind.IsUnknown && kind.Value >= 0 && kind.Value < into.Length)
        {
            into[kind.Value] = true;
        }
    }
}
