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
}

/// <summary>
/// One finding. The subject is a small closed union rather than a node, because a rule
/// that cannot name what is wrong is a rule a caller cannot act on.
/// </summary>
public struct RuleViolation
{
    public string Rule;
    public Severity Severity;

    /// <summary>Which of the fields below carries the subject.</summary>
    public SubjectKind Subject;

    /// <summary>The node, for Node and Reference.</summary>
    public NodeId Node;

    /// <summary>Which of <see cref="Node"/>'s edges, for Reference.</summary>
    public int RefIndex;

    public string Message;
}

/// <summary>Opt-in checks over the whole source, independent of domain libraries.</summary>
public enum StockRule : byte
{
    NoDanglingReferences = 0,
    NoUnboundReferences = 1,
    // 2 is retired: a rule about declarations needs to know what a declaration is, which is a
    // language's knowledge, not the format's.
    EveryKindIsInterpretable = 3,
    PayloadMatchesTheSchema = 4,
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
            case StockRule.NoUnboundReferences: return "no-unbound-references";
            case StockRule.EveryKindIsInterpretable: return "every-kind-is-interpretable";
            case StockRule.PayloadMatchesTheSchema: return "payload-matches-the-schema";
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
            case StockRule.NoUnboundReferences:
                return StockRules.NoUnboundReferences(in src, in table, ref into, found);
            case StockRule.EveryKindIsInterpretable:
                return StockRules.EveryKindIsInterpretable(in src, in table, ref into, found);
            case StockRule.PayloadMatchesTheSchema:
                return StockRules.PayloadMatchesTheSchema(in src, in table, ref into, found);
            case StockRule.EdgeDataMatchesTheRelation:
                return StockRules.EdgeDataMatchesTheRelation(in src, in table, ref into, found);
            default:
                return found;
        }
    }

    /// <summary>Whether anything found is severe enough that the change carrying it should not stand.</summary>
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
    /// Report against a node, or against the source when the id is none: no node means nothing
    /// narrower is to blame.
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

            default:
                return "source";
        }
    }

    static int Grow(ref RuleViolation[] into, int found)
    {
        into ??= new RuleViolation[8];
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
                    continue;   // an unbound reference is a different rule
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

    /// <summary>Reports unbound references. A structurally valid source may hold them.</summary>
    public static int NoUnboundReferences(
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
                        ? "unbound: \"" + System.Text.Encoding.UTF8.GetString(label) + "\""
                        : "unbound");
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
            long length = SourceWalk.PayloadLength(in src, node);

            // Empty payloads represent incomplete content and are not reported by this rule.
            if (length == 0 && schema.Payload != PayloadFormat.None)
            {
                continue;
            }

            string wrong = SourceWalk.IsContiguous(in src, node)
                ? Disagreement(in schema.Payload, SourceWalk.Payload(in src, node))
                : Disagreement(in schema.Payload, in src, node, length);
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
    /// Checks bound edge data against its relation schema. Unbound edges carry labels instead, and
    /// are exempt.
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
                ReadOnlySpan<byte> data = SourceWalk.EdgeData(in src, in refs[r]);

                // None provides no edge-data format to check.
                if (schema.Payload == PayloadFormat.None)
                {
                    continue;
                }

                string wrong = Disagreement(in schema.Payload, data);
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
    /// Returns a format mismatch description, or null. Opaque payloads have no format to check.
    /// </summary>
    static string Disagreement(in PayloadFormat format, ReadOnlySpan<byte> payload)
    {
        switch (format.Form)
        {
            case PayloadForm.None:
                // The asymmetric case: something wrote bytes and nothing can read them.
                return payload.Length == 0
                    ? null
                    : "declares no payload and carries " + payload.Length + " byte(s)";

            case PayloadForm.Text:
            {
                int header = format.HeaderSize;
                if (payload.Length < header)
                {
                    return "declares a " + header + "-byte header and carries only " + payload.Length + " byte(s)";
                }
                string wrong = ElementDisagreement(in format.Header, payload.Slice(0, header));
                if (wrong != null)
                {
                    return "in its header, " + wrong;
                }
                return System.Text.Unicode.Utf8.IsValid(payload.Slice(header))
                    ? null
                    : "declares text and carries bytes that are not utf-8";
            }

            case PayloadForm.One:
                if (payload.Length != format.Element.Size)
                {
                    return "declares one " + format + " and carries " + payload.Length + " byte(s)";
                }
                return ElementDisagreement(in format.Element, payload);

            case PayloadForm.Many:
                return ManyDisagreement(in format, payload);

            default:
                return null;
        }
    }

    /// <summary>
    /// The same checks over a payload too large for one span, read in windows: lengths by
    /// arithmetic, text as one stream of UTF-8, elements a whole number of them at a time.
    /// </summary>
    static string Disagreement(in PayloadFormat format, in Source src, NodeId node, long length)
    {
        switch (format.Form)
        {
            case PayloadForm.None:
                return "declares no payload and carries " + length + " byte(s)";

            case PayloadForm.One:
                return "declares one " + format + " and carries " + length + " byte(s)";

            case PayloadForm.Text:
            {
                int header = format.HeaderSize;
                if (length < header)
                {
                    return "declares a " + header + "-byte header and carries only " + length + " byte(s)";
                }
                byte[] head = new byte[header];
                SourceWalk.ReadPayload(in src, node, 0, head);
                string wrong = ElementDisagreement(in format.Header, head);
                if (wrong != null)
                {
                    return "in its header, " + wrong;
                }

                System.Text.Decoder utf8 = new System.Text.UTF8Encoding(false, true).GetDecoder();
                byte[] window = new byte[(int)Math.Min(length - header, SourceWalk.Window)];
                try
                {
                    for (long at = header; at < length; at += window.Length)
                    {
                        int count = (int)Math.Min(window.Length, length - at);
                        SourceWalk.ReadPayload(in src, node, at, new Span<byte>(window, 0, count));
                        utf8.GetCharCount(window, 0, count, at + count == length);
                    }
                }
                catch (System.Text.DecoderFallbackException)
                {
                    return "declares text and carries bytes that are not utf-8";
                }
                return null;
            }

            case PayloadForm.Many:
            {
                int header = format.ElementsStart;
                if (length < header)
                {
                    return "declares a " + header + "-byte header and carries only " + length + " byte(s)";
                }
                byte[] head = new byte[header];
                SourceWalk.ReadPayload(in src, node, 0, head);
                string wrong = ElementDisagreement(in format.Header, head);
                if (wrong != null)
                {
                    return "in its header, " + wrong;
                }

                int size = format.Element.Size;
                if (size <= 0)
                {
                    return null;
                }
                long body = length - header;
                if (body % size != 0)
                {
                    return "declares " + size + "-byte elements and carries " + body
                        + " byte(s) after its header, which is not a whole number of them";
                }

                int perWindow = Math.Max(1, SourceWalk.Window / size);
                byte[] window = new byte[perWindow * size];
                for (long at = header; at < length; at += window.Length)
                {
                    int count = (int)Math.Min(window.Length, length - at);
                    SourceWalk.ReadPayload(in src, node, at, new Span<byte>(window, 0, count));
                    for (int e = 0; e < count; e += size)
                    {
                        wrong = ElementDisagreement(in format.Element, new ReadOnlySpan<byte>(window, e, size));
                        if (wrong != null)
                        {
                            return "in element " + ((at - header + e) / size) + ", " + wrong;
                        }
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>A header, then whole elements, each of whose booleans is 0 or 1.</summary>
    static string ManyDisagreement(in PayloadFormat format, ReadOnlySpan<byte> payload)
    {
        int header = format.ElementsStart;
        if (payload.Length < header)
        {
            return "declares a " + header + "-byte header and carries only " + payload.Length + " byte(s)";
        }

        string wrong = ElementDisagreement(in format.Header, payload.Slice(0, header));
        if (wrong != null)
        {
            return "in its header, " + wrong;
        }

        int size = format.Element.Size;
        if (size <= 0)
        {
            return null;
        }

        int body = payload.Length - header;
        if (body % size != 0)
        {
            return "declares " + size + "-byte elements and carries " + body
                + " byte(s) after its header, which is not a whole number of them";
        }

        for (int at = header; at < payload.Length; at += size)
        {
            wrong = ElementDisagreement(in format.Element, payload.Slice(at, size));
            if (wrong != null)
            {
                return "in element " + ((at - header) / size) + ", " + wrong;
            }
        }
        return null;
    }

    /// <summary>The one value a field's type rules out by itself: a boolean that is not 0 or 1.</summary>
    static string ElementDisagreement(in PayloadElement element, ReadOnlySpan<byte> bytes)
    {
        if (element.Fields == null)
        {
            return null;
        }

        for (int f = 0; f < element.Fields.Length; f++)
        {
            PayloadField field = element.Fields[f];
            if (field.Type != PayloadScalar.Bool)
            {
                continue;
            }
            for (int n = 0; n < field.Repeat; n++)
            {
                int at = field.Offset + n;
                if (at < bytes.Length && bytes[at] > 1)
                {
                    string name = string.IsNullOrEmpty(field.Name) ? "a bool" : field.Name;
                    return name + " is " + bytes[at] + ", which is not a bool";
                }
            }
        }
        return null;
    }

}
