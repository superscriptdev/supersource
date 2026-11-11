using System;
using System.IO;
using Supersource.Cli;
using Xunit;

namespace Supersource.Cli.Tests;

/// <summary>
/// `new` and `declare`: creating a document, and declaring the kinds it is made of.
/// </summary>
public class Spec_Declaring : IDisposable
{
    readonly string dir;

    public Spec_Declaring()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-declaring-" + Guid.NewGuid().ToString("N"));
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

    string At(string name)
    {
        return Path.Combine(dir, name);
    }

    string Declaration(string name, string json)
    {
        string path = At(name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Three prose-shaped kinds and the relation one of them uses.</summary>
    const string Prose = """
    {
      "relations": [
        { "name": "documents", "payload": "none" }
      ],
      "kinds": [
        { "name": "article", "facts": ["child-order-significant"],
          "payload": "text", "uses": ["documents"] },
        { "name": "section", "payload": "text" },
        { "name": "paragraph", "facts": ["child-order-significant"], "payload": "text" },
        { "name": "rule" }
      ]
    }
    """;

    /// <summary>A library: a document that holds the kinds, made and then declared into.</summary>
    string Library(string name = "prose.ss")
    {
        string path = At(name);
        Assert.Equal(0, Run(new[] { "new", path, "--root", "notes", "--label", "prose" }, out _, out _));
        Assert.Equal(0, Run(new[] { "declare", path, Declaration("prose.json", Prose) }, out _, out _));
        return path;
    }

    // ---- new ---------------------------------------------------------------------------------

    [Fact]
    public void New_creates_a_document_rooted_at_the_named_kind()
    {
        string path = At("one.ss");
        Assert.Equal(0, Run(new[] { "new", path, "--root", "notes", "--label", "start" },
            out string said, out string errors));

        Assert.Equal("", errors);
        Assert.Contains("root       n0  notes", said);
        Assert.True(File.Exists(path));

        Assert.Equal(0, Run(new[] { "cat", path }, out string graph, out _));
        Assert.Contains("start", graph);
    }

    [Fact]
    public void New_embeds_the_schemas_it_needs()
    {
        // Asked of the file: the root's kind is the document's own, so only its embedded
        // schema says what it is.
        string path = At("one.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);

        Assert.Equal(0, Run(new[] { "kinds", path }, out string carried, out _));
        Assert.Contains("notes", carried);

        Assert.Equal(0, Run(new[] { "sections", path }, out string sections, out _));
        Assert.DoesNotContain("unknown", sections.ToLowerInvariant());
    }

    [Fact]
    public void New_refuses_an_existing_destination_unless_forced()
    {
        string path = At("one.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);

        Assert.Equal(3, Run(new[] { "new", path, "--root", "notes" }, out _, out string errors));
        Assert.Contains("exists", errors);

        Assert.Equal(0, Run(new[] { "new", path, "--root", "notes", "--force" }, out _, out _));
    }

    [Fact]
    public void An_identity_is_minted_unless_one_is_given()
    {
        // §5.1: a minted identity lets a reference to the document survive the file moving.
        // The explicit form is for a library whose consumers pin it.
        string one = At("one.ss");
        string two = At("two.ss");
        Run(new[] { "new", one, "--root", "notes" }, out string first, out _);
        Run(new[] { "new", two, "--root", "notes" }, out string second, out _);
        Assert.NotEqual(Identity(first), Identity(second));

        string fixed_ = At("three.ss");
        Run(new[] { "new", fixed_, "--root", "notes", "--identity", "00000000000000000000000000000abc" },
            out string stated, out _);
        Assert.Contains("abc", Identity(stated));
    }

    static string Identity(string said)
    {
        foreach (string line in said.Split('\n'))
        {
            if (line.StartsWith("source", StringComparison.Ordinal)) return line.Trim();
        }

        return "";
    }

    [Fact]
    public void A_label_needs_a_root_that_can_hold_text()
    {
        // A rule carries no payload, so a label on it would be bytes its schema says are not
        // there.
        string library = Library();
        Assert.Equal(1, Run(new[] { "new", At("one.ss"), "--root", "rule", "--kinds", library, "--label", "x" },
            out _, out string errors));
        Assert.Contains("text", errors);
        Assert.False(File.Exists(At("one.ss")));
    }

    [Fact]
    public void A_root_no_library_supplies_is_a_kind_of_the_documents_own()
    {
        // A data document needs no other library's kind to start from.
        string path = At("accounts.ss");
        Assert.Equal(0, Run(new[] { "new", path, "--root", "accounts", "--identity",
            "00000000000000000000000000000abc" }, out _, out _));

        Assert.Equal(0, Run(new[] { "kinds", path }, out string kinds, out _));
        Assert.Contains("accounts", kinds);
        Assert.Contains("00000000000000000000000000000abc", kinds);
    }

    [Fact]
    public void A_qualified_root_no_library_supplies_names_what_to_do_about_it()
    {
        Assert.Equal(1, Run(new[] { "new", At("one.ss"), "--root", "prose/article" }, out _, out string errors));
        Assert.Contains("prose/article", errors);
        Assert.False(File.Exists(At("one.ss")));
    }

    // ---- declare -----------------------------------------------------------------------------

    [Fact]
    public void Declare_puts_the_kinds_and_relations_in_the_library()
    {
        string path = At("prose.ss");
        Run(new[] { "new", path, "--root", "notes", "--label", "prose" }, out _, out _);

        Assert.Equal(0, Run(new[] { "declare", path, Declaration("prose.json", Prose) },
            out string said, out string errors));
        Assert.Equal("", errors);
        Assert.Contains("4 kind(s), 1 relation(s)", said);

        Assert.Equal(0, Run(new[] { "kinds", path }, out string carried, out _));
        Assert.Contains("article", carried);
        Assert.Contains("ChildOrderSignificant", carried);
        Assert.Contains("documents", carried);
    }

    [Fact]
    public void A_library_needs_no_node_for_the_kinds_it_declares()
    {
        // A source's own kinds are written with it, so declaring adds no node.
        string path = Library();

        foreach (string kind in new[] { "article", "section", "paragraph" })
        {
            Assert.Equal(0, Run(new[] { "find", path, kind }, out string found, out _));
            Assert.Contains("0 node(s)", found);
        }

        // A build that compiled no kind library reads those names back out of the file.
        Assert.Equal(0, Run(new[] { "kinds", path }, out string carried, out _));
        Assert.Contains("paragraph", carried);
    }

    [Fact]
    public void Declaring_a_kind_this_library_already_has_is_refused()
    {
        string path = Library();
        string again = Declaration("one.json",
            """{"kinds": [{"name": "article", "payload": "text"}]}""");

        // Redefining a kind reinterprets the bytes of every node already carrying it, which
        // is a migration rather than a declaration.
        Assert.Equal(1, Run(new[] { "declare", path, again }, out _, out string errors));
        Assert.Contains("already declares", errors);
    }

    [Fact]
    public void A_misspelled_fact_is_refused_and_names_the_choices()
    {
        string path = At("prose.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);
        string bad = Declaration("bad.json",
            """{"kinds": [{"name": "x", "facts": ["declarashun"]}]}""");

        Assert.Equal(1, Run(new[] { "declare", path, bad }, out _, out string errors));
        Assert.Contains("unknown fact: declarashun", errors);
        Assert.Contains("annotation", errors);
    }

    [Fact]
    public void Elements_and_fields_are_declared_and_read_back()
    {
        string path = At("mesh.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);
        string mesh = Declaration("mesh.json", """
            {"kinds": [
              {"name": "count", "payload": {"one": "u32"}},
              {"name": "vertices", "payload": {
                "many": {"size": 16, "fields": [
                  {"name": "position", "type": "f32", "offset": 0, "repeat": 3},
                  {"name": "visible", "type": "bool", "offset": 12}]},
                "header": {"size": 4, "fields": [{"name": "count", "type": "u32", "offset": 0}]}}}
            ]}
            """);

        Assert.Equal(0, Run(new[] { "declare", path, mesh }, out _, out string errors));
        Assert.Equal("", errors);

        Assert.Equal(0, Run(new[] { "kinds", path }, out string carried, out _));
        Assert.Contains("one u32", carried);
        Assert.Contains("many {position f32×3 @0, visible bool @12} 16 after {count u32 @0} 4", carried);
    }

    [Fact]
    public void A_field_past_the_end_of_its_element_is_refused()
    {
        string path = At("bad.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);
        string bad = Declaration("bad.json",
            """{"kinds": [{"name": "x", "payload": {"one": {"size": 4, "fields": [{"name": "v", "type": "f64", "offset": 0}]}}}]}""");

        Assert.Equal(1, Run(new[] { "declare", path, bad }, out _, out string errors));
        Assert.Contains("past the element", errors);
    }

    [Fact]
    public void An_unknown_field_is_refused_and_names_it()
    {
        string path = At("prose.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);
        string bad = Declaration("bad.json",
            """{"kinds": [{"name": "x", "payloadd": "text"}]}""");

        Assert.Equal(1, Run(new[] { "declare", path, bad }, out _, out string errors));
        Assert.Contains("unknown field: payloadd", errors);
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        string path = At("prose.ss");
        Run(new[] { "new", path, "--root", "notes" }, out _, out _);
        byte[] before = File.ReadAllBytes(path);

        Assert.Equal(0, Run(new[] { "declare", path, Declaration("prose.json", Prose), "--dry-run" },
            out string said, out _));
        Assert.Contains("nothing written", said);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Declare_needs_a_source_to_declare_into()
    {
        Assert.Equal(1, Run(new[] { "declare", At("nothing.ss"), Declaration("p.json", Prose) },
            out _, out string errors));
        Assert.Contains("make one with `new`", errors);
    }

    // ---- the round trip ----------------------------------------------------------------------

    [Fact]
    public void A_declared_kind_survives_into_a_fresh_document()
    {
        // A kind reaches a second document through a file: a build that compiled no kind
        // library reads it back by name, with its facts, payload format and the relation
        // it uses — the §22 stranger, applied to a kind rather than to a node.
        string library = Library();
        string document = At("doc.ss");

        Assert.Equal(0, Run(new[] { "new", document, "--root", "article", "--kinds", library,
            "--label", "On Formats" }, out string said, out string errors));
        Assert.Equal("", errors);
        Assert.Contains("seeded from", said);
        Assert.Contains("root       n0  article", said);

        Assert.Equal(0, Run(new[] { "stat", document }, out string stat, out _));
        Assert.Contains("uninterpretable  0", stat);

        Assert.Equal(0, Run(new[] { "kinds", document }, out string carried, out _));
        Assert.Contains("article", carried);
        Assert.Contains("ChildOrderSignificant", carried);
        Assert.Contains("text", carried);
        Assert.Contains("documents", carried);
    }
}
