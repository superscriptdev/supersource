using System.IO;
using System.Text;
using Supersource.Compare;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Timeline.Tests;

/// <summary>
/// A node's kind is what it is now, not who it is: retyping keeps its id, its payload, its
/// children and everything pointing at it.
/// </summary>
public class Spec_Retype : System.IDisposable
{
    static readonly SourceId Library = SourceId.Mint();

    readonly string dir = Path.Combine(Path.GetTempPath(), "ss-retype-" + System.Guid.NewGuid().ToString("n"));

    public Spec_Retype()
    {
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }

    struct World
    {
        public SourceFile File;
        public KindId Chapter, Paragraph, Heading;
        public RelationId About;
        public NodeId Root, Text, Note, Comment;
    }

    static World Build()
    {
        World w = default;
        w.File = SourceFile.Create(16);
        w.Chapter = KindTable.Intern(ref w.File.Table, new QualifiedName(Library, "chapter"));
        w.Paragraph = KindTable.Intern(ref w.File.Table, new QualifiedName(Library, "paragraph"));
        w.Heading = KindTable.Intern(ref w.File.Table, new QualifiedName(Library, "heading"));
        w.About = KindTable.InternRelation(ref w.File.Table, new QualifiedName(Library, "about"));
        KindTable.DefineSchema(ref w.File.Table, w.Chapter, StructuralFact.None,
            PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.File.Table, w.Paragraph, StructuralFact.None,
            PayloadFormat.Text, new RelationId[] { w.About });
        KindTable.DefineSchema(ref w.File.Table, w.Heading, StructuralFact.None,
            PayloadFormat.Text, new RelationId[] { w.About });

        SourceBuild.MintIdentity(ref w.File.Src);
        w.Root = SourceBuild.AddRoot(ref w.File.Src, w.Chapter);
        w.Text = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Paragraph);
        SourceBuild.SetPayload(ref w.File.Src, w.Text, Encoding.UTF8.GetBytes("On formats"));
        w.Note = SourceBuild.AddChild(ref w.File.Src, w.Text, w.Paragraph);
        w.Comment = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Paragraph);
        SourceBuild.AddRef(ref w.File.Src, w.Comment, w.About, w.Text);

        // The heading kind is in use before the file is first written, so a journal can name it.
        NodeId title = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Heading);
        SourceBuild.SetPayload(ref w.File.Src, title, Encoding.UTF8.GetBytes("Title"));
        return w;
    }

    static void Retype(ref World w, NodeId node, KindId kind)
    {
        Operation op = default;
        op.Kind = OpKind.Retype;
        op.Node = node;
        op.NodeKind = kind;
        Log.Do(ref w.File.Log, ref w.File.Src, op);
    }

    [Fact]
    public void A_retype_keeps_the_id_and_everything_pointing_at_it()
    {
        World w = Build();

        Retype(ref w, w.Text, w.Heading);

        Assert.Equal(w.Heading, w.File.Src.Nodes[w.Text.Index].Kind);
        Assert.Equal("On formats", Encoding.UTF8.GetString(SourceWalk.Payload(in w.File.Src, w.Text)));
        Assert.Equal(w.Note, SourceWalk.ChildAt(in w.File.Src, w.Text, 0));
        Assert.Equal(w.Text, SourceWalk.Target(in w.File.Src, w.Comment, w.About));
    }

    [Fact]
    public void A_retype_undoes_and_redoes()
    {
        World w = Build();

        Retype(ref w, w.Text, w.Heading);
        Assert.True(Log.Undo(ref w.File.Log, ref w.File.Src));
        Assert.Equal(w.Paragraph, w.File.Src.Nodes[w.Text.Index].Kind);

        Assert.True(Log.Redo(ref w.File.Log, ref w.File.Src));
        Assert.Equal(w.Heading, w.File.Src.Nodes[w.Text.Index].Kind);
    }

    [Fact]
    public void A_retype_is_appended_as_a_journal_and_read_back()
    {
        World w = Build();
        string path = Path.Combine(dir, "retype.ss");
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        Retype(ref w, w.Text, w.Heading);
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        QualifiedName kind = KindTable.NameOf(in back.Table, back.Src.Nodes[w.Text.Index].Kind);
        Assert.Equal("heading", kind.Name);
        Assert.Equal(w.Text, SourceWalk.Target(in back.Src, w.Comment,
            KindTable.LookupRelation(in back.Table, new QualifiedName(Library, "about"))));
    }

    [Fact]
    public void A_diff_between_two_kinds_of_one_node_is_a_retype()
    {
        World w = Build();
        Source before = SourceCompact.Compact(in w.File.Src);
        Retype(ref w, w.Text, w.Heading);

        Operation[] ops = null;
        int count = StructuralDiff.Between(in before, in w.File.Src, in w.File.Table, ref ops);

        Assert.Equal(1, count);
        Assert.Equal(OpKind.Retype, ops[0].Kind);
        Assert.Equal(w.Text, ops[0].Node);
        Assert.Equal(w.Heading, ops[0].NodeKind);
    }
}
