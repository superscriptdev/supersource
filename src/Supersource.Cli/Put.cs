using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Supersource.Compare;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;

namespace Supersource.Cli;

/// <summary>
/// `put`: text as `cat` prints it, written back into the source. Each line at the outermost indent
/// names a node already there, by id, and the subtree under it becomes what the text says: a line
/// with an id restates that node — its kind, its payload, its references, where it sits — and a
/// line without one adds a node. A node under a restated one that the text leaves out is deleted.
///
/// The change is made as the operations `edit` applies, through the log, so ids are kept and a
/// payload that changed in one row is a splice of that row. Text that comes back as `cat` printed
/// it is no change.
/// </summary>
public static class Put
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) return Usage();
        string path = args[1];
        string from = null;
        string inline = null;
        string library = null;
        string expected = null;
        bool dryRun = false;

        for (int i = 2; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--dry-run" && !dryRun) dryRun = true;
            else if (arg == "--text" && inline == null && i + 1 < args.Length) inline = args[++i];
            else if (arg == "--kinds" && library == null && i + 1 < args.Length) library = args[++i];
            else if (arg == "--expect-hash" && expected == null && i + 1 < args.Length)
            {
                expected = args[++i];
                if (expected.Length != 64) return Usage();
                for (int c = 0; c < expected.Length; c++)
                    if (!Uri.IsHexDigit(expected[c])) return Usage();
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && from == null) from = arg;
            else return Usage();
        }

        // Exactly one place the text comes from.
        if ((from == null) == (inline == null)) return Usage();

        SourceFile file = default;
        SourceFile seed = default;
        try
        {
            string text = inline ?? (from == "-" ? Console.In.ReadToEnd() : File.ReadAllText(from));

            string before = ContentHash.OfFile(path).ToString();
            if (expected != null && !string.Equals(before, expected, StringComparison.OrdinalIgnoreCase))
                return Refuse("file hash differs from --expect-hash; inspect the current file before retrying");

            file = SourceRead.FromFile(path);
            Edit.CheckStructure(in file);

            if (library != null)
            {
                if (!File.Exists(library)) throw new ArgumentException("no kind source at " + library);
                seed = SourceRead.FromFile(library);
                Declaring.Seed(ref file.Table, in seed.Table);
            }

            List<GraphText.Entry> tops = GraphText.Read(text, in file);
            if (tops.Count == 0)
            {
                throw new ArgumentException("the text restates no node; start from what `cat` prints");
            }

            var state = new State();
            Check(tops, in file, state);
            int danglingBefore = Dangling(in file.Src);

            Place(ref file, tops, state);
            for (int i = 0; i < state.All.Count; i++) Connect(ref file, state.All[i], state);
            Prune(ref file, state);

            Edit.CheckStructure(in file);
            Edit.CheckPayloads(in file, state.Touched);

            if (ContentHash.OfFile(path).ToString() != before)
                return Refuse("file changed while reading the text; no save attempted");

            Console.WriteLine("source " + file.Src.Identity);
            Console.WriteLine("input hash " + before);
            Console.Write(state.Report.ToString());

            int danglingAfter = Dangling(in file.Src);
            if (danglingAfter > danglingBefore)
            {
                Console.WriteLine((danglingAfter - danglingBefore)
                    + " more node(s) now hold a reference to a deleted node; `missing` lists them");
            }

            if (state.Steps == 0)
            {
                Console.WriteLine("no change");
                return 0;
            }
            if (dryRun)
            {
                Console.WriteLine("dry run: " + state.Steps + " edit(s) checked; file unchanged");
                return 0;
            }
            if (File.Exists(path + ".writing"))
                return Refuse("a .writing sibling already exists; preserve it before retrying");
            if (!SourceWrite.Replace(ref file, path, out string refusal)) return Refuse(refusal);
            Console.WriteLine("saved " + state.Steps + " edit(s)");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException || error is FormatException
            || error is InvalidOperationException)
        {
            Console.Error.WriteLine("put rejected: " + error.Message);
            return 1;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("put I/O error: " + error.Message);
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
            "usage: put <file> (<text-file> | - | --text <text>) [--kinds <source.ss>] [--dry-run] "
            + "[--expect-hash <sha256>]");
        return 1;
    }

    static int Refuse(string reason)
    {
        Console.Error.WriteLine("put refused: " + reason);
        return 3;
    }

    /// <summary>What a put has done so far, and what the text named.</summary>
    sealed class State
    {
        /// <summary>Every entry, outermost first, each before what is under it.</summary>
        public readonly List<GraphText.Entry> All = new List<GraphText.Entry>();

        /// <summary>The node each entry is: the one it restates, or the one made for it.</summary>
        public readonly Dictionary<GraphText.Entry, NodeId> Nodes = new Dictionary<GraphText.Entry, NodeId>();

        public readonly Dictionary<string, GraphText.Entry> Named = new Dictionary<string, GraphText.Entry>(StringComparer.Ordinal);
        public readonly Dictionary<NodeId, GraphText.Entry> Restated = new Dictionary<NodeId, GraphText.Entry>();
        public readonly HashSet<NodeId> Touched = new HashSet<NodeId>();
        public readonly StringBuilder Report = new StringBuilder();
        public int Steps;
    }

    /// <summary>
    /// Everything that can be refused before anything is done: ids that name no node or one node
    /// twice, names given twice or never, and outermost lines that do not restate a node.
    /// </summary>
    static void Check(List<GraphText.Entry> tops, in SourceFile file, State state)
    {
        var stack = new Stack<GraphText.Entry>();
        for (int i = tops.Count - 1; i >= 0; i--) stack.Push(tops[i]);
        while (stack.Count > 0)
        {
            GraphText.Entry entry = stack.Pop();
            state.All.Add(entry);
            for (int c = entry.Children.Count - 1; c >= 0; c--) stack.Push(entry.Children[c]);
        }

        for (int i = 0; i < state.All.Count; i++)
        {
            GraphText.Entry entry = state.All[i];
            if (entry.Id.IsSome)
            {
                if (entry.Id.Index >= file.Src.NodeCount || !file.Src.IsLive(entry.Id))
                {
                    throw Line(entry.Line, "there is no node " + entry.Id
                        + "; a line without an id adds a node");
                }
                if (state.Restated.ContainsKey(entry.Id))
                {
                    throw Line(entry.Line, entry.Id + " is already on line " + state.Restated[entry.Id].Line
                        + "; a node is in one place");
                }
                state.Restated.Add(entry.Id, entry);
            }
            else
            {
                if (!KindTable.HasSchema(in file.Table, entry.Kind))
                {
                    throw Line(entry.Line, "the kind " + GraphText.KindName(in file.Table, entry.Kind)
                        + " has no schema here, so no node of it can be made");
                }
                if (entry.Payload == null)
                {
                    throw Line(entry.Line, "a new node has no payload to keep; write it out");
                }
            }

            if (entry.Handle != null && !state.Named.TryAdd(entry.Handle, entry))
            {
                throw Line(entry.Line, "@" + entry.Handle + " already names the node on line "
                    + state.Named[entry.Handle].Line);
            }
        }

        for (int t = 0; t < tops.Count; t++)
        {
            if (tops[t].Id.IsNone)
            {
                throw Line(tops[t].Line, "an outermost line names a node already there, by id, whose "
                    + "subtree the text restates; a new node goes under one");
            }
        }

        NodeId root = file.Src.Root;
        if (state.Restated.TryGetValue(root, out GraphText.Entry rooted) && !tops.Contains(rooted))
        {
            throw Line(rooted.Line, "the root stays where it is; restate it on an outermost line");
        }

        for (int a = 0; a < tops.Count; a++)
        {
            for (int b = 0; b < tops.Count; b++)
            {
                if (a != b && SourceWalk.IsAncestorOf(in file.Src, tops[a].Id, tops[b].Id))
                {
                    throw Line(tops[b].Line, tops[b].Id + " is inside " + tops[a].Id + ", which line "
                        + tops[a].Line + " restates; restate it there");
                }
            }
        }

        for (int i = 0; i < state.All.Count; i++)
        {
            foreach (GraphText.Edge edge in state.All[i].Edges)
            {
                if (edge.Handle != null)
                {
                    if (!state.Named.ContainsKey(edge.Handle))
                    {
                        throw Line(edge.Line, "no new node is called @" + edge.Handle);
                    }
                    continue;
                }
                if (edge.Unbound) continue;

                if (edge.Target.Index >= file.Src.NodeCount)
                {
                    throw Line(edge.Line, "there is no node " + edge.Target);
                }
                bool live = file.Src.IsLive(edge.Target);
                if (live && edge.Dangling)
                {
                    throw Line(edge.Line, edge.Target + " is there; drop `(deleted)`");
                }
                if (!live && !edge.Dangling)
                {
                    throw Line(edge.Line, edge.Target + " was deleted; write `" + edge.Target
                        + " (deleted)` to keep a reference to it as it is");
                }
            }
        }
    }

    /// <summary>
    /// Puts every node where the text says, outermost first: each parent's children in order,
    /// making the new ones, moving the ones that are elsewhere, and restating each. A parent is
    /// placed before its children, so a move never lands under a node that is about to move.
    /// </summary>
    static void Place(ref SourceFile file, List<GraphText.Entry> tops, State state)
    {
        for (int t = 0; t < tops.Count; t++)
        {
            state.Nodes[tops[t]] = tops[t].Id;
            Restate(ref file, tops[t], state);
        }

        var stack = new Stack<GraphText.Entry>();
        for (int t = tops.Count - 1; t >= 0; t--) stack.Push(tops[t]);

        while (stack.Count > 0)
        {
            GraphText.Entry parent = stack.Pop();
            NodeId under = state.Nodes[parent];

            for (int i = 0; i < parent.Children.Count; i++)
            {
                GraphText.Entry child = parent.Children[i];
                if (child.Id.IsNone)
                {
                    Operation insert = default;
                    insert.Kind = OpKind.Insert;
                    insert.Parent = under;
                    insert.Index = i;
                    insert.NodeKind = child.Kind;
                    insert.Value = child.Payload;
                    NodeId made = Do(ref file, insert, state, child.Line,
                        child.Handle == null ? null : "@" + child.Handle);
                    state.Nodes[child] = made;
                    continue;
                }

                state.Nodes[child] = child.Id;
                if (SourceWalk.Parent(in file.Src, child.Id) != under
                    || SourceWalk.IndexInParent(in file.Src, child.Id) != i)
                {
                    Operation move = default;
                    move.Kind = OpKind.Move;
                    move.Node = child.Id;
                    move.Parent = under;
                    move.Index = i;
                    Do(ref file, move, state, child.Line);
                }
                Restate(ref file, child, state);
            }

            for (int i = parent.Children.Count - 1; i >= 0; i--) stack.Push(parent.Children[i]);
        }
    }

    /// <summary>A node's kind and payload, where they differ from what the text says.</summary>
    static void Restate(ref SourceFile file, GraphText.Entry entry, State state)
    {
        NodeId node = entry.Id;
        if (file.Src.Nodes[node.Index].Kind != entry.Kind)
        {
            Operation retype = default;
            retype.Kind = OpKind.Retype;
            retype.Node = node;
            retype.NodeKind = entry.Kind;
            Do(ref file, retype, state, entry.Line);
        }

        if (entry.Payload == null) return;

        if (!SourceWalk.IsContiguous(in file.Src, node))
        {
            Operation replace = default;
            replace.Kind = OpKind.ModifyValue;
            replace.Node = node;
            replace.Value = entry.Payload;
            Do(ref file, replace, state, entry.Line);
            return;
        }

        ReadOnlySpan<byte> before = SourceWalk.Payload(in file.Src, node);
        if (before.SequenceEqual(entry.Payload)) return;

        // What prints the same is the same: a NaN reads back as the NaN, not as its old bits.
        PayloadFormat format = GraphText.FormatOf(in file.Table, entry.Kind);
        if (GraphText.Rendered(in format, before) == GraphText.Rendered(in format, entry.Payload)) return;

        KindSchema schema = KindTable.HasSchema(in file.Table, entry.Kind)
            ? KindTable.SchemaOf(in file.Table, entry.Kind)
            : new KindSchema { Kind = entry.Kind, Payload = PayloadFormat.Opaque };
        if (PayloadDiff.Between(in schema, node, before, entry.Payload, out Operation change))
        {
            Do(ref file, change, state, entry.Line);
        }
    }

    /// <summary>
    /// A node's references, made to read as the text's: those that already match, in order, are
    /// kept, and from the first that does not, the rest are disconnected and connected afresh.
    /// </summary>
    static void Connect(ref SourceFile file, GraphText.Entry entry, State state)
    {
        NodeId node = state.Nodes[entry];

        ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, node);
        int count = refs.Length;
        int kept = 0;
        while (kept < count && kept < entry.Edges.Count
            && Same(in file, in refs[kept], entry.Edges[kept], state))
        {
            kept++;
        }
        if (kept == count && kept == entry.Edges.Count) return;

        for (int r = count - 1; r >= kept; r--)
        {
            Operation cut = default;
            cut.Kind = OpKind.Disconnect;
            cut.Node = node;
            cut.Index = r;
            Do(ref file, cut, state, entry.Line);
        }

        for (int e = kept; e < entry.Edges.Count; e++)
        {
            GraphText.Edge edge = entry.Edges[e];
            Operation join = default;
            join.Kind = OpKind.Connect;
            join.Node = node;
            join.Relation = edge.Relation;
            join.Target = TargetOf(edge, state);
            join.Index = e;
            join.Value = edge.Data;
            Do(ref file, join, state, edge.Line);
        }
    }

    static NodeId TargetOf(GraphText.Edge edge, State state)
    {
        if (edge.Unbound) return NodeId.None;
        if (edge.Handle != null) return state.Nodes[state.Named[edge.Handle]];
        return edge.Target;
    }

    static bool Same(in SourceFile file, in Ref have, GraphText.Edge want, State state)
    {
        if (have.Relation != want.Relation) return false;
        if (want.Unbound != have.IsUnbound) return false;
        if (!want.Unbound && have.Target != TargetOf(want, state)) return false;

        ReadOnlySpan<byte> data = SourceWalk.EdgeData(in file.Src, in have);
        if (data.SequenceEqual(want.Data)) return true;
        if (want.Unbound) return false;
        PayloadFormat format = KindTable.RelationSchemaOf(in file.Table, have.Relation).Payload;
        return GraphText.Rendered(in format, data) == GraphText.Rendered(in format, want.Data);
    }

    /// <summary>
    /// Deletes what the text leaves out: every child of a restated node past the ones the text
    /// gives it. Refused when that would take a node the text restates with it.
    /// </summary>
    static void Prune(ref SourceFile file, State state)
    {
        var drop = new List<NodeId>();
        for (int i = 0; i < state.All.Count; i++)
        {
            GraphText.Entry entry = state.All[i];
            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, state.Nodes[entry]);
            for (int c = entry.Children.Count; c < children.Length; c++) drop.Add(new NodeId(children[c]));
        }

        NodeId[] under = null;
        for (int d = 0; d < drop.Count; d++)
        {
            NodeId gone = drop[d];
            if (!file.Src.IsLive(gone)) continue;

            int count = SourceWalk.Descendants(in file.Src, gone, ref under);
            for (int u = 0; u < count; u++)
            {
                if (state.Restated.TryGetValue(under[u], out GraphText.Entry kept))
                {
                    throw Line(kept.Line, under[u] + " is restated, but " + gone
                        + " above it is left out and would take it with it when deleted");
                }
            }

            Operation delete = default;
            delete.Kind = OpKind.Delete;
            delete.Node = gone;
            Do(ref file, delete, state, 0);
        }
    }

    static int Dangling(in Source src)
    {
        NodeId[] found = null;
        return SourceWalk.Dangling(in src, ref found);
    }

    /// <summary>Applies one operation through the log and reports it, naming the line it came from when refused.</summary>
    static NodeId Do(ref SourceFile file, Operation op, State state, int line, string note = null)
    {
        try
        {
            Log.Do(ref file.Log, ref file.Src, op);
        }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
        {
            throw line > 0 ? Line(line, error.Message) : new ArgumentException(error.Message);
        }

        Operation applied = file.Log.Entries[file.Log.At - 1].Op;
        state.Steps++;
        state.Touched.Add(applied.Node);
        state.Report.Append(state.Steps).Append("  ").Append(applied.Kind).Append("  ").Append(applied.Node)
            .Append(Program.Describe(in file, in applied));
        if (note != null) state.Report.Append("  ").Append(note);
        state.Report.AppendLine();
        return applied.Node;
    }

    static ArgumentException Line(int line, string message)
    {
        return new ArgumentException("line " + line + ": " + message);
    }
}
