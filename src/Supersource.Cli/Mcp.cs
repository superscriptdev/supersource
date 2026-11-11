using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Supersource.Cli;

/// <summary>
/// One parameter of a tool, and how it reaches the command line.
///
/// A parameter states three things about itself: whether it is positional or a flag, whether a
/// flag carries a value, and whether it is required. The argv and the JSON schema are both
/// derived from those.
/// </summary>
public struct McpParam
{
    /// <summary>What the caller names it in `arguments`.</summary>
    public string Name;

    /// <summary>The flag as the CLI spells it, or null when this is a positional.</summary>
    public string Flag;

    /// <summary>A flag that is present or absent rather than one carrying a value.</summary>
    public bool Boolean;

    public bool Required;

    /// <summary>Split on whitespace into several positionals; see <see cref="Rest"/>.</summary>
    public bool Words;

    public string About;

    /// <summary>A positional argument, in the order the verb expects it.</summary>
    public static McpParam At(string name, string about, bool required = true)
    {
        McpParam p = default;
        p.Name = name;
        p.About = about;
        p.Required = required;
        return p;
    }

    /// <summary>
    /// Trailing positionals given as one space-separated string, each word its own argument.
    /// Last in a tool's table, and optional.
    /// </summary>
    public static McpParam Rest(string name, string about)
    {
        McpParam p = At(name, about, false);
        p.Words = true;
        return p;
    }

    /// <summary>A flag carrying a value: `--name value`.</summary>
    public static McpParam Option(string name, string about)
    {
        McpParam p = default;
        p.Name = name;
        p.Flag = "--" + name;
        p.About = about;
        return p;
    }

    /// <summary>A flag that is present or absent: `--name`.</summary>
    public static McpParam Switch(string name, string about)
    {
        McpParam p = default;
        p.Name = name;
        p.Flag = "--" + name;
        p.Boolean = true;
        p.About = about;
        return p;
    }
}

/// <summary>One tool an agent may call, and the argv it becomes.</summary>
public struct McpTool
{
    /// <summary>What the agent calls it. Dotted, so related tools read as a family.</summary>
    public string Name;

    public string About;

    /// <summary>The verb, as words. `Takes` follows them, in order.</summary>
    public string[] Verb;

    public McpParam[] Takes;

    /// <summary>Whether it can change a file. Stated in the generated description.</summary>
    public bool Writes;

    public static McpTool Read(string name, string about, string[] verb, params McpParam[] takes)
    {
        McpTool t = default;
        t.Name = name;
        t.About = about;
        t.Verb = verb;
        t.Takes = takes ?? Array.Empty<McpParam>();
        return t;
    }

    public static McpTool Write(string name, string about, string[] verb, params McpParam[] takes)
    {
        McpTool t = Read(name, about, verb, takes);
        t.Writes = true;
        return t;
    }
}

/// <summary>
/// Speak MCP on stdio over a CLI's own verbs.
///
/// The verbs are the tools. Nothing here knows what a verb does: a tool is a name, a verb and an
/// ordered parameter list, and the answer is whatever the CLI printed. A verb becomes callable
/// by gaining a table row.
///
/// Nothing may write to stdout while a verb runs. This speaks JSON-RPC over it, so a verb's own
/// output there is a protocol error rather than noise; <see cref="Call"/> redirects both streams
/// for the duration and hands back what it caught.
/// </summary>
public static class Mcp
{
    const string Protocol = "2024-11-05";

    /// <summary>
    /// Read requests from stdin and answer on stdout, until the stream ends. Line-delimited
    /// JSON rather than the header framing.
    /// </summary>
    public static int Serve(McpTool[] tools, Func<string[], int> run, string name = "supersource")
    {
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            // A client whose writer opens with a byte-order mark sends one before the first request.
            line = line.TrimStart('﻿');
            if (line.Trim().Length == 0) continue;

            string answer = Answer(line, tools, run, name);
            if (answer != null)
            {
                Console.Out.WriteLine(answer);
                Console.Out.Flush();
            }
        }

        return 0;
    }

    /// <summary>
    /// One request, one answer — or null for a notification, which is answered with silence.
    /// </summary>
    public static string Answer(
        string request, McpTool[] tools, Func<string[], int> run, string name = "supersource")
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(request);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "not JSON");
        }

        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            bool hasId = root.TryGetProperty("id", out JsonElement idElement);
            string id = hasId ? idElement.GetRawText() : null;

            if (!root.TryGetProperty("method", out JsonElement methodElement))
            {
                return Error(id, -32600, "no method");
            }

            // A notification has no id and wants no answer; `notifications/initialized`
            // arrives on every connection.
            if (!hasId) return null;

            switch (methodElement.GetString())
            {
                case "initialize": return Initialize(id, name);
                case "tools/list": return ToolList(id, tools);
                case "tools/call": return ToolCall(id, root, tools, run);
                case "ping": return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}";
                default:
                    return Error(id, -32601, "no such method: " + methodElement.GetString());
            }
        }
    }

    /// <summary>The tool with this name, or false.</summary>
    public static bool Find(McpTool[] tools, string name, out McpTool found)
    {
        found = default;
        for (int i = 0; i < tools.Length; i++)
        {
            if (tools[i].Name == name) { found = tools[i]; return true; }
        }

        return false;
    }

    /// <summary>
    /// The argv a call becomes, or null with the reason when the arguments do not fit.
    ///
    /// Positionals are appended in declaration order, and a missing required one is an error
    /// rather than a gap to skip. An optional positional is only valid as the last one, which
    /// <see cref="Check"/> enforces.
    /// </summary>
    public static string[] Argv(in McpTool tool, Func<string, JsonElement?> argument, out string why)
    {
        why = null;
        var argv = new List<string>(tool.Verb);
        var flags = new List<string>();

        for (int i = 0; i < tool.Takes.Length; i++)
        {
            McpParam take = tool.Takes[i];
            JsonElement? given = argument(take.Name);

            if (given == null)
            {
                if (take.Required)
                {
                    why = "missing required argument: " + take.Name;
                    return null;
                }

                continue;
            }

            if (take.Boolean)
            {
                // Present and false is absent: `{"check": false}` appends no flag.
                if (given.Value.ValueKind == JsonValueKind.False) continue;
                if (given.Value.ValueKind != JsonValueKind.True)
                {
                    why = take.Name + " is a flag; pass true or false";
                    return null;
                }

                flags.Add(take.Flag);
                continue;
            }

            string value = given.Value.ValueKind == JsonValueKind.String
                ? given.Value.GetString()
                : given.Value.GetRawText();

            if (string.IsNullOrEmpty(value))
            {
                why = take.Name + " is empty";
                return null;
            }

            if (take.Words)
            {
                foreach (string word in value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)) argv.Add(word);
            }
            else if (take.Flag == null) argv.Add(value);
            else { flags.Add(take.Flag); flags.Add(value); }
        }

        // Flags after every positional: a verb reads its positionals by index, and some scan
        // the tail for options.
        argv.AddRange(flags);
        return argv.ToArray();
    }

    /// <summary>
    /// What a tool table has to be true for: no duplicate names, and no optional positional
    /// before a required one. Called from the tests of every layer that publishes a table.
    /// </summary>
    public static void Check(McpTool[] tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tools.Length; i++)
        {
            McpTool tool = tools[i];
            if (string.IsNullOrEmpty(tool.Name))
            {
                throw new ArgumentException("a tool with no name");
            }

            if (!names.Add(tool.Name))
            {
                throw new ArgumentException("two tools named " + tool.Name);
            }

            if (tool.Verb == null || tool.Verb.Length == 0)
            {
                throw new ArgumentException(tool.Name + " names no verb");
            }

            bool optionalSeen = false;
            for (int t = 0; t < tool.Takes.Length; t++)
            {
                McpParam take = tool.Takes[t];
                if (string.IsNullOrEmpty(take.Name))
                {
                    throw new ArgumentException(tool.Name + " has a parameter with no name");
                }

                if (take.Flag != null) continue;

                if (optionalSeen && take.Required)
                {
                    throw new ArgumentException(
                        tool.Name + ": required positional \"" + take.Name
                        + "\" follows an optional one, so argv would be off by a position");
                }

                optionalSeen |= !take.Required;
            }
        }
    }

    /// <summary>
    /// Run the verb and give back what it printed, both streams: a verb that refuses says why
    /// on stderr.
    /// </summary>
    public static string Call(Func<string[], int> run, string[] argv, out bool ok)
    {
        TextWriter outWas = Console.Out;
        TextWriter errWas = Console.Error;
        var into = new StringWriter();
        int code;

        try
        {
            Console.SetOut(into);
            Console.SetError(into);
            code = run(argv);
        }
        catch (Exception error)
        {
            ok = false;
            return error.Message;
        }
        finally
        {
            Console.SetOut(outWas);
            Console.SetError(errWas);
        }

        ok = code == 0;
        string said = into.ToString();
        return said.Length == 0 ? (ok ? "done" : "failed") : said;
    }

    static string Initialize(string id, string name)
    {
        var into = new StringBuilder();
        into.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id);
        into.Append(",\"result\":{\"protocolVersion\":\"").Append(Protocol).Append('"');
        into.Append(",\"capabilities\":{\"tools\":{}}");
        into.Append(",\"serverInfo\":{\"name\":").Append(Quote(name)).Append(",\"version\":\"1\"}}}");
        return into.ToString();
    }

    static string ToolList(string id, McpTool[] tools)
    {
        var into = new StringBuilder();
        into.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id).Append(",\"result\":{\"tools\":[");

        for (int i = 0; i < tools.Length; i++)
        {
            if (i != 0) into.Append(',');
            McpTool tool = tools[i];

            into.Append("{\"name\":").Append(Quote(tool.Name));
            into.Append(",\"description\":").Append(Quote(
                tool.About + (tool.Writes ? " Writes a file." : "")));
            into.Append(",\"inputSchema\":{\"type\":\"object\",\"properties\":{");

            var required = new List<string>();
            for (int t = 0; t < tool.Takes.Length; t++)
            {
                McpParam take = tool.Takes[t];
                if (t != 0) into.Append(',');
                into.Append(Quote(take.Name)).Append(":{\"type\":")
                    .Append(take.Boolean ? "\"boolean\"" : "\"string\"")
                    .Append(",\"description\":").Append(Quote(take.About)).Append('}');
                if (take.Required) required.Add(take.Name);
            }

            into.Append("},\"required\":[");
            for (int r = 0; r < required.Count; r++)
            {
                if (r != 0) into.Append(',');
                into.Append(Quote(required[r]));
            }

            into.Append("]}}");
        }

        into.Append("]}}");
        return into.ToString();
    }

    static string ToolCall(string id, JsonElement root, McpTool[] tools, Func<string[], int> run)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters))
        {
            return Error(id, -32602, "no params");
        }

        if (!parameters.TryGetProperty("name", out JsonElement nameElement))
        {
            return Error(id, -32602, "no tool name");
        }

        if (!Find(tools, nameElement.GetString(), out McpTool tool))
        {
            return Error(id, -32602, "no such tool: " + nameElement.GetString());
        }

        bool hasArguments = parameters.TryGetProperty("arguments", out JsonElement arguments);
        JsonElement captured = arguments;

        string[] argv = Argv(in tool, name =>
        {
            if (!hasArguments || !captured.TryGetProperty(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Null ? null : (JsonElement?)value;
        }, out string why);

        // A bad call is reported as a result rather than a transport error: something the
        // agent can read and correct.
        if (argv == null) return Content(id, why, true);

        string said = Call(run, argv, out bool ok);
        return Content(id, said, !ok);
    }

    /// <summary>
    /// An answer, as text. A verb that refused is reported with `isError` rather than as a
    /// failed call, so the refusal itself reaches the agent.
    /// </summary>
    static string Content(string id, string text, bool bad)
    {
        var into = new StringBuilder();
        into.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id);
        into.Append(",\"result\":{\"content\":[{\"type\":\"text\",\"text\":");
        into.Append(Quote(text));
        into.Append("}],\"isError\":").Append(bad ? "true" : "false").Append("}}");
        return into.ToString();
    }

    static string Error(string id, int code, string message)
    {
        var into = new StringBuilder();
        into.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id ?? "null");
        into.Append(",\"error\":{\"code\":").Append(code);
        into.Append(",\"message\":").Append(Quote(message)).Append("}}");
        return into.ToString();
    }

    static string Quote(string text)
    {
        var into = new StringBuilder("\"");
        string body = text ?? string.Empty;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            switch (c)
            {
                case '"': into.Append("\\\""); break;
                case '\\': into.Append("\\\\"); break;
                case '\n': into.Append("\\n"); break;
                case '\r': into.Append("\\r"); break;
                case '\t': into.Append("\\t"); break;
                default:
                    if (c < 0x20) into.Append("\\u").Append(((int)c).ToString("x4"));
                    else into.Append(c);
                    break;
            }
        }

        return into.Append('"').ToString();
    }

    // ---- the format's own tools ---------------------------------------------------------

    /// <summary>
    /// The verbs answered as tools: every verb but `roundtrip`, `upgrade` and `merge`, which the
    /// command line still answers.
    /// </summary>
    public static McpTool[] Tools()
    {
        McpParam file = McpParam.At("file", "path to the .ss file");
        McpParam node = McpParam.At("node", "a node, as `n12` or `12`");

        return new[]
        {
            McpTool.Read("source.stat", "Counts, payload sizes, and how much of it this build can interpret.",
                new[] { "stat" }, file),
            McpTool.Read("source.cat", "The whole graph, as text.", new[] { "cat" }, file),
            McpTool.Read("source.node", "One node, its children and its references.",
                new[] { "node" }, file, node),
            McpTool.Read("source.validate", "Structural integrity only; source.rules checks the rest.",
                new[] { "validate" }, file),
            McpTool.Read("source.rules", "Source rules and schema conformance.", new[] { "rules" }, file),
            McpTool.Read("source.missing", "Unbound and dangling references. A valid state, not an error.",
                new[] { "missing" }, file),
            McpTool.Read("source.refs", "What references a node.", new[] { "refs" }, file, node),
            McpTool.Read("source.kinds", "The kind schemas the source carries.", new[] { "kinds" }, file),
            McpTool.Read("source.find", "Nodes by local kind name, and optionally by payload text.",
                new[] { "find" }, file, McpParam.At("kind", "a local kind name"),
                McpParam.At("text", "payload text to match", false)),
            McpTool.Read("source.sections", "Section sizes, and sections this build did not understand.",
                new[] { "sections" }, file),
            McpTool.Read("source.verify", "Whether the stored digest matches.", new[] { "verify" }, file),
            McpTool.Read("source.diff", "What changed between two states of one source.",
                new[] { "diff" }, McpParam.At("before", "path to the earlier state"),
                McpParam.At("after", "path to the later state")),
            McpTool.Read("source.deps", "External references, and whether they resolve against the libraries given.",
                new[] { "deps" }, file,
                McpParam.Rest("libs", "paths to the sources to resolve against, separated by spaces")),

            McpTool.Write("source.new",
                "Create a source with a root of the named kind: one seeded from the source `kinds` "
                + "names, or else a plain name, which becomes a kind of the new source's own with a "
                + "text payload.",
                new[] { "new" },
                McpParam.At("file", "path to write the .ss file to"),
                McpParam.Option("root", "the root node's kind"),
                McpParam.Option("label", "UTF-8 payload for the root, if its kind carries text"),
                McpParam.Option("kinds", "a source whose embedded schemas to seed the table from"),
                McpParam.Option("identity", "\"mint\" (the default) or 32 hex digits"),
                McpParam.Switch("force", "replace the destination if it exists")),

            McpTool.Write("source.declare",
                "Declare this library's kinds and relations in a source, which makes that source "
                + "a kind library: its own identity qualifies every name it declares. A kind is a "
                + "name, its structural facts, its payload format "
                + "and the relations it uses. Changing a kind already in use is refused.",
                new[] { "declare" },
                McpParam.At("file", "path to the .ss library, which must already exist"),
                McpParam.At("declaration", "path to the declaration, as JSON"),
                McpParam.Switch("dry-run", "check the declaration and write nothing")),

            McpTool.Write("source.edit",
                "Apply a checked batch of structural edits: insert, set-payload, splice, "
                + "move, retype, delete, connect, disconnect, extern. Checks structure, and the "
                + "payload and edge formats of the nodes it touches; it knows nothing of what a "
                + "kind means.",
                new[] { "edit" }, file,
                McpParam.At("edits", "path to a JSON array of edit operations"),
                McpParam.Option("kinds", "a source whose embedded schemas to seed kind names from"),
                McpParam.Switch("dry-run", "check the batch and write nothing"),
                McpParam.Option("expect-hash", "refuse unless the file's sha256 matches")),
        };
    }
}
