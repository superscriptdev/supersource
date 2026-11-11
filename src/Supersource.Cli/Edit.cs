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
        string library = null;
        if (args.Length < 3) return Usage();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--dry-run" && !dryRun) dryRun = true;
            else if (args[i] == "--kinds" && library == null && i + 1 < args.Length)
            {
                library = args[++i];
            }
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
        SourceFile seed = default;
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

            // `--kinds` seeds kind names from another source so the batch can name a kind the
            // source has never used. The schemas of the kinds it used are embedded before the
            // save (§4.5).
            if (library != null)
            {
                if (!File.Exists(library))
                {
                    throw new ArgumentException("no kind source at " + library);
                }

                seed = SourceRead.FromFile(library);
                Declaring.Seed(ref file.Table, in seed.Table);
            }
            var aliases = new Dictionary<string, NodeId>(StringComparer.Ordinal);
            var touched = new HashSet<NodeId>();
            var report = new StringBuilder();
            int step = 0;
            foreach (JsonElement item in patch.RootElement.EnumerateArray())
            {
                step++;
                try
                {
                    Operation op = ReadOperation(item, ref file, aliases, out string alias, out External outward);
                    bool isExtern = outward.Node >= 0;
                    if (isExtern)
                    {
                        Extern(ref file, ref op, in outward);
                    }
                    else
                    {
                        Log.Do(ref file.Log, ref file.Src, op);
                    }

                    // An extern's handle names its external reference, not the edge after it.
                    Operation applied = isExtern ? op : file.Log.Entries[file.Log.At - 1].Op;
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
            SourceFile.Close(ref seed);
            SourceFile.Close(ref file);
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine(
            "usage: edit <file> <edits.json> [--kinds <source.ss>] [--dry-run] "
            + "[--expect-hash <sha256>]");
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

    static Operation ReadOperation(JsonElement item, ref SourceFile file,
        Dictionary<string, NodeId> aliases, out string alias, out External outward)
    {
        outward = default;
        outward.Node = -1;
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("each edit must be an object");
        string verb = Text(item, "op");
        Operation op = default;
        alias = null;
        switch (verb)
        {
            case "insert":
                Fields(item, "op", "parent", "index", "kind", "text", "base64", "as");
                op.Kind = OpKind.Insert;
                op.Parent = Node(item, "parent", in file, aliases);
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
            case "splice":
                if (verb == "splice") Fields(item, "op", "node", "offset", "remove", "text", "base64");
                else Fields(item, "op", "node", "text", "base64");
                op.Node = Node(item, "node", in file, aliases);
                op.Kind = verb == "splice" ? OpKind.Splice : OpKind.ModifyValue;
                op.Value = Payload(item, true);
                KindId kind = file.Src.Nodes[op.Node.Index].Kind;
                CheckText(item, in file, kind);
                if (verb == "splice")
                {
                    op.Index = Offset(item, "offset");
                    op.Count = Offset(item, "remove");
                    long length = SourceWalk.PayloadLength(in file.Src, op.Node);
                    Bound(op.Index, length, "byte offset");
                    Bound(op.Count, length - op.Index, "bytes removed");
                    if (KindTable.SchemaOf(in file.Table, kind).Payload == PayloadFormat.Text
                        && (!TextBoundary(in file.Src, op.Node, op.Index)
                            || !TextBoundary(in file.Src, op.Node, op.Index + op.Count)))
                        throw new ArgumentException("splice splits a UTF-8 code point");
                }
                break;
            case "move":
                Fields(item, "op", "node", "parent", "index");
                op.Kind = OpKind.Move;
                op.Node = Node(item, "node", in file, aliases);
                NotRoot(in file, op.Node);
                op.Parent = Node(item, "parent", in file, aliases);
                int children = SourceWalk.ChildCount(in file.Src, op.Parent);
                if (file.Src.Nodes[op.Node.Index].Parent == op.Parent) children--;
                op.Index = Index(item, "index", children);
                Bound(op.Index, children, "child index after removal");
                break;
            case "retype":
                Fields(item, "op", "node", "kind");
                op.Kind = OpKind.Retype;
                op.Node = Node(item, "node", in file, aliases);
                op.NodeKind = FindKind(in file, Text(item, "kind"));
                break;
            case "delete":
                Fields(item, "op", "node");
                op.Kind = OpKind.Delete;
                op.Node = Node(item, "node", in file, aliases);
                NotRoot(in file, op.Node);
                break;
            case "connect":
                Fields(item, "op", "node", "relation", "target", "index", "text", "base64");
                op.Kind = OpKind.Connect;
                op.Node = Node(item, "node", in file, aliases);
                op.Relation = FindRelation(in file, Text(item, "relation"));
                if (!item.TryGetProperty("target", out JsonElement target))
                    throw new ArgumentException("target is required; use null for an unbound reference");
                op.Target = target.ValueKind == JsonValueKind.Null ? NodeId.None
                    : Node(item, "target", in file, aliases);
                op.Index = Index(item, "index", file.Src.Nodes[op.Node.Index].RefCount);
                Bound(op.Index, file.Src.Nodes[op.Node.Index].RefCount, "reference index");
                op.Value = Payload(item, false);
                break;
            case "extern":
                // A reference to a node in another source, as a node of its own: §5.1 gives a
                // source an identity, so the reference survives the file moving. Ordinary local
                // edges point at these nodes, so a `calls` or `uses` edge is drawn at the handle
                // this op registers exactly as it would be at a local node. The other source's
                // address goes into its dependency, once, made under the root if it is new.
                Fields(item, "op", "parent", "index", "source", "node", "locator", "hash", "as");
                op.Kind = OpKind.Insert;
                op.Parent = Node(item, "parent", in file, aliases);
                op.Index = Index(item, "index", SourceWalk.ChildCount(in file.Src, op.Parent));
                Bound(op.Index, SourceWalk.ChildCount(in file.Src, op.Parent), "child index");
                op.NodeKind = ExternalKinds.Install(ref file.Table).ExternalRef;
                outward = Outward(item);
                op.Value = ExternalKinds.EncodeNode(outward.Node);
                if (item.TryGetProperty("as", out _))
                {
                    alias = Text(item, "as");
                    if (alias.Length == 0 || aliases.ContainsKey(alias))
                        throw new ArgumentException("extern handle must be nonempty and unique");
                }
                break;
            case "disconnect":
                Fields(item, "op", "node", "index");
                op.Kind = OpKind.Disconnect;
                op.Node = Node(item, "node", in file, aliases);
                op.Index = Index(item, "index");
                Bound(op.Index, file.Src.Nodes[op.Node.Index].RefCount - 1, "reference index");
                break;
            default: throw new ArgumentException("unknown edit operation: " + verb);
        }
        return op;
    }

    /// <summary>
    /// Writes an extern as the operations it is: the external reference; the dependency, under
    /// the root, when this source has none for that address yet; and the reference's `of` edge,
    /// first. Each is logged, so the batch undoes and journals like any other.
    /// </summary>
    static void Extern(ref SourceFile file, ref Operation insert, in External outward)
    {
        ExternalKinds.Ids ids = ExternalKinds.Install(ref file.Table);

        // The reference first, so it takes the id the batch would expect of an insert.
        Log.Do(ref file.Log, ref file.Src, insert);
        insert = file.Log.Entries[file.Log.At - 1].Op;

        NodeId dependency = ExternalKinds.FindDependency(in file.Src, in ids, outward.Source, outward.Hash, outward.Locator);
        if (dependency.IsNone)
        {
            Operation made = default;
            made.Kind = OpKind.Insert;
            made.Parent = file.Src.Root;
            made.Index = SourceWalk.ChildCount(in file.Src, file.Src.Root);
            made.NodeKind = ids.Dependency;
            made.Value = ExternalKinds.EncodeDependency(outward.Source, outward.Hash, outward.Locator);
            Log.Do(ref file.Log, ref file.Src, made);
            dependency = file.Log.Entries[file.Log.At - 1].Op.Node;
        }

        Operation of = default;
        of.Kind = OpKind.Connect;
        of.Node = insert.Node;
        of.Index = 0;
        of.Relation = ids.Of;
        of.Target = dependency;
        Log.Do(ref file.Log, ref file.Src, of);
    }

    /// <summary>
    /// What an external reference points at: a source identity and a node, optionally a locator
    /// a fetcher can find the file by and the content hash of the version it means. A reference
    /// with no hash still resolves; §5.1 allows a pin to be added later.
    /// </summary>
    static External Outward(JsonElement item)
    {
        External outward = default;
        outward.Source = Identity(Text(item, "source"));
        outward.Node = Index(item, "node");
        outward.Locator = item.TryGetProperty("locator", out _) ? Text(item, "locator") : null;

        if (item.TryGetProperty("hash", out _))
        {
            string hash = Text(item, "hash");
            if (hash.Length != 64)
            {
                throw new ArgumentException("hash must be 64 hex digits");
            }

            for (int i = 0; i < hash.Length; i++)
            {
                if (!Uri.IsHexDigit(hash[i]))
                {
                    throw new ArgumentException("hash must be 64 hex digits");
                }
            }

            // Four words, big-endian, as the digest's own accessor describes them.
            ulong[] words = new ulong[4];
            for (int i = 0; i < 64; i++)
            {
                words[i / 16] = (words[i / 16] << 4)
                    | (ulong)Convert.ToInt32(hash[i].ToString(), 16);
            }

            outward.Hash = new ContentHash(words[0], words[1], words[2], words[3]);
        }

        return outward;
    }

    static SourceId Identity(string text)
    {
        if (text.Length != 32)
        {
            throw new ArgumentException("source must be 32 hex digits");
        }

        ulong hi = 0;
        ulong lo = 0;
        for (int i = 0; i < 32; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                throw new ArgumentException("source must be 32 hex digits");
            }

            ulong digit = (ulong)Convert.ToInt32(text[i].ToString(), 16);
            if (i < 16) hi = (hi << 4) | digit;
            else lo = (lo << 4) | digit;
        }

        if (hi == 0 && lo == 0)
        {
            throw new ArgumentException("an unminted source cannot be referred to");
        }

        return new SourceId(hi, lo);
    }

    /// <summary>Whether a byte offset falls between code points, read where it is in a payload of any size.</summary>
    static bool TextBoundary(in Source src, NodeId node, long at)
    {
        if (at == 0 || at == SourceWalk.PayloadLength(in src, node)) return true;
        Span<byte> one = stackalloc byte[1];
        SourceWalk.ReadPayload(in src, node, at, one);
        return (one[0] & 0xc0) != 0x80;
    }

    static void NotRoot(in SourceFile file, NodeId node)
    {
        if (file.Src.Root == node) throw new ArgumentException("cannot delete or move the source root");
    }

    static NodeId Node(JsonElement item, string field, in SourceFile file,
        Dictionary<string, NodeId> aliases)
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
        return node;
    }

    static void CheckText(JsonElement item, in SourceFile file, KindId kind)
    {
        if (item.TryGetProperty("text", out _)
            && (!KindTable.HasSchema(in file.Table, kind)
                || KindTable.SchemaOf(in file.Table, kind).Payload != PayloadFormat.Text))
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
        return new KindId(found);
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

    /// <summary>A byte offset or count: 64-bit, since a payload may be larger than an array.</summary>
    static long Offset(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out JsonElement value))
            throw new ArgumentException(field + " is required");
        if (!value.TryGetInt64(out long number) || number < 0)
            throw new ArgumentException(field + " must be a nonnegative integer");
        return number;
    }

    static void Bound(long value, long maximum, string label)
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
