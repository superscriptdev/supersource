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
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, PayloadFormat.Utf8,
            DisplayShape.Block, 0, new[] { relation });
        SourceBuild.MintIdentity(ref file.Src);
        var root = SourceBuild.AddRoot(ref file.Src, kind);
        var child = SourceBuild.AddChild(ref file.Src, root, kind);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("root payload"));
        SourceBuild.SetPayload(ref file.Src, child, Encoding.UTF8.GetBytes("child payload"));
        SourceBuild.AddRef(ref file.Src, root, relation, child);
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);
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
        Assert.Equal(2, Run("upgrade", path, extra).Code);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".migrating"));
    }

    [Fact]
    public void Node_parser_supports_64_bit_identifiers()
    {
        Assert.Equal(2147483648L, Program.ParseNode("n2147483648").Index);
    }

    [Fact]
    public void Edit_batch_preserves_identity_and_records_reopenable_history()
    {
        var before = SourceRead.FromBytes(original);
        long inserted = before.Src.NodeCount;
        string patch = WritePatch("""
            [
              {"op":"insert","parent":"n0","kind":"item","text":"new node","as":"new"},
              {"op":"set-payload","node":"n1","text":"café"},
              {"op":"splice","node":"n1","offset":3,"remove":2,"text":"e"},
              {"op":"rename","node":"n0","text":"renamed root"},
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
            Assert.Equal("cafe", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, new NodeId(1))));
            Assert.False(after.Src.IsLive(new NodeId(1)));
            Assert.Equal(new NodeId(0), after.Src.Nodes[inserted].Parent);
            Assert.Equal(0, after.Src.Nodes[0].RefCount);
            var refs = SourceWalk.References(in after.Src, new NodeId(inserted));
            Assert.Equal(new NodeId(0), refs[0].Target);
            Assert.Equal(new NodeId(1), refs[1].Target);
            Assert.Equal(9, after.History.At);
            Assert.True(SourceValidate.Check(in after, out _));
        }
        finally { SourceFile.Close(ref after); }
        // A second saved edit must remain addressable and keep the earlier history.
        patch = WritePatch("""[{"op":"set-payload","node":"n0","text":"second save"}]""");
        Assert.Equal(0, Run("edit", path, patch).Code);
        after = SourceRead.FromFile(path);
        try { Assert.Equal(10, after.History.At); Assert.Equal(before.Src.Identity, after.Src.Identity); }
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
        string patch = WritePatch("""[{"op":"rename","node":"n0","text":"changed"}]""");
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
        string patch = WritePatch("""[{"op":"rename","node":"n0","text":"changed"}]""");
        Assert.Equal(3, Run("edit", path, patch).Code);
        Assert.Equal("recoverable work", File.ReadAllText(path + ".writing"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Generated_node_edits_preserve_flags_and_history()
    {
        var file = SourceRead.FromBytes(original);
        file.Src.Nodes[1].Flags |= NodeFlags.Generated;
        var bytes = SourceWrite.ToBytes(in file);
        File.WriteAllBytes(path, bytes);
        string patch = WritePatch("""[{"op":"rename","node":"n1","text":"changed"}]""");
        Assert.Equal(0, Run("edit", path, patch).Code);
        var after = SourceRead.FromFile(path);
        try
        {
            var node = new NodeId(1);
            Assert.True(after.Src.Nodes[1].IsGenerated);
            Assert.Equal("changed", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, node)));
            Assert.Equal(file.History.At + 1, after.History.Count);
            Assert.True(Log.Undo(ref after.History, ref after.Src));
            Assert.Equal(SourceWalk.Payload(in file.Src, node).ToArray(), SourceWalk.Payload(in after.Src, node).ToArray());
        }
        finally { SourceFile.Close(ref after); }
    }

    [Fact]
    public void Ambiguous_kind_requires_qualified_name()
    {
        var file = SourceRead.FromBytes(original);
        var name = new QualifiedName(new SourceId(77, 88), "item");
        var second = KindTable.Intern(ref file.Table, name);
        KindTable.DefineSchema(ref file.Table, second, StructuralFact.None, PayloadFormat.Utf8,
            DisplayShape.Block, 0, Array.Empty<RelationId>());
        SourceBuild.AddChild(ref file.Src, file.Src.Root, second);
        KindSchemaCodec.Sync(ref file.Src, in file.Table, in file.Boot);
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
        KindTable.DefineSchema(ref file.Table, binary, StructuralFact.None, PayloadFormat.Opaque,
            DisplayShape.Leaf, 0, Array.Empty<RelationId>());
        var node = SourceBuild.AddChild(ref file.Src, file.Src.Root, binary);
        KindSchemaCodec.Sync(ref file.Src, in file.Table, in file.Boot);
        File.WriteAllBytes(path, SourceWrite.ToBytes(in file));
        byte[] wanted = new byte[] { 0, 255, 17, 42 };
        string patch = WritePatch("[{\"op\":\"set-payload\",\"node\":\"" + node + "\",\"base64\":\"" + Convert.ToBase64String(wanted) + "\"}]");
        var result = Run("edit", path, patch);
        Assert.True(result.Code == 0, result.Error);
        var after = SourceRead.FromFile(path);
        try { Assert.Equal(wanted, SourceWalk.Payload(in after.Src, node).ToArray()); }
        finally { SourceFile.Close(ref after); }
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
