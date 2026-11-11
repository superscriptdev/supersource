using System;
using System.IO;
using Supersource.Cli;
using Xunit;

namespace Supersource.Cli.Tests;

/// <summary>
/// `extern`: a reference to a node in another source, authored as an edit rather than built.
/// §5.1 gives a source an identity, so the reference survives the file moving; `deps` reads
/// these nodes and resolution follows them.
/// </summary>
public class Spec_Extern : IDisposable
{
    readonly string dir;

    public Spec_Extern()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-extern-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static int Run(string[] args, out string output, out string errors)
    {
        TextWriter wasOut = Console.Out, wasError = Console.Error;
        StringWriter captured = new(), complaints = new();
        Console.SetOut(captured);
        Console.SetError(complaints);
        try
        {
            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(wasOut);
            Console.SetError(wasError);
            output = captured.ToString();
            errors = complaints.ToString();
        }
    }

    string At(string name) => Path.Combine(dir, name);

    const string Identity = "0000000000000000000000000000beef";

    /// <summary>A source with a stated identity, so a reference can name it.</summary>
    string Target()
    {
        string path = At("lib.ss");
        Assert.Equal(0, Run(new[] { "new", path, "--root", "notes", "--label", "lib",
            "--identity", Identity }, out _, out _));
        return path;
    }

    string Batch(string name, string json)
    {
        string path = At(name);
        File.WriteAllText(path, json);
        return path;
    }

    string Importer(string name = "importer.ss")
    {
        string path = At(name);
        Assert.Equal(0, Run(new[] { "new", path, "--root", "notes", "--label", "importer" },
            out _, out _));

        // A relation of its own, so an ordinary edge has something to mean.
        Assert.Equal(0, Run(new[] { "declare", path, Batch(name + ".kinds.json",
            """{"relations": [{"name": "uses"}], "kinds": [{"name": "note", "payload": "text", "uses": ["uses"]}]}""") },
            out _, out _));
        return path;
    }

    [Fact]
    public void An_authored_reference_resolves_against_its_target()
    {
        string target = Target();
        string importer = Importer();

        Assert.Equal(0, Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                 ""node"":0,""locator"":""lib.ss"",""as"":""lib_root""}}]") },
            out _, out string errors));
        Assert.Equal("", errors);

        Assert.Equal(0, Run(new[] { "deps", importer, target }, out string said, out _));
        Assert.Contains("bound", said);
        Assert.Contains("0 unresolved", said);
    }

    static string Tool(string name, string arguments)
    {
        string answer = Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":"
            + "{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}",
            Mcp.Tools(), Program.Main);
        System.Text.Json.JsonElement result =
            System.Text.Json.JsonDocument.Parse(answer).RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(),
            result.GetProperty("content")[0].GetProperty("text").GetString());
        return result.GetProperty("content")[0].GetProperty("text").GetString();
    }

    static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    [Fact]
    public void The_deps_tool_resolves_against_the_libraries_it_is_given()
    {
        string target = Target();
        string importer = Importer();
        Assert.Equal(0, Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":0}}]") },
            out _, out _));

        string said = Tool("source.deps", "{\"file\":" + Json(importer) + ",\"libs\":" + Json(target) + "}");
        Assert.Contains("0 unresolved", said);
    }

    [Fact]
    public void The_edit_tool_seeds_kinds_from_another_source()
    {
        string importer = Importer();
        string kinds = At("kinds.ss");
        Assert.Equal(0, Run(new[] { "new", kinds, "--root", "catalogue" }, out _, out _));
        Assert.Equal(0, Run(new[] { "declare", kinds, Batch("card.json",
            """{"kinds": [{"name": "card", "payload": "text"}]}""") }, out _, out _));

        string edits = Batch("insert.json", """[{"op":"insert","parent":"n0","kind":"card","text":"seeded"}]""");
        Tool("source.edit", "{\"file\":" + Json(importer) + ",\"edits\":" + Json(edits)
            + ",\"kinds\":" + Json(kinds) + "}");

        Assert.Equal(0, Run(new[] { "find", importer, "card" }, out string found, out _));
        Assert.Contains("seeded", found);
    }

    [Fact]
    public void An_unresolved_reference_is_named_in_the_formats_words()
    {
        string importer = Importer();
        Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":0}}]") },
            out _, out _);

        Assert.Equal(0, Run(new[] { "deps", importer }, out string said, out _));
        Assert.Contains("1 unresolved", said);
        Assert.DoesNotContain("hole", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reference_with_no_target_present_is_a_hole_not_an_error()
    {
        // §6: an unresolved reference is a valid source that says what is missing.
        string importer = Importer();
        Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":0}}]") },
            out _, out _);

        Assert.Equal(0, Run(new[] { "deps", importer }, out string said, out _));
        Assert.Contains("1 cross-source reference", said);
    }

    [Fact]
    public void The_document_still_says_what_its_own_nodes_are()
    {
        // The op widens the kind table, so the external-ref schema is embedded before the file
        // saves (§4.5).
        string importer = Importer();
        Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":0}}]") },
            out _, out _);

        Assert.Equal(0, Run(new[] { "stat", importer }, out string said, out _));
        Assert.Contains("uninterpretable  0", said);

        Assert.Equal(0, Run(new[] { "kinds", importer }, out string carried, out _));
        Assert.Contains("external-ref", carried);
    }

    [Fact]
    public void An_ordinary_edge_points_at_the_handle()
    {
        // Ordinary local edges point at these nodes: a reference across sources is drawn
        // exactly as one within a source.
        string importer = Importer();
        string batch = Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                  ""node"":0,""as"":""lib_root""}},
                {{""op"":""connect"",""node"":""n0"",""relation"":""uses"",
                  ""target"":""@lib_root"",""index"":0}}]");

        Assert.Equal(0, Run(new[] { "edit", importer, batch }, out string said, out string errors));
        Assert.Equal("", errors);
        Assert.Contains("Connect", said);

        Assert.Equal(0, Run(new[] { "refs", importer, "n1" }, out string referrers, out _));
        Assert.Contains("n0", referrers);
    }

    [Fact]
    public void A_hash_is_carried_and_a_malformed_one_is_refused()
    {
        string importer = Importer();
        string good = new string('a', 64);

        Assert.Equal(0, Run(new[] { "edit", importer, Batch("ok.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                  ""node"":0,""hash"":""{good}""}}]") }, out _, out _));

        // The address is the dependency's, printed as the bytes it is.
        Assert.Equal(0, Run(new[] { "find", importer, "dependency" }, out string found, out _));
        Assert.Contains("hash " + good, found);
        Assert.Contains("source " + Identity, found);

        string other = Importer("other.ss");
        Assert.Equal(1, Run(new[] { "edit", other, Batch("bad.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                  ""node"":0,""hash"":""abc""}}]") }, out _, out string errors));
        Assert.Contains("64 hex", errors);
    }

    [Fact]
    public void An_unminted_source_cannot_be_referred_to()
    {
        // All zeroes is `SourceId.None`: legal for a source nobody points at (§5.1), and never
        // a target.
        string importer = Importer();
        Assert.Equal(1, Run(new[] { "edit", importer, Batch("bad.json",
            @"[{""op"":""extern"",""parent"":""n0"",""index"":0,
                ""source"":""00000000000000000000000000000000"",""node"":0}]") },
            out _, out string errors));
        Assert.Contains("unminted", errors);
    }

    [Fact]
    public void References_into_one_source_share_its_dependency()
    {
        string importer = Importer();
        Assert.Equal(0, Run(new[] { "edit", importer, Batch("ext.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":0}},
                {{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",""node"":1}}]") },
            out _, out string errors));
        Assert.Equal("", errors);

        Assert.Equal(0, Run(new[] { "find", importer, "dependency" }, out string found, out _));
        Assert.Contains("1 node(s)", found);
        Assert.Equal(0, Run(new[] { "deps", importer }, out string said, out _));
        Assert.Contains("2 cross-source reference", said);
    }

    [Fact]
    public void A_pin_label_is_not_part_of_a_reference()
    {
        // The hash says which version; a free-text label beside it could only disagree with it.
        string importer = Importer();
        Assert.Equal(1, Run(new[] { "edit", importer, Batch("bad.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                  ""node"":0,""pin"":""2.1""}}]") }, out _, out string errors));
        Assert.Contains("unknown field: pin", errors);
    }

    [Fact]
    public void A_misspelled_field_is_refused()
    {
        string importer = Importer();
        Assert.Equal(1, Run(new[] { "edit", importer, Batch("bad.json",
            $@"[{{""op"":""extern"",""parent"":""n0"",""index"":0,""source"":""{Identity}"",
                  ""nodee"":0}}]") }, out _, out string errors));
        Assert.Contains("unknown field: nodee", errors);
    }
}
