using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Rules;
using Supersource.Timeline;

namespace Supersource.Cli;
/// <summary>Apply a checked batch to a loaded source before attempting to save it.</summary>
public static class Edit
{
    public static int Run(string[] args)
    {
        bool dryRun = false;
        string expected = null;
        if (args.Length < 3) return Usage();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--dry-run" && !dryRun) dryRun = true;
            else if (args[i] == "--expect-hash" && expected == null && i + 1 < args.Length)
            {
                expected = args[++i];
                if (expected.Length != 64) return Usage();
                for (int c = 0; c < expected.Length; c++)
                    if (!Uri.IsHexDigit(expected[c])) return Usage();
            }
            else return Usage();
        }

        SourceFile file = default;
        try
        {
            using JsonDocument patch = JsonDocument.Parse(File.ReadAllBytes(args[2]));
            if (patch.RootElement.ValueKind != JsonValueKind.Array || patch.RootElement.GetArrayLength() == 0)
                throw new ArgumentException("edits must be a nonempty JSON array");

            string before = ContentHash.OfFile(args[1]).ToString();
            if (expected != null && !string.Equals(before, expected, StringComparison.OrdinalIgnoreCase))
                return Refuse("file hash differs from --expect-hash; inspect the current file before retrying");
            file = SourceRead.FromFile(args[1]);
            CheckStructure(in file);
            var aliases = new Dictionary<string, NodeId>(StringComparer.Ordinal);
            var touched = new HashSet<NodeId>();
            var report = new StringBuilder();
            int step = 0;
            foreach (JsonElement item in patch.RootElement.EnumerateArray())
            {
                step++;
                try
                {
                    Operation op = ReadOperation(item, in file, aliases, out string alias);
                    Log.Do(ref file.History, ref file.Src, op);
                    Operation applied = file.History.Entries[file.History.At - 1].Op;
                    if (alias != null) aliases.Add(alias, applied.Node);
                    touched.Add(applied.Node);
                    report.Append(step).Append("  ").Append(applied.Kind).Append("  ").Append(applied.Node);
                    if (alias != null) report.Append("  @").Append(alias);
                    if (applied.Kind == OpKind.Connect || applied.Kind == OpKind.Disconnect)
                        report.Append("  reference ").Append(applied.Index);
                    report.AppendLine();
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException
                    || error is FormatException || error is FormatCorruptException)
                {
                    throw new ArgumentException("edit " + step + ": " + error.Message);
                }
            }

            CheckStructure(in file);
            CheckPayloads(in file, touched);
            if (ContentHash.OfFile(args[1]).ToString() != before)
                return Refuse("file changed while preparing edits; no save attempted");
            Console.WriteLine("source " + file.Src.Identity);
            Console.WriteLine("input hash " + before);
            Console.Write(report.ToString());
            if (dryRun)
            {
                Console.WriteLine("dry run: " + step + " edit(s) checked; file unchanged");
                return 0;
            }
            if (File.Exists(args[1] + ".writing"))
                return Refuse("a .writing sibling already exists; preserve it before retrying");
            if (!SourceWrite.Replace(ref file, args[1], out string refusal)) return Refuse(refusal);
            Console.WriteLine("saved " + step + " edit(s)");
            return 0;
        }
        catch (Exception error) when (error is JsonException || error is ArgumentException
            || error is FormatException || error is InvalidOperationException)
        {
            Console.Error.WriteLine("edit rejected: " + error.Message);
            return 1;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("edit I/O error: " + error.Message);
            return 2;
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: edit <file> <edits.json> [--dry-run] [--expect-hash <sha256>]");
        return 1;
    }

    static int Refuse(string reason)
    {
        Console.Error.WriteLine("edit refused: " + reason);
        return 3;
    }

    static void CheckStructure(in SourceFile file)
    {
        if (!SourceValidate.Check(in file, out Problem[] problems))
            throw new ArgumentException("structural validation failed: " + problems[0].Kind + " " + problems[0].Node);
    }

    static void CheckPayloads(in SourceFile file, HashSet<NodeId> touched)
    {
        RuleViolation[] found = new RuleViolation[8];
        int count = StockRules.PayloadMatchesTheSchema(in file.Src, in file.Table, ref found, 0);
        count = StockRules.EdgeDataMatchesTheRelation(in file.Src, in file.Table, ref found, count);
        for (int i = 0; i < count; i++)
            if (touched.Contains(found[i].Node))
                throw new ArgumentException(found[i].Node + ": " + found[i].Message);
    }

    static Operation ReadOperation(JsonElement item, in SourceFile file,
        Dictionary<string, NodeId> aliases, out string alias)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("each edit must be an object");
        string verb = Text(item, "op");
        Operation op = default;
        alias = null;
        switch (verb)
        {
            case "insert":
                Fields(item, "op", "parent", "index", "kind", "text", "base64", "as");
                op.Kind = OpKind.Insert;
                op.Parent = Node(item, "parent", in file, aliases, true);
                op.Index = Index(item, "index", SourceWalk.ChildCount(in file.Src, op.Parent));
                Bound(op.Index, SourceWalk.ChildCount(in file.Src, op.Parent), "child index");
                op.NodeKind = FindKind(in file, Text(item, "kind"));
                op.Value = Payload(item, false);
                CheckText(item, in file, op.NodeKind);
                if (item.TryGetProperty("as", out _))
                {
                    alias = Text(item, "as");
                    if (alias.Length == 0 || aliases.ContainsKey(alias))
                        throw new ArgumentException("insert handle must be nonempty and unique");
                }
                break;
            case "set-payload":
            case "rename":
            case "splice":
                if (verb == "splice") Fields(item, "op", "node", "offset", "remove", "text", "base64");
                else Fields(item, "op", "node", "text", "base64");
                op.Node = Node(item, "node", in file, aliases, true);
                op.Kind = verb == "splice" ? OpKind.Splice : verb == "rename" ? OpKind.Rename : OpKind.ModifyValue;
                op.Value = Payload(item, true);
                KindId kind = file.Src.Nodes[op.Node.Index].Kind;
                CheckText(item, in file, kind);
                if (verb == "splice")
                {
                    op.Index = Index(item, "offset");
                    op.Count = Index(item, "remove");
                    ReadOnlySpan<byte> bytes = SourceWalk.Payload(in file.Src, op.Node);
                    Bound(op.Index, bytes.Length, "byte offset");
                    Bound(op.Count, bytes.Length - op.Index, "bytes removed");
                    if (KindTable.SchemaOf(in file.Table, kind).Payload == PayloadFormat.Utf8
                        && (!TextBoundary(bytes, op.Index) || !TextBoundary(bytes, op.Index + op.Count)))
                        throw new ArgumentException("splice splits a UTF-8 code point");
                }
                break;
            case "move":
                Fields(item, "op", "node", "parent", "index");
                op.Kind = OpKind.Move;
                op.Node = Node(item, "node", in file, aliases, true);
                NotRoot(in file, op.Node);
                op.Parent = Node(item, "parent", in file, aliases, true);
                int children = SourceWalk.ChildCount(in file.Src, op.Parent);
                if (file.Src.Nodes[op.Node.Index].Parent == op.Parent) children--;
                op.Index = Index(item, "index", children);
                Bound(op.Index, children, "child index after removal");
                break;
            case "delete":
                Fields(item, "op", "node");
                op.Kind = OpKind.Delete;
                op.Node = Node(item, "node", in file, aliases, true);
                NotRoot(in file, op.Node);
                // Deleting an ancestor also deletes its embedded schemas or generated nodes.
                for (long i = 0; i < file.Src.NodeCount; i++)
                {
                    NodeId descendant = new NodeId(i);
                    if (!file.Src.IsLive(descendant)) continue;
                    for (NodeId at = descendant; at.IsSome; at = file.Src.Nodes[at.Index].Parent)
                        if (at == op.Node) { Editable(in file, descendant); break; }
                }
                break;
            case "connect":
                Fields(item, "op", "node", "relation", "target", "index", "text", "base64");
                op.Kind = OpKind.Connect;
                op.Node = Node(item, "node", in file, aliases, true);
                op.Relation = FindRelation(in file, Text(item, "relation"));
                if (!item.TryGetProperty("target", out JsonElement target))
                    throw new ArgumentException("target is required; use null for an unbound reference");
                op.Target = target.ValueKind == JsonValueKind.Null ? NodeId.None
                    : Node(item, "target", in file, aliases, false);
                op.Index = Index(item, "index", file.Src.Nodes[op.Node.Index].RefCount);
                Bound(op.Index, file.Src.Nodes[op.Node.Index].RefCount, "reference index");
                op.Value = Payload(item, false);
                break;
            case "disconnect":
                Fields(item, "op", "node", "index");
                op.Kind = OpKind.Disconnect;
                op.Node = Node(item, "node", in file, aliases, true);
                op.Index = Index(item, "index");
                Bound(op.Index, file.Src.Nodes[op.Node.Index].RefCount - 1, "reference index");
                break;
            default: throw new ArgumentException("unknown edit operation: " + verb);
        }
        return op;
    }

    static bool TextBoundary(ReadOnlySpan<byte> bytes, int at)
        => at == 0 || at == bytes.Length || (bytes[at] & 0xc0) != 0x80;

    static void NotRoot(in SourceFile file, NodeId node)
    {
        if (file.Src.Root == node) throw new ArgumentException("cannot delete or move the source root");
    }

    static void Editable(in SourceFile file, NodeId node)
    {
        KindId kind = file.Src.Nodes[node.Index].Kind;
        ContentKind(in file, kind);
    }

    static void ContentKind(in SourceFile file, KindId kind)
    {
        if (kind == file.Boot.SchemaSet || kind == file.Boot.KindSchema || kind == file.Boot.RelationDecl)
            throw new ArgumentException("edit does not modify embedded schema definitions");
    }

    static NodeId Node(JsonElement item, string field, in SourceFile file,
        Dictionary<string, NodeId> aliases, bool editable)
    {
        string text = Text(item, field);
        NodeId node;
        if (text.StartsWith("@", StringComparison.Ordinal))
        {
            if (!aliases.TryGetValue(text.Substring(1), out node))
                throw new ArgumentException("unknown insert handle: " + text);
        }
        else node = Program.ParseNode(text);
        if (!file.Src.IsLive(node)) throw new ArgumentException("no live node " + text);
        if (editable) Editable(in file, node);
        return node;
    }

    static void CheckText(JsonElement item, in SourceFile file, KindId kind)
    {
        if (item.TryGetProperty("text", out _)
            && (!KindTable.HasSchema(in file.Table, kind)
                || KindTable.SchemaOf(in file.Table, kind).Payload != PayloadFormat.Utf8))
            throw new ArgumentException("text requires a kind with a UTF-8 payload; use base64 for other formats");
    }

    static KindId FindKind(in SourceFile file, string name)
    {
        int found = -1;
        for (int i = 0; i < file.Table.KindCount; i++)
        {
            QualifiedName candidate = KindTable.NameOf(in file.Table, new KindId(i));
            if (!Matches(candidate, name)) continue;
            if (found >= 0) throw new ArgumentException("ambiguous kind; use library-id/name: " + name);
            found = i;
        }
        if (found < 0 || !KindTable.HasSchema(in file.Table, new KindId(found)))
            throw new ArgumentException("kind needs an existing embedded schema: " + name);
        KindId kind = new KindId(found);
        ContentKind(in file, kind);
        return kind;
    }

    static RelationId FindRelation(in SourceFile file, string name)
    {
        int found = -1;
        for (int i = 0; i < file.Table.RelationCount; i++)
        {
            if (!Matches(KindTable.RelationNameOf(in file.Table, new RelationId(i)), name)) continue;
            if (found >= 0) throw new ArgumentException("ambiguous relation; use library-id/name: " + name);
            found = i;
        }
        if (found < 0) throw new ArgumentException("unknown relation: " + name);
        return new RelationId(found);
    }

    static bool Matches(QualifiedName name, string requested)
        => !name.IsNone && (name.Name == requested || name.ToString() == requested);

    static byte[] Payload(JsonElement item, bool required)
    {
        bool text = item.TryGetProperty("text", out _);
        bool encoded = item.TryGetProperty("base64", out _);
        if (text && encoded) throw new ArgumentException("use either text or base64, not both");
        if (text) return new UTF8Encoding(false, true).GetBytes(Text(item, "text"));
        if (encoded) return Convert.FromBase64String(Text(item, "base64"));
        if (required) throw new ArgumentException("text or base64 is required; use an empty string to clear payload");
        return Array.Empty<byte>();
    }

    static string Text(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(field + " must be a string");
        return value.GetString();
    }

    static int Index(JsonElement item, string field, int fallback = -1)
    {
        if (!item.TryGetProperty(field, out JsonElement value))
        {
            if (fallback >= 0) return fallback;
            throw new ArgumentException(field + " is required");
        }
        if (!value.TryGetInt32(out int number) || number < 0)
            throw new ArgumentException(field + " must be a nonnegative 32-bit integer");
        return number;
    }

    static void Bound(int value, int maximum, string label)
    {
        if (value < 0 || value > maximum) throw new ArgumentException(label + " is out of range");
    }

    static void Fields(JsonElement item, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("duplicate field: " + property.Name);
            bool found = false;
            for (int i = 0; i < allowed.Length; i++) if (allowed[i] == property.Name) { found = true; break; }
            if (!found) throw new ArgumentException("unknown field: " + property.Name);
        }
    }
}
