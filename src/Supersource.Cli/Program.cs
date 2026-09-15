using System;
using System.Globalization;
using System.IO;
using System.Text;
using Supersource;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Resolve;
using Supersource.Rules;
using Supersource.Compare;
using Supersource.Timeline;

namespace Supersource.Cli;
/// <summary>
/// Generic CLI for reading, comparing and editing source graphs without domain libraries.
/// Dispatch also serves tools that extend this command set with their own verbs.
/// Source content is never executed by these commands.
/// </summary>
public static class Program
{
    /// <summary>One layer's verb table. False means "not mine, ask the layer below".</summary>
    public delegate bool Verbs(string[] args, out int code);

    /// <summary>Command names owned by this CLI, exposed for extension dispatch.</summary>
    public static readonly string[] VerbNames =
    {
        "roundtrip", "cat", "stat", "validate", "rules", "holes", "scope", "refs",
        "history", "sections", "upgrade", "diff", "merge", "kinds", "blame", "verify",
        "find", "node", "deps", "edit",
    };

    public static int Main(string[] args)
    {
        return Dispatch(args, TryRun, Usage);
    }

    /// <summary>Dispatches commands and maps corrupt-input and I/O failures to exit code 2.</summary>
    public static int Dispatch(string[] args, Verbs verbs, Action<TextWriter> usage)
    {
        if (args.Length == 0)
        {
            usage(Console.Error);
            return 1;
        }

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help"))
        {
            usage(Console.Out);
            return 0;
        }

        try
        {
            int code;
            if (verbs(args, out code))
            {
                return code;
            }

            Console.Error.WriteLine("unknown command: " + args[0]);
            usage(Console.Error);
            return 1;
        }
        catch (FormatCorruptException error)
        {
            Console.Error.WriteLine("corrupt: " + error.Message);
            return 2;
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("no such file");
            return 2;
        }
    }

    public static bool TryRun(string[] args, out int code)
    {
        switch (args[0])
        {
            case "roundtrip": code = RoundTrip(Arg(args, 1)); return true;
            case "cat":       code = Cat(Arg(args, 1)); return true;
            case "node":      code = InspectNode(args); return true;
            case "edit":      code = Edit.Run(args); return true;
            case "stat":      code = Stat(Arg(args, 1)); return true;
            case "validate":  code = Validate(Arg(args, 1)); return true;
            case "rules":     code = Rules(Arg(args, 1)); return true;
            case "holes":     code = Holes(Arg(args, 1)); return true;
            case "scope":     code = ScopeAt(Arg(args, 1), Arg(args, 2)); return true;
            case "refs":      code = Refs(Arg(args, 1), Arg(args, 2)); return true;
            case "history":
                code = History(Arg(args, 1), NodeFlag(args, "--node"), IntFlag(args, "--actor"));
                return true;
            case "sections":  code = Sections(Arg(args, 1)); return true;
            case "upgrade":   code = Upgrade(args); return true;
            case "diff":      code = Diff(Arg(args, 1), Arg(args, 2)); return true;
            case "merge":     code = MergeFiles(args); return true;
            case "kinds":     code = Kinds(Arg(args, 1)); return true;
            case "blame":     code = Blame(Arg(args, 1), Arg(args, 2)); return true;
            case "verify":    code = Verify(Arg(args, 1)); return true;
            case "find":      code = Find(Arg(args, 1), Opt(args, 2), Opt(args, 3)); return true;
            case "deps":      code = Deps.Run(Arg(args, 1), Rest(args, 2)); return true;
            default:          code = 0; return false;
        }
    }

    public static void Usage(TextWriter w)
    {
        Header(w, "supersource");
        VerbLines(w);
    }

    /// <summary>Writes the usage header with the caller’s command name.</summary>
    public static void Header(TextWriter w, string command)
    {
        w.WriteLine(command + " <command> [arguments]");
        w.WriteLine();
    }

    /// <summary>This layer's verbs, with no header, for the layers that contain it.</summary>
    public static void VerbLines(TextWriter w)
    {
        w.WriteLine("  edit <file> <edits.json> [--dry-run] [--expect-hash <sha256>]  apply a checked batch");
        w.WriteLine("  --help                              show usage");
        w.WriteLine("  cat <file>                          print the graph");
        w.WriteLine("  node <file> <n>                      inspect one node, its children and references");
        w.WriteLine("  stat <file>                          counts, payload sizes and schema coverage");
        w.WriteLine("  validate <file>                      check structural integrity");
        w.WriteLine("  rules <file>                         check source rules and schema conformance");
        w.WriteLine("  holes <file>                         list unbound and dangling references");
        w.WriteLine("  scope <file> <n>                     declarations visible at node n");
        w.WriteLine("  refs <file> <n>                      nodes that reference node n");
        w.WriteLine("  history <file> [--node n] [--actor n] list recorded operations");
        w.WriteLine("  blame <file> <n>                     last recorded operation touching node n");
        w.WriteLine("  kinds <file>                         embedded kind schemas");
        w.WriteLine("  find <file> <kind> [text]            find nodes by local kind name and payload text");
        w.WriteLine("  sections <file>                      section sizes and unknown sections");
        w.WriteLine("  verify <file>                        check the stored digest");
        w.WriteLine("  roundtrip <file>                     compare a temporary rewrite; needs write access");
        w.WriteLine("  upgrade <file>                       rewrite in place using the current format");
        w.WriteLine("  diff <before> <after>                compare two states of one source");
        w.WriteLine("  merge <base> <ours> <theirs> <out>    write a three-way merge, including conflicts");
        w.WriteLine("  deps <file> [libs...]                inspect and resolve external references");
    }

    /// <summary>A `--flag value` naming a node, or none when it is absent.</summary>
    public static NodeId NodeFlag(string[] args, string flag)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return ParseNode(args[i + 1]);
            }
        }
        return NodeId.None;
    }

    /// <summary>A `--flag value` naming a number, or -1 when it is absent.</summary>
    public static int IntFlag(string[] args, string flag)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == flag && int.TryParse(args[i + 1], out int value))
            {
                return value;
            }
        }
        return -1;
    }

    public static string Arg(string[] args, int at)
    {
        if (at >= args.Length)
        {
            throw new FileNotFoundException();
        }
        return args[at];
    }

    public static string Opt(string[] args, int at)
    {
        return at < args.Length ? args[at] : null;
    }

    public static string[] Rest(string[] args, int from)
    {
        if (from >= args.Length)
        {
            return Array.Empty<string>();
        }
        string[] rest = new string[args.Length - from];
        Array.Copy(args, from, rest, 0, rest.Length);
        return rest;
    }

    /// <summary>
    /// Checks byte preservation on rewrite; journalled files are checked after folding their
    /// journals.
    /// </summary>
    static int RoundTrip(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        // A full rewrite folds journals into the graph, so journalled files are not
        // byte-identical.
        int journals = 0;
        for (int i = 0; i < file.SectionOrderCount; i++)
        {
            if (file.SectionOrder[i] == (uint)SectionId.Journal)
            {
                journals++;
            }
        }

        // Use a sibling file to avoid retaining the rewritten source bytes in memory.
        string beside = path + ".roundtrip";
        try
        {
            long nodes = file.Src.NodeCount;
            SourceWrite.ToFile(in file, beside);
            SourceFile.Close(ref file);

            long at = FirstDifference(path, beside, out long originalLength, out long rewrittenLength);

            if (journals > 0)
            {
                // The claim that survives: the folded file says the same thing.
                SourceFile folded = SourceRead.FromFile(beside);
                long same = folded.Src.NodeCount;
                SourceFile.Close(ref folded);

                if (same != nodes)
                {
                    Console.Error.WriteLine("DIFFERS: " + nodes + " nodes in, " + same + " out");
                    return 3;
                }

                Console.WriteLine("folded " + journals + " journal(s): " + originalLength +
                    " bytes in, " + rewrittenLength + " out, " + nodes + " nodes either way");
                return 0;
            }

            if (at >= 0)
            {
                Console.Error.WriteLine(originalLength == rewrittenLength
                    ? "DIFFERS at byte " + at
                    : "DIFFERS: " + originalLength + " bytes in, " + rewrittenLength + " out");
                return 3;
            }

            Console.WriteLine("identical (" + originalLength + " bytes, " +
                file.UnknownCount + " section(s) preserved unread)");
            return 0;
        }
        finally
        {
            SourceFile.Close(ref file);
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }
    }

    /// <summary>Returns the first differing byte offset, or -1. Compares through fixed-size buffers.</summary>
    static long FirstDifference(string left, string right, out long leftLength, out long rightLength)
    {
        using (FileStream a = File.OpenRead(left))
        using (FileStream b = File.OpenRead(right))
        {
            leftLength = a.Length;
            rightLength = b.Length;

            byte[] one = new byte[1 << 16];
            byte[] two = new byte[1 << 16];
            long at = 0;

            while (true)
            {
                int got = a.Read(one, 0, one.Length);
                int had = b.Read(two, 0, two.Length);

                int common = got < had ? got : had;
                for (int i = 0; i < common; i++)
                {
                    if (one[i] != two[i])
                    {
                        return at + i;
                    }
                }

                if (got != had)
                {
                    return at + common;
                }
                if (got == 0)
                {
                    return -1;
                }

                at += got;
            }
        }
    }

    /// <summary>
    /// Writes a three-way merge after remapping each input’s kind and relation handles by qualified
    /// name. Conflicts retain ours, add merge notes and return exit code 3.
    /// </summary>
    static int MergeFiles(string[] args)
    {
        if (Arg(args, 4) == null)
        {
            Console.Error.WriteLine("usage: merge <common.ss> <ours.ss> <theirs.ss> <out.ss>");
            return 1;
        }

        SourceFile common = SourceRead.FromFile(args[1]);
        SourceFile ours = SourceRead.FromFile(args[2]);
        SourceFile theirs = SourceRead.FromFile(args[3]);

        if (ours.Src.Identity != common.Src.Identity || theirs.Src.Identity != common.Src.Identity)
        {
            Console.Error.WriteLine("these are not three states of one source");
            return 3;
        }

        // Into our numbering, by name. Both of the others.
        KindRemap.Apply(ref common.Src, in common.Table, ref ours.Table);
        KindRemap.Apply(ref theirs.Src, in theirs.Table, ref ours.Table);

        MergeNote[] notes = null;
        SourceFile merged = SourceFile.Create(16);
        merged.Table = ours.Table;
        merged.Src = Merge.Between(in common.Src, in ours.Src, in theirs.Src,
            in merged.Table, ref notes, out int count);

        int conflicts = 0;
        for (int i = 0; i < count; i++)
        {
            bool decided = notes[i].How == Decision.Conflict
                || notes[i].How == Decision.Unrepresentable;
            conflicts += decided ? 1 : 0;

            Console.WriteLine(
                (decided ? "DECIDED  " : "         ") +
                notes[i].Node.ToString().PadRight(8) +
                notes[i].Facet.ToString().PadRight(12) +
                notes[i].Detail);
        }

        MergeIds ids = MergeKinds.Install(ref merged.Table);
        MergeRecord.Write(ref merged.Src, in ids, merged.Src.Root, notes, count);

        // Embed schemas for the merge records just added.
        KindSchemaCodec.Sync(ref merged.Src, in merged.Table, in merged.Boot);

        SourceWrite.ToFile(in merged, args[4]);

        // Keep the mapped inputs open until writing has finished.
        SourceFile.Close(ref common);
        SourceFile.Close(ref ours);
        SourceFile.Close(ref theirs);

        Console.WriteLine();
        Console.WriteLine(count == 0
            ? "merged cleanly; nothing to decide"
            : count + " decision(s), " + conflicts + " of them contested");
        return conflicts > 0 ? 3 : 0;
    }

    /// <summary>Compares states of the same source by node ID after remapping kind and relation handles.</summary>
    static int Diff(string beforePath, string afterPath)
    {
        if (afterPath == null)
        {
            Console.Error.WriteLine("usage: diff <before.ss> <after.ss>");
            return 1;
        }

        SourceFile before = SourceRead.FromFile(beforePath);
        SourceFile after = SourceRead.FromFile(afterPath);

        if (before.Src.Identity != after.Src.Identity)
        {
            Console.Error.WriteLine(
                "these are two sources, not two states of one: " +
                before.Src.Identity + " and " + after.Src.Identity);
            return 3;
        }

        Operation[] ops = null;
        int count = StructuralDiff.Between(in before.Src, in after.Src, in after.Table, ref ops);

        for (int i = 0; i < count; i++)
        {
            Console.WriteLine(
                ops[i].Kind.ToString().PadRight(12) +
                ops[i].Node + Describe(in after, in ops[i]));
        }

        Console.WriteLine();
        Console.WriteLine(count == 0 ? "no change" : count + " operation(s)");
        return 0;
    }

    /// <summary>
    /// Rewrites a file in the current format, preserving unknown sections. An already-current file
    /// is left unchanged.
    /// </summary>
    static int Upgrade(string[] args)
    {
        string path = Arg(args, 1);

        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: upgrade <file> (rewrites in place; takes no options)");
            return 2;
        }

        SourceFile file = SourceRead.FromFile(path);

        ContentHash digestOnLoad = file.Digest;
        bool wasDigestless = file.DigestKind == DigestKind.None;

        // Clear the remembered section order to request the current format’s section layout.
        file.SectionOrderCount = 0;

        // Write and validate the sibling file before replacing the original.
        string beside = path + ".migrating";
        try
        {
            SourceWrite.ToFile(in file, beside);

            long differsAt = FirstDifference(path, beside, out long originalLength, out long rewrittenLength);
            if (differsAt < 0)
            {
                Console.WriteLine("already current (" + originalLength + " bytes)");
                return 0;
            }

            // Check the rewritten content before replacing the original. Upgrades may add
            // sections.
            SourceFile check = SourceRead.FromFile(beside);
            bool same = check.Src.NodeCount == file.Src.NodeCount
                && check.UnknownCount == file.UnknownCount;
            SourceFile.Close(ref check);

            if (!same)
            {
                Console.Error.WriteLine("REFUSED: the rewritten file does not read back the same");
                return 3;
            }

            // Both files have to be let go before one can replace the other: a mapped file
            // is open, and on some systems an open file cannot be overwritten.
            ContentHash since = SourceRead.DigestOf(path);
            SourceFile.Close(ref file);

            if (!wasDigestless && !since.IsNone && !since.Equals(digestOnLoad))
            {
                Console.Error.WriteLine("REFUSED: the file has been written since it was read");
                return 3;
            }

            File.Move(beside, path, true);

            long delta = rewrittenLength - originalLength;
            Console.WriteLine(
                originalLength + " -> " + rewrittenLength + " bytes (" +
                (delta > 0 ? "+" : "") + delta + ", " +
                Percent((int)-delta, (int)originalLength) + " smaller)");
            return 0;
        }
        finally
        {
            SourceFile.Close(ref file);
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }
    }

    /// <summary>Prints the embedded schemas without loading their authoring libraries.</summary>
    static int Kinds(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        int described = 0;
        for (int i = 0; i < file.Table.KindCount; i++)
        {
            KindId kind = new KindId(i);
            QualifiedName name = KindTable.NameOf(in file.Table, kind);
            if (name.IsNone)
            {
                continue;
            }

            bool has = KindTable.HasSchema(in file.Table, kind);
            KindSchema schema = KindTable.SchemaOf(in file.Table, kind);

            Console.WriteLine(name.Name.PadRight(24) + (has ? "" : "(no schema)"));
            if (!has)
            {
                continue;
            }

            described++;
            Console.WriteLine("    library    " + name.Library);
            Console.WriteLine("    facts      " + (schema.Facts == StructuralFact.None
                ? "none" : schema.Facts.ToString()));
            Console.WriteLine("    payload    " + schema.Payload +
                (schema.Stride.IsArray
                    ? "  header " + schema.Stride.Header + ", " + schema.Stride.Unit + " bytes each"
                    : ""));
            Console.WriteLine("    shape      " + schema.Shape +
                (schema.DisplayColor == 0 ? "" : "  colour " + schema.DisplayColor.ToString("x8")));

            ReadOnlySpan<RelationId> uses = KindTable.UsedRelations(in file.Table, kind);
            for (int r = 0; r < uses.Length; r++)
            {
                Console.WriteLine("    uses       " +
                    KindTable.RelationNameOf(in file.Table, uses[r]).Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine(described + " of " + file.Table.KindCount + " kind(s) described");
        return 0;
    }

    /// <summary>Summarizes the recorded operations that touched one node.</summary>
    static int Blame(string path, string which)
    {
        SourceFile file = SourceRead.FromFile(path);
        NodeId node = ParseNode(which);

        if (file.History.Count == 0)
        {
            Console.WriteLine("no history");
            return 0;
        }

        int touched = 0;
        int last = -1;
        for (int i = 0; i < file.History.Count; i++)
        {
            if (!Names(in file.History.Entries[i].Op, node))
            {
                continue;
            }
            touched++;
            last = i;
        }

        if (last < 0)
        {
            Console.WriteLine(node + " is in no entry of this history");
            return 0;
        }

        Entry entry = file.History.Entries[last];
        Console.WriteLine(
            entry.Order.ToString().PadLeft(6) +
            "  actor " + entry.Actor +
            "  " + entry.Op.Kind.ToString().PadRight(12) +
            entry.Op.Node + Describe(in file, in entry.Op));

        Console.WriteLine();
        Console.WriteLine(touched + " of " + file.History.Count + " entries touch " + node);
        return 0;
    }

    /// <summary>Whether an operation is about this node — as its subject or as its container.</summary>
    static bool Names(in Operation op, NodeId node)
    {
        return op.Node == node || op.Parent == node || op.OldParent == node || op.Target == node;
    }

    /// <summary>
    /// Finds nodes by local kind name and optional payload text. A shared local name matches every
    /// library using it.
    /// </summary>
    static int Find(string path, string kind, string text)
    {
        if (kind == null)
        {
            Console.Error.WriteLine("usage: find <file> <kind> [text]");
            return 1;
        }

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            byte[] wanted = text == null ? null : Encoding.UTF8.GetBytes(text);
            int found = 0;

            for (long i = 0; i < file.Src.NodeCount; i++)
            {
                NodeId node = new NodeId(i);
                if (!file.Src.IsLive(node))
                {
                    continue;
                }

                QualifiedName name = KindTable.NameOf(in file.Table, file.Src.Nodes[i].Kind);
                if (name.IsNone || name.Name != kind)
                {
                    continue;
                }

                ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, node);
                if (wanted != null && !Holds(payload, wanted))
                {
                    continue;
                }

                found++;
                PayloadFormat format = KindTable.SchemaOf(in file.Table, file.Src.Nodes[i].Kind).Payload;
                Console.WriteLine(
                    node.ToString().PadRight(10) +
                    (format == PayloadFormat.Utf8 && payload.Length > 0
                        ? "\"" + Encoding.UTF8.GetString(payload) + "\""
                        : "<" + payload.Length + " bytes>"));
            }

            Console.WriteLine();
            Console.WriteLine(found + " node(s)");
            return 0;
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    /// <summary>Returns whether the payload contains the requested byte sequence.</summary>
    static bool Holds(ReadOnlySpan<byte> payload, byte[] wanted)
    {
        for (int at = 0; at + wanted.Length <= payload.Length; at++)
        {
            int i = 0;
            while (i < wanted.Length && payload[at + i] == wanted[i])
            {
                i++;
            }
            if (i == wanted.Length)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Checks the stored digest. A file without a digest is reported as unverifiable.</summary>
    static int Verify(string path)
    {
        if (!SourceRead.Verify(path, out ContentHash recorded, out ContentHash actual))
        {
            if (recorded.IsNone)
            {
                Console.WriteLine("no digest; this file predates them (migrate adds one)");
                return 0;
            }

            Console.Error.WriteLine("CORRUPT: the content does not match the digest");
            Console.Error.WriteLine("  recorded  " + recorded);
            Console.Error.WriteLine("  actual    " + actual);
            return 3;
        }

        Console.WriteLine("intact (" + actual + ")");
        return 0;
    }

    /// <summary>Reads section headers without decoding their contents, including unknown sections.</summary>
    static int Sections(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        SectionSpan[] spans = null;
        int count = SourceRead.Sections(bytes, ref spans);

        int framing = bytes.Length;
        for (int i = 0; i < count; i++)
        {
            framing -= (int)spans[i].Length;
        }

        for (int i = 0; i < count; i++)
        {
            string name = NameOfSection(spans[i].Id);
            Console.WriteLine(
                ("0x" + spans[i].Id.ToString("x")).PadLeft(10) + "  " +
                name.PadRight(16) +
                spans[i].Length.ToString().PadLeft(9) + "  " +
                Percent(spans[i].Length, bytes.Length));
        }

        Console.WriteLine();
        Console.WriteLine(
            "".PadLeft(10) + "  " + "framing".PadRight(16) +
            framing.ToString().PadLeft(9) + "  " + Percent(framing, bytes.Length));
        Console.WriteLine(
            "".PadLeft(10) + "  " + "total".PadRight(16) +
            bytes.Length.ToString().PadLeft(9));
        return 0;
    }

    static string Percent(int part, int whole)
    {
        if (whole <= 0)
        {
            return "";
        }
        // Use invariant formatting for stable command output.
        return (100.0 * part / whole).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5) + "%";
    }

    /// <summary>Returns a known section name or an unread-section label.</summary>
    static string NameOfSection(uint id)
    {
        switch ((SectionId)id)
        {
            case SectionId.Identity: return "identity";
            case SectionId.KindNames: return "kind names";
            case SectionId.RelationNames: return "relation names";
            case SectionId.Nodes: return "nodes";
            case SectionId.History: return "history";
            default: return "(unread)";
        }
    }

    /// <summary>Prints stored history, optionally filtered by node and actor.</summary>
    static int History(string path, NodeId only, int actor)
    {
        SourceFile file = SourceRead.FromFile(path);

        if (file.History.Count == 0)
        {
            Console.WriteLine("no history");
            return 0;
        }

        int shown = 0;
        for (int i = 0; i < file.History.Count; i++)
        {
            Entry entry = file.History.Entries[i];

            if (only.IsSome && !Names(in entry.Op, only))
            {
                continue;
            }
            if (actor >= 0 && entry.Actor != actor)
            {
                continue;
            }

            shown++;
            Console.WriteLine(
                entry.Order.ToString().PadLeft(6) +
                "  actor " + entry.Actor +
                "  " + entry.Op.Kind.ToString().PadRight(12) +
                entry.Op.Node + Describe(in file, in entry.Op));
        }

        Console.WriteLine();
        Console.WriteLine(shown == file.History.Count
            ? file.History.Count + " entries"
            : shown + " of " + file.History.Count + " entries");
        return 0;
    }

    /// <summary>The part of an operation worth showing, which differs by kind.</summary>
    static string Describe(in SourceFile file, in Operation op)
    {
        switch (op.Kind)
        {
            case OpKind.Insert:
            case OpKind.Wrap:
                return "  in " + op.Parent + " at " + op.Index +
                    "  " + KindTable.NameOf(in file.Table, op.NodeKind).Name;

            case OpKind.Move:
                return "  " + op.OldParent + "[" + op.OldIndex + "] -> " +
                    op.Parent + "[" + op.Index + "]";

            case OpKind.Splice:
                return "  at " + op.Index + " -" + op.Count +
                    " +" + (op.Value == null ? 0 : op.Value.Length) + " bytes";

            case OpKind.Rename:
            case OpKind.ModifyValue:
                return "  " + (op.Value == null ? 0 : op.Value.Length) + " bytes";

            case OpKind.Connect:
            case OpKind.Disconnect:
                return "  " + KindTable.RelationNameOf(in file.Table, op.Relation).Name +
                    " -> " + op.Target;

            default:
                return string.Empty;
        }
    }

    static int Cat(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        Console.WriteLine("source " + file.Src.Identity);
        Console.WriteLine(file.Src.NodeCount + " slots, " +
            (file.Src.NodeCount - file.Src.DeletedNodeCount()) + " live");
        Console.WriteLine();

        if (file.Src.Root.IsSome && file.Src.IsLive(file.Src.Root))
        {
            Print(in file, file.Src.Root, 0);
        }

        return 0;
    }

    /// <summary>
    /// Prints a subtree iteratively to avoid stack overflow on deep containment. Children are
    /// pushed in reverse to preserve their order.
    /// </summary>
    static void Print(in SourceFile file, NodeId root, int rootDepth)
    {
        long[] work = new long[64];
        int[] depths = new int[64];
        int count = 0;

        work[count] = root.Index;
        depths[count] = rootDepth;
        count++;

        while (count > 0)
        {
            count--;
            NodeId id = new NodeId(work[count]);
            int depth = depths[count];

            PrintOne(in file, id, depth);

            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, id);
            if (count + children.Length > work.Length)
            {
                int size = work.Length;
                while (size < count + children.Length)
                {
                    size *= 2;
                }
                Array.Resize(ref work, size);
                Array.Resize(ref depths, size);
            }

            for (int c = children.Length - 1; c >= 0; c--)
            {
                work[count] = children[c];
                depths[count] = depth + 1;
                count++;
            }
        }
    }

    static void PrintOne(in SourceFile file, NodeId id, int depth, bool indices = false)
    {
        StringBuilder line = new StringBuilder();
        line.Append(' ', depth * 2);

        QualifiedName name = KindTable.NameOf(in file.Table, file.Src.Nodes[id.Index].Kind);
        line.Append(name.Name ?? "<unknown-kind>");

        if (!KindTable.HasSchema(in file.Table, file.Src.Nodes[id.Index].Kind))
        {
            line.Append(" [no schema]");
        }

        ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, id);
        if (payload.Length > 0)
        {
            PayloadFormat format = KindTable.SchemaOf(in file.Table, file.Src.Nodes[id.Index].Kind).Payload;
            line.Append("  ");
            line.Append(format == PayloadFormat.Utf8
                ? "\"" + Encoding.UTF8.GetString(payload) + "\""
                : "<" + payload.Length + " bytes>");
        }

        line.Append("   ").Append(id.ToString());
        Console.WriteLine(line.ToString());

        ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, id);
        for (int r = 0; r < refs.Length; r++)
        {
            StringBuilder edge = new StringBuilder();
            edge.Append(' ', depth * 2 + 2);
            if (indices) edge.Append('[').Append(r).Append("] ");
            edge.Append("--");
            edge.Append(KindTable.RelationNameOf(in file.Table, refs[r].Relation).Name ?? "<unknown-relation>");
            edge.Append("--> ");

            if (refs[r].IsUnbound)
            {
                ReadOnlySpan<byte> label = SourceWalk.Label(in file.Src, in refs[r]);
                edge.Append("hole \"").Append(Encoding.UTF8.GetString(label)).Append('"');
            }
            else if (!file.Src.IsLive(refs[r].Target))
            {
                edge.Append(refs[r].Target).Append(" (deleted)");
            }
            else
            {
                edge.Append(refs[r].Target);
            }

            if (indices)
                edge.Append("  (relation ").Append(KindTable.RelationNameOf(in file.Table, refs[r].Relation)).Append(')');
            Console.WriteLine(edge.ToString());
        }
    }

    static int InspectNode(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: node <file> <n>");
            return 1;
        }

        NodeId id = ParseNode(args[2]);
        SourceFile file = SourceRead.FromFile(args[1]);
        try
        {
            if (id.IsNone || id.Index >= file.Src.NodeCount)
            {
                Console.Error.WriteLine("no node " + args[2]);
                return 2;
            }

            Console.WriteLine("source " + file.Src.Identity);
            Console.WriteLine("state " + (file.Src.IsLive(id) ? "live" : "deleted"));
            Console.WriteLine("parent " + file.Src.Nodes[id.Index].Parent);
            Console.WriteLine("kind " + KindTable.NameOf(in file.Table, file.Src.Nodes[id.Index].Kind));
            Console.WriteLine("payload " + file.Src.Nodes[id.Index].PayloadLength + " bytes");
            PrintOne(in file, id, 0, true);

            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, id);
            Console.WriteLine("children " + children.Length);
            for (int i = 0; i < children.Length; i++)
            {
                Console.WriteLine("  " + new NodeId(children[i]));
            }
            return 0;
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    /// <summary>Reports schema coverage and declared opacity without interpreting payloads.</summary>
    static int Stat(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        int live = 0;
        int noSchema = 0;
        int declaredOpaque = 0;
        int opaquePayload = 0;
        long payloadBytes = 0;
        long opaqueBytes = 0;

        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId id = new NodeId(i);
            if (!file.Src.IsLive(id))
            {
                continue;
            }

            live++;
            KindId kind = file.Src.Nodes[i].Kind;
            int bytes = file.Src.Nodes[i].PayloadLength;
            payloadBytes += bytes;

            if (!KindTable.HasSchema(in file.Table, kind))
            {
                noSchema++;
                opaqueBytes += bytes;
                continue;
            }

            KindSchema schema = KindTable.SchemaOf(in file.Table, kind);
            if (schema.InteriorOpaque)
            {
                declaredOpaque++;
            }
            if (schema.Payload == PayloadFormat.Opaque)
            {
                opaquePayload++;
                opaqueBytes += bytes;
            }
        }

        Console.WriteLine("nodes            " + live + " live, " + file.Src.DeletedNodeCount() + " deleted");
        Console.WriteLine("kinds            " + file.Table.KindCount + " named");
        Console.WriteLine("relations        " + file.Table.RelationCount + " named");
        Console.WriteLine();
        Console.WriteLine("uninterpretable  " + noSchema + Percent(noSchema, live) + "  no embedded schema");
        Console.WriteLine("opaque interior  " + declaredOpaque + Percent(declaredOpaque, live) + "  declared");
        Console.WriteLine("opaque payload   " + opaquePayload + Percent(opaquePayload, live) + "  declared");
        Console.WriteLine();
        Console.WriteLine("payload bytes    " + payloadBytes + ", " + opaqueBytes + " opaque" + Percent(opaqueBytes, payloadBytes));
        Console.WriteLine("unread sections  " + file.UnknownCount);

        return 0;
    }

    static string Percent(long part, long whole)
    {
        if (whole == 0)
        {
            return "";
        }
        // Invariant culture: tool output that changes shape with the host locale is not
        // something another tool can read, and this is meant to be tracked over time.
        return " (" + (part * 100.0 / whole).ToString(
            "0.#", System.Globalization.CultureInfo.InvariantCulture) + "%)";
    }

    static int Validate(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        Problem[] problems;
        if (SourceValidate.Check(in file, out problems))
        {
            Console.WriteLine("structurally sound");
            Console.WriteLine();
            Console.WriteLine("Note: this checks structural truths only. Holes, dangling");
            Console.WriteLine("references and uninterpretable kinds are valid states (§3.6, §6),");
            Console.WriteLine("and source rules are a separate check (§18.4).");
            return 0;
        }

        for (int i = 0; i < problems.Length; i++)
        {
            Console.Error.WriteLine(problems[i].Kind + "  " + problems[i].Node + "  " + problems[i].Detail);
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine(problems.Length + " problem(s)");
        return 3;
    }

    /// <summary>Runs the CLI’s default generic rules. Domain checks belong to their authoring libraries.</summary>
    static int Rules(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.NoDanglingReferences, Severity.Block);
        RuleSet.Add(ref rules, StockRule.NoHoles, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.EveryDeclarationHasATextLabel, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.EveryKindIsInterpretable, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.PayloadMatchesTheSchema, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.EdgeDataMatchesTheRelation, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.SchemaDeclaresOnlyWhatIsUsed, Severity.Warn);

        RuleViolation[] found = null;

        int count = RuleSet.Run(in rules, in file.Src, in file.Table, ref found);

        for (int i = 0; i < count; i++)
        {
            Console.WriteLine(
                (found[i].Severity == Severity.Block ? "BLOCK  " : "warn   ") +
                RuleSet.Describe(in found[i], in file.Table) + "  " +
                found[i].Rule + ": " + found[i].Message);
        }

        Console.WriteLine();
        if (count == 0)
        {
            Console.WriteLine("all rules pass");
            return 0;
        }

        bool blocked = RuleSet.Blocks(found, count);
        Console.WriteLine(count + " violation(s); " +
            (blocked ? "this change would not enter shared history" : "none blocking"));
        return blocked ? 3 : 0;
    }

    /// <summary>Reports unbound and dangling references without modifying them.</summary>
    static int Holes(string path)
    {
        SourceFile file = SourceRead.FromFile(path);

        Finding[] findings = null;
        int count = RelationCheck.Check(in file.Src, in file.Table, ref findings);

        int shown = 0;
        for (int i = 0; i < count; i++)
        {
            if (findings[i].Kind != FindingKind.Hole && findings[i].Kind != FindingKind.DanglingReference)
            {
                continue;
            }
            Console.WriteLine(findings[i].Node + "  " + findings[i].Kind + "  " + findings[i].Detail);
            shown++;
        }

        Console.WriteLine();
        Console.WriteLine(shown + " hole(s). This is a valid state, not an error (§6).");
        return 0;
    }

    /// <summary>Lists declarations visible at a node using the embedded schema’s scope facts.</summary>
    static int ScopeAt(string path, string node)
    {
        SourceFile file = SourceRead.FromFile(path);

        NodeId at = ParseNode(node);
        if (!file.Src.IsLive(at))
        {
            Console.Error.WriteLine("no live node " + at);
            return 2;
        }

        NodeId[] visible = null;
        int count = Scope.Visible(in file.Src, in file.Table, at, ref visible);

        for (int i = 0; i < count; i++)
        {
            Console.WriteLine(visible[i] + "  " + Describe(in file, visible[i]));
        }

        Console.WriteLine();
        Console.WriteLine(count + " declaration(s) visible, innermost first.");
        return 0;
    }

    static int Refs(string path, string node)
    {
        SourceFile file = SourceRead.FromFile(path);

        NodeId target = ParseNode(node);
        NodeId[] referrers = null;
        int count = SourceWalk.Referrers(in file.Src, target, ref referrers);

        for (int i = 0; i < count; i++)
        {
            Console.WriteLine(referrers[i] + "  " + Describe(in file, referrers[i]));
        }

        Console.WriteLine();
        Console.WriteLine(count + " referrer(s).");
        return 0;
    }

    public static NodeId ParseNode(string text)
    {
        string digits = text.StartsWith("n") ? text.Substring(1) : text;
        long index;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            || index == long.MaxValue)
        {
            throw new FormatCorruptException("not a node id: " + text);
        }
        return new NodeId(index);
    }

    public static string Describe(in SourceFile file, NodeId id)
    {
        QualifiedName kind = KindTable.NameOf(in file.Table, file.Src.Nodes[id.Index].Kind);
        string name = kind.Name ?? "<unknown-kind>";

        if (KindTable.SchemaOf(in file.Table, file.Src.Nodes[id.Index].Kind).Payload == PayloadFormat.Utf8)
        {
            ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, id);
            if (payload.Length > 0)
            {
                return name + " \"" + Encoding.UTF8.GetString(payload) + "\"";
            }
        }
        return name;
    }    }
