using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

/// <summary>
/// A source's content hash is of its state, not its bytes: what a pin names is which graph a
/// source holds, so saving it another way — appended to, rewritten, upgraded from an older
/// format — leaves the hash alone, and any change to what it holds moves it.
/// </summary>
public class Spec_SourceHash : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName Chapter = new QualifiedName(Library, "chapter");
    static readonly QualifiedName Paragraph = new QualifiedName(Library, "paragraph");
    static readonly QualifiedName About = new QualifiedName(Library, "about");

    readonly string dir = Path.Combine(Path.GetTempPath(), "ss-hash-" + Guid.NewGuid().ToString("n"));

    public Spec_SourceHash()
    {
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }

    /// <summary>The same graph, with the table interned in either order.</summary>
    static SourceFile Build(SourceId identity, bool reversed)
    {
        SourceFile file = SourceFile.Create(16);
        KindId chapter, paragraph;
        if (reversed)
        {
            paragraph = KindTable.Intern(ref file.Table, Paragraph);
            chapter = KindTable.Intern(ref file.Table, Chapter);
        }
        else
        {
            chapter = KindTable.Intern(ref file.Table, Chapter);
            paragraph = KindTable.Intern(ref file.Table, Paragraph);
        }
        RelationId about = KindTable.InternRelation(ref file.Table, About);
        KindTable.DefineSchema(ref file.Table, chapter, StructuralFact.None, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref file.Table, paragraph, StructuralFact.None,
            PayloadFormat.Text, new RelationId[] { about });

        file.Src.Identity = identity;
        NodeId root = SourceBuild.AddRoot(ref file.Src, chapter);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("On formats"));
        NodeId first = SourceBuild.AddChild(ref file.Src, root, paragraph);
        SourceBuild.SetPayload(ref file.Src, first, Encoding.UTF8.GetBytes("A file holds a graph."));
        NodeId second = SourceBuild.AddChild(ref file.Src, root, paragraph);
        SourceBuild.AddRef(ref file.Src, second, about, first);
        return file;
    }

    static void Rename(ref SourceFile file, NodeId node, string text)
    {
        Operation op = default;
        op.Kind = OpKind.ModifyValue;
        op.Node = node;
        op.Value = Encoding.UTF8.GetBytes(text);
        Log.Do(ref file.Log, ref file.Src, op);
    }

    static bool Journaled(byte[] bytes)
    {
        SectionSpan[] spans = null;
        int count = SourceRead.Sections(bytes, ref spans);
        for (int i = 0; i < count; i++)
        {
            if (spans[i].Id == (uint)SectionId.Journal)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Interning_order_is_not_part_of_the_state()
    {
        SourceId identity = SourceId.Mint();
        Assert.Equal(SourceHash.Of(Build(identity, false)), SourceHash.Of(Build(identity, true)));
    }

    [Fact]
    public void An_appended_file_and_a_rewritten_one_hash_the_same_though_their_bytes_differ()
    {
        string path = Path.Combine(dir, "book.ss");
        SourceFile file = Build(SourceId.Mint(), false);
        SourceWrite.ToFile(in file, path);

        file = SourceRead.FromBytes(File.ReadAllBytes(path));
        Rename(ref file, new NodeId(1), "A file holds a graph, and nothing else.");
        Assert.True(SourceWrite.Replace(ref file, path, out _));

        byte[] appended = File.ReadAllBytes(path);
        Assert.True(Journaled(appended), "the edit should have been appended, not rewritten");
        SourceFile read = SourceRead.FromBytes(appended);

        byte[] rewritten = SourceWrite.ToBytes(in read);
        Assert.NotEqual(appended, rewritten);

        Assert.Equal(SourceHash.Of(in read), SourceHash.Of(SourceRead.FromBytes(rewritten)));
        Assert.Equal(SourceHash.Of(in read), SourceHash.OfFile(path));
    }

    [Theory]
    [InlineData("ordinary-v2.ss")]
    [InlineData("vec-v4.ss")]
    [InlineData("externs-v4.ss")]
    public void Upgrading_a_file_keeps_its_hash(string name)
    {
        byte[] old = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "legacy", name));
        SourceFile before = SourceRead.FromBytes(old);
        SourceFile after = SourceRead.FromBytes(SourceWrite.ToBytes(in before));
        Assert.Equal(SourceHash.Of(in before), SourceHash.Of(in after));
    }

    [Fact]
    public void A_payload_edit_moves_the_hash()
    {
        SourceFile file = Build(SourceId.Mint(), false);
        ContentHash was = SourceHash.Of(in file);
        SourceBuild.SetPayload(ref file.Src, new NodeId(1), Encoding.UTF8.GetBytes("A file holds a tree."));
        Assert.NotEqual(was, SourceHash.Of(in file));
    }

    [Fact]
    public void A_moved_child_moves_the_hash()
    {
        SourceFile file = Build(SourceId.Mint(), false);
        ContentHash was = SourceHash.Of(in file);
        SourceBuild.Move(ref file.Src, new NodeId(2), file.Src.Root, 0);
        Assert.NotEqual(was, SourceHash.Of(in file));
    }

    [Fact]
    public void A_removed_edge_moves_the_hash()
    {
        SourceFile file = Build(SourceId.Mint(), false);
        ContentHash was = SourceHash.Of(in file);
        SourceBuild.RemoveRef(ref file.Src, new NodeId(2), 0);
        Assert.NotEqual(was, SourceHash.Of(in file));
    }

    [Fact]
    public void A_schema_change_moves_the_hash()
    {
        // Two sources with the same bytes in their nodes, read differently, are not the same.
        SourceFile file = Build(SourceId.Mint(), false);
        ContentHash was = SourceHash.Of(in file);
        KindId chapter = KindTable.Lookup(in file.Table, Chapter);
        KindTable.DefineSchema(ref file.Table, chapter, StructuralFact.None, PayloadFormat.Opaque, default);
        Assert.NotEqual(was, SourceHash.Of(in file));
    }

    [Fact]
    public void A_different_identity_is_a_different_source()
    {
        Assert.NotEqual(SourceHash.Of(Build(SourceId.Mint(), false)), SourceHash.Of(Build(SourceId.Mint(), false)));
    }
}
