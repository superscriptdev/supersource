using System;
using System.IO;
using System.Text;
using Supersource;
using Supersource.Cli;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Supersource.Cli.Tests;

public class Spec_Cli : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "supersource-cli-" + Guid.NewGuid());
    readonly string path;
    readonly byte[] original;

    public Spec_Cli()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "sample source.ss");
        var file = SourceFile.Create();
        var library = new SourceId(17, 23);
        var kind = KindTable.Intern(ref file.Table, new QualifiedName(library, "item"));
        var relation = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "about"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, PayloadFormat.Text, new[] { relation });
        SourceBuild.MintIdentity(ref file.Src);
        var root = SourceBuild.AddRoot(ref file.Src, kind);
        var child = SourceBuild.AddChild(ref file.Src, root, kind);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("root payload"));
        SourceBuild.SetPayload(ref file.Src, child, Encoding.UTF8.GetBytes("child payload"));
        SourceBuild.AddRef(ref file.Src, root, relation, child);
        original = SourceWrite.ToBytes(in file);
        File.WriteAllBytes(path, original);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_succeeds_without_opening_a_file(string flag)
    {
        var result = Run(flag);
        Assert.Equal(0, result.Code);
        Assert.Contains("node <file> <n>", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Shared_dispatch_uses_the_calling_tools_help()
    {
        var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Program.Dispatch(new[] { "--help" },
                (string[] args, out int code) => throw new Exception("should not dispatch a verb"),
                writer => writer.Write("outer tool help")));
        }
        finally { Console.SetOut(previous); }
        Assert.Equal("outer tool help", output.ToString());
    }

    [Theory]
    [InlineData("n0")]
    [InlineData("0")]
    public void Node_shows_local_relationships_without_descendant_payloads(string id)
    {
        var result = Run("node", path, id);
        Assert.Equal(0, result.Code);
        Assert.Contains("state live", result.Output);
        Assert.Contains("parent none", result.Output);
        Assert.Contains("root payload", result.Output);
        Assert.Contains("--about--> n1", result.Output);
        Assert.Contains("  n1", result.Output);
        Assert.DoesNotContain("child payload", result.Output);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("n999999")]
    [InlineData("n2147483648")]
    [InlineData("-1")]
    [InlineData("not-a-node")]
    [InlineData("9223372036854775807")]
    public void Invalid_or_missing_node_is_reported_without_modifying_input(string id)
    {
        var result = Run("node", path, id);
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Node_requires_exactly_a_file_and_id()
    {
        Assert.Equal(1, Run("node", path).Code);
        Assert.Equal(1, Run("node", path, "n0", "ignored").Code);
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("unexpected-path.ss")]
    public void Upgrade_rejects_extra_arguments_without_writing(string extra)
    {
        // A usage error, exit 1, like every other verb's.
        Assert.Equal(1, Run("upgrade", path, extra).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".migrating"));
    }

    [Fact]
    public void Node_parser_supports_64_bit_identifiers()
    {
        Assert.Equal(2147483648L, Program.ParseNode("n2147483648").Index);
    }

    [Fact]
    public void Edit_batch_preserves_identity_and_saves_the_resulting_state()
    {
        var before = SourceRead.FromBytes(original);
        long inserted = before.Src.NodeCount;
        string patch = WritePatch("""
            [
              {"op":"insert","parent":"n0","kind":"item","text":"new node","as":"new"},
              {"op":"set-payload","node":"n1","text":"café"},
              {"op":"splice","node":"n1","offset":3,"remove":2,"text":"e"},
              {"op":"set-payload","node":"n0","text":"renamed root"},
              {"op":"move","node":"n1","parent":"@new","index":0},
              {"op":"connect","node":"@new","relation":"about","target":"n0"},
              {"op":"connect","node":"@new","relation":"about","target":"n1"},
              {"op":"disconnect","node":"n0","index":0},
              {"op":"delete","node":"n1"}
            ]
            """);
        var result = Run("edit", path, patch, "--expect-hash", ContentHash.Of(original).ToString());
        Assert.True(result.Code == 0, result.Error);
        var after = SourceRead.FromFile(path);
        try
        {
            Assert.Equal(before.Src.Identity, after.Src.Identity);
            Assert.Equal(before.Src.Root, after.Src.Root);
            Assert.Equal(inserted + 1, after.Src.NodeCount);
            Assert.Equal("renamed root", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, new NodeId(0))));
            // n1 was edited, moved and then deleted: what is left is the id, and nothing else.
            Assert.False(after.Src.IsLive(new NodeId(1)));
            Assert.Equal(0, SourceWalk.Payload(in after.Src, new NodeId(1)).Length);
            Assert.Equal(new NodeId(0), after.Src.Nodes[inserted].Parent);
            Assert.Equal(0, after.Src.Nodes[0].RefCount);
            var refs = SourceWalk.References(in after.Src, new NodeId(inserted));
            Assert.Equal(new NodeId(0), refs[0].Target);
            Assert.Equal(new NodeId(1), refs[1].Target);
            Assert.Equal(0, after.Log.At);
            Assert.True(SourceValidate.Check(in after, out _));
        }
        finally { SourceFile.Close(ref after); }
        // A second saved edit lands on the same source.
        patch = WritePatch("""[{"op":"set-payload","node":"n0","text":"second save"}]""");
        Assert.Equal(0, Run("edit", path, patch).Code);
        after = SourceRead.FromFile(path);
        try
        {
            Assert.Equal("second save", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, new NodeId(0))));
            Assert.Equal(before.Src.Identity, after.Src.Identity);
        }
        finally { SourceFile.Close(ref after); }
    }

    [Fact]
    public void Dry_run_checks_the_batch_without_changing_any_source_bytes()
    {
        string patch = WritePatch("""[{"op":"insert","parent":"n0","kind":"item","as":"new","text":"preview"}]""");
        var result = Run("edit", path, patch, "--dry-run");
        Assert.True(result.Code == 0, result.Error);
        Assert.Contains("@new", result.Output);
        Assert.Contains(ContentHash.Of(original).ToString(), result.Output);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void Stale_expected_hash_refuses_the_edit()
    {
        string patch = WritePatch("""[{"op":"set-payload","node":"n0","text":"changed"}]""");
        Assert.Equal(3, Run("edit", path, patch, "--expect-hash", new string('0', 64)).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("{\"op\":\"move\",\"node\":\"n1\",\"parent\":\"n1\"}")]
    [InlineData("{\"op\":\"delete\",\"node\":\"n0\"}")]
    [InlineData("{\"op\":\"disconnect\",\"node\":\"n0\",\"index\":99}")]
    [InlineData("{\"op\":\"set-payload\",\"node\":\"n99\",\"text\":\"x\"}")]
    [InlineData("{\"op\":\"insert\",\"parent\":\"n0\",\"kind\":\"unknown\"}")]
    [InlineData("{\"op\":\"set-payload\",\"node\":\"n1\",\"base64\":\"/w==\"}")]
    [InlineData("{\"op\":\"set-payload\",\"node\":\"n1\",\"text\":\"x\",\"base64\":\"eA==\"}")]
    [InlineData("{\"op\":\"delete\",\"node\":\"n1\",\"node\":\"n0\"}")]
    [InlineData("{\"op\":\"rename\",\"node\":\"n1\",\"text\":\"x\",\"typo\":true}")]
    [InlineData("{\"op\":\"insert\",\"parent\":\"n0\",\"kind\":\"item\",\"index\":999}")]
    public void Rejected_later_edit_discards_the_entire_batch(string invalid)
    {
        string patch = WritePatch("[{\"op\":\"rename\",\"node\":\"n0\",\"text\":\"would change\"}," + invalid + "]");
        var result = Run("edit", path, patch);
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void Splice_cannot_split_a_utf8_character()
    {
        string patch = WritePatch("""
            [{"op":"set-payload","node":"n1","text":"café"},
             {"op":"splice","node":"n1","offset":4,"remove":1,"text":"x"}]
            """);
        Assert.Equal(1, Run("edit", path, patch).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Unbound_reference_label_survives_save()
    {
        string patch = WritePatch("""[{"op":"connect","node":"n1","relation":"about","target":null,"text":"missing"}]""");
        var result = Run("edit", path, patch);
        Assert.True(result.Code == 0, result.Error);
        var after = SourceRead.FromFile(path);
        try
        {
            var edge = SourceWalk.References(in after.Src, new NodeId(1))[0];
            Assert.True(edge.IsUnbound);
            Assert.Equal("missing", Encoding.UTF8.GetString(SourceWalk.Label(in after.Src, in edge)));
        }
        finally { SourceFile.Close(ref after); }
    }

    [Fact]
    public void Unsupported_flags_and_schema_edits_do_not_save()
    {
        string patch = WritePatch("""[{"op":"delete","node":"n2"}]""");
        Assert.Equal(1, Run("edit", path, patch, "--dryrun").Code);
        Assert.Equal(1, Run("edit", path, patch).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Existing_save_temporary_file_is_preserved()
    {
        File.WriteAllText(path + ".writing", "recoverable work");
        string patch = WritePatch("""[{"op":"set-payload","node":"n0","text":"changed"}]""");
        Assert.Equal(3, Run("edit", path, patch).Code);
        Assert.Equal("recoverable work", File.ReadAllText(path + ".writing"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Ambiguous_kind_requires_qualified_name()
    {
        var file = SourceRead.FromBytes(original);
        var name = new QualifiedName(new SourceId(77, 88), "item");
        var second = KindTable.Intern(ref file.Table, name);
        KindTable.DefineSchema(ref file.Table, second, StructuralFact.None, PayloadFormat.Text, Array.Empty<RelationId>());
        SourceBuild.AddChild(ref file.Src, file.Src.Root, second);
        var bytes = SourceWrite.ToBytes(in file);
        File.WriteAllBytes(path, bytes);
        string patch = WritePatch("""[{"op":"insert","parent":"n0","kind":"item","text":"added"}]""");
        Assert.Equal(1, Run("edit", path, patch).Code);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        patch = WritePatch("[{\"op\":\"insert\",\"parent\":\"n0\",\"kind\":\"" + name + "\",\"text\":\"added\"}]");
        var result = Run("edit", path, patch);
        Assert.True(result.Code == 0, result.Error);
        var after = SourceRead.FromFile(path);
        try
        {
            Assert.Equal(name, KindTable.NameOf(in after.Table, after.Src.Nodes[file.Src.NodeCount].Kind));
        }
        finally { SourceFile.Close(ref after); }
    }

    [Fact]
    public void Binary_payload_bytes_are_preserved_exactly()
    {
        var file = SourceRead.FromBytes(original);
        var binary = KindTable.Intern(ref file.Table, new QualifiedName(new SourceId(77, 88), "binary"));
        KindTable.DefineSchema(ref file.Table, binary, StructuralFact.None, PayloadFormat.Opaque, Array.Empty<RelationId>());
        var node = SourceBuild.AddChild(ref file.Src, file.Src.Root, binary);
        File.WriteAllBytes(path, SourceWrite.ToBytes(in file));
        byte[] wanted = new byte[] { 0, 255, 17, 42 };
        string patch = WritePatch("[{\"op\":\"set-payload\",\"node\":\"" + node + "\",\"base64\":\"" + Convert.ToBase64String(wanted) + "\"}]");
        var result = Run("edit", path, patch);
        Assert.True(result.Code == 0, result.Error);
        var after = SourceRead.FromFile(path);
        try { Assert.Equal(wanted, SourceWalk.Payload(in after.Src, node).ToArray()); }
        finally { SourceFile.Close(ref after); }
    }

    [Fact]
    public void Missing_lists_what_a_source_refers_to_and_does_not_have()
    {
        // One reference names something by label only; another pointed at a node now deleted.
        string patch = WritePatch("""
            [
              {"op":"connect","node":"n0","relation":"about","target":null,"text":"sugar"},
              {"op":"delete","node":"n1"}
            ]
            """);
        Assert.Equal(0, Run("edit", path, patch).Code);

        var result = Run("missing", path);
        Assert.Equal(0, result.Code);
        Assert.Contains("sugar", result.Output);
        Assert.Contains("no longer exists", result.Output);
        Assert.Contains("2 missing", result.Output);
    }

    [Fact]
    public void A_merge_writes_the_merged_state_and_prints_its_decisions()
    {
        string ours = Path.Combine(directory, "ours.ss");
        string theirs = Path.Combine(directory, "theirs.ss");
        string merged = Path.Combine(directory, "merged.ss");
        File.Copy(path, ours);
        File.Copy(path, theirs);
        Assert.Equal(0, Run("edit", ours, WritePatch("""[{"op":"set-payload","node":"n1","text":"ours"}]""")).Code);
        Assert.Equal(0, Run("edit", theirs, WritePatch("""[{"op":"set-payload","node":"n1","text":"theirs"}]""")).Code);

        // A conflict keeps ours, and says so.
        var result = Run("merge", path, ours, theirs, merged);
        Assert.Equal(3, result.Code);
        Assert.Contains("DECIDED", result.Output);

        // The merged file holds the merged state and nothing about how it was reached.
        var file = SourceRead.FromBytes(File.ReadAllBytes(merged));
        Assert.Equal("ours", Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, new NodeId(1))));
        for (int i = 0; i < file.Table.KindCount; i++)
        {
            Assert.DoesNotContain("merge", KindTable.NameOf(in file.Table, new KindId(i)).Name ?? "");
        }
        Assert.Equal(SourceRead.FromBytes(File.ReadAllBytes(ours)).Src.NodeCount, file.Src.NodeCount);
    }

    [Fact]
    public void A_diff_matches_kinds_by_name_whatever_each_file_numbers_them()
    {
        // Each file names its kinds in first-use order. Deleting the first node of `first`
        // leaves `second` numbered 1 in the later file and 2 in the earlier one.
        var file = SourceFile.Create();
        var library = new SourceId(17, 23);
        var item = KindTable.Intern(ref file.Table, new QualifiedName(library, "item"));
        var first = KindTable.Intern(ref file.Table, new QualifiedName(library, "first"));
        var second = KindTable.Intern(ref file.Table, new QualifiedName(library, "second"));
        foreach (var kind in new[] { item, first, second })
        {
            KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, PayloadFormat.Text, default);
        }
        SourceBuild.MintIdentity(ref file.Src);
        var root = SourceBuild.AddRoot(ref file.Src, item);
        var gone = SourceBuild.AddChild(ref file.Src, root, first);
        SourceBuild.AddChild(ref file.Src, root, second);

        string before = Path.Combine(directory, "before.ss");
        string after = Path.Combine(directory, "after.ss");
        File.WriteAllBytes(before, SourceWrite.ToBytes(in file));
        SourceBuild.Delete(ref file.Src, gone);
        File.WriteAllBytes(after, SourceWrite.ToBytes(in file));

        var result = Run("diff", before, after);
        Assert.True(result.Code == 0, result.Error);
        Assert.Contains("Delete      n1", result.Output);
        Assert.DoesNotContain("Retype", result.Output);
    }

    [Fact]
    public void Upgrade_reads_an_older_major_and_writes_the_current_one()
    {
        string older = Path.Combine(directory, "older.ss");
        File.Copy(Path.Combine(Fixtures(), "ordinary-v2.ss"), older);
        var was = SourceRead.FromBytes(File.ReadAllBytes(older));

        var result = Run("upgrade", older);
        Assert.True(result.Code == 0, result.Error);

        byte[] bytes = File.ReadAllBytes(older);
        Assert.Equal(SourceWrite.FormatMajor, BitConverter.ToUInt16(bytes, SourceWrite.Magic.Length));
        var now = SourceRead.FromBytes(bytes);
        Assert.Equal(SourceHash.Of(in was), SourceHash.Of(in now));
        Assert.Equal(0, Run("verify", older).Code);
        Assert.Contains("already current", Run("upgrade", older).Output);
    }

    static string Fixtures()
    {
        string at = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(at, "tests", "Supersource.Tests", "legacy")))
        {
            at = Path.GetDirectoryName(at);
        }
        return Path.Combine(at, "tests", "Supersource.Tests", "legacy");
    }

    [Fact]
    public void Verify_names_the_verb_that_adds_a_checksum()
    {
        string older = Path.Combine(directory, "older.ss");
        File.Copy(Path.Combine(Fixtures(), "ordinary-v1.ss"), older);

        var result = Run("verify", older);
        Assert.Equal(0, result.Code);
        Assert.Contains("upgrade adds one", result.Output);
        Assert.DoesNotContain("migrate", result.Output);
    }

    [Fact]
    public void Cat_and_validate_speak_of_unbound_references()
    {
        string patch = WritePatch("""
            [{"op":"connect","node":"n0","relation":"about","target":null,"text":"sugar"}]
            """);
        Assert.Equal(0, Run("edit", path, patch).Code);

        var cat = Run("cat", path);
        Assert.Contains("--about--> unbound \"sugar\"", cat.Output);
        Assert.DoesNotContain("hole", cat.Output, StringComparison.OrdinalIgnoreCase);

        var validate = Run("validate", path);
        Assert.Equal(0, validate.Code);
        Assert.DoesNotContain("hole", validate.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Usage_says_new_creates_a_source_and_deps_names_no_other_binary()
    {
        var help = Run("--help");
        Assert.Contains("create a source", help.Output);
        Assert.DoesNotContain("document", help.Output);

        var deps = Run("deps", "");
        Assert.Equal(1, deps.Code);
        Assert.StartsWith("usage: deps ", deps.Error);
    }

    string WritePatch(string json)
    {
        string patch = Path.Combine(directory, "edits.json");
        File.WriteAllText(patch, json);
        return patch;
    }

    static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var oldOutput = Console.Out;
        var oldError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return (Program.Main(args), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOutput);
            Console.SetError(oldError);
        }
    }

    public void Dispose() => Directory.Delete(directory, true);
}
