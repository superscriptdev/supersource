using Supersource;
using Xunit;

namespace Supersource.Tests;

public class Spec_Revision
{
    [Fact]
    public void A_fresh_document_starts_somewhere()
    {
        Source src = Source.Create(64);
        Assert.Equal(0, src.Revision);
    }

    [Fact]
    public void Adding_a_node_moves_it()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);
        int before = src.Revision;

        SourceBuild.AddChild(ref src, src.Root, kind);

        Assert.True(src.Revision > before, "adding a node left the revision alone");
    }

    [Fact]
    public void Replacing_a_payload_with_one_the_same_length_moves_it()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);
        NodeId n = SourceBuild.AddChild(ref src, src.Root, kind);
        SourceBuild.SetPayload(ref src, n, System.Text.Encoding.UTF8.GetBytes("abc"));

        long nodes = src.NodeCount;
        int before = src.Revision;

        SourceBuild.SetPayload(ref src, n, System.Text.Encoding.UTF8.GetBytes("xyz"));

        Assert.Equal(nodes, src.NodeCount);
        Assert.True(src.Revision > before,
            "a same-length payload edit left the revision alone, which is exactly the edit a "
            + "node count misses");
    }

    [Fact]
    public void Reading_leaves_it_alone()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);
        NodeId n = SourceBuild.AddChild(ref src, src.Root, kind);

        int settled = src.Revision;
        int atRoot = RevisionOf(in src, src.Root);
        int atNode = RevisionOf(in src, n);

        SourceWalk.ChildCount(in src, src.Root);
        SourceWalk.Payload(in src, n);
        src.IsLive(n);

        Assert.Equal(settled, src.Revision);
        Assert.Equal(atRoot, RevisionOf(in src, src.Root));
        Assert.Equal(atNode, RevisionOf(in src, n));
    }

    [Fact]
    public void An_edit_in_one_subtree_leaves_a_sibling_subtree_alone()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);

        NodeId one = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId nine = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId paragraph = SourceBuild.AddChild(ref src, nine, kind);

        int chapterOne = RevisionOf(in src, one);
        int whole = src.Revision;

        SourceBuild.SetPayload(ref src, paragraph, System.Text.Encoding.UTF8.GetBytes("edited"));

        Assert.True(src.Revision > whole, "the source counter should still move");
        Assert.Equal(chapterOne, RevisionOf(in src, one));
    }

    [Fact]
    public void An_edit_deep_in_a_subtree_moves_every_ancestor()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);

        NodeId chapter = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId section = SourceBuild.AddChild(ref src, chapter, kind);
        NodeId paragraph = SourceBuild.AddChild(ref src, section, kind);

        int atRoot = RevisionOf(in src, src.Root);
        int atChapter = RevisionOf(in src, chapter);
        int atSection = RevisionOf(in src, section);
        int atParagraph = RevisionOf(in src, paragraph);

        SourceBuild.SetPayload(ref src, paragraph, System.Text.Encoding.UTF8.GetBytes("edited"));

        Assert.True(RevisionOf(in src, paragraph) > atParagraph);
        Assert.True(RevisionOf(in src, section) > atSection);
        Assert.True(RevisionOf(in src, chapter) > atChapter);
        Assert.True(RevisionOf(in src, src.Root) > atRoot);
    }

    [Fact]
    public void Adding_and_removing_a_child_moves_the_containing_subtree()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);

        NodeId chapter = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId other = SourceBuild.AddChild(ref src, src.Root, kind);

        int atChapter = RevisionOf(in src, chapter);
        int atOther = RevisionOf(in src, other);

        NodeId added = SourceBuild.AddChild(ref src, chapter, kind);
        Assert.True(RevisionOf(in src, chapter) > atChapter);
        Assert.Equal(atOther, RevisionOf(in src, other));

        atChapter = RevisionOf(in src, chapter);
        SourceBuild.Delete(ref src, added);
        Assert.True(RevisionOf(in src, chapter) > atChapter, "a delete left its container alone");
        Assert.Equal(atOther, RevisionOf(in src, other));
    }

    [Fact]
    public void A_move_moves_both_ends()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);

        NodeId from = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId to = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId moving = SourceBuild.AddChild(ref src, from, kind);

        int atFrom = RevisionOf(in src, from);
        int atTo = RevisionOf(in src, to);

        SourceBuild.Move(ref src, moving, to, 0);

        Assert.True(RevisionOf(in src, from) > atFrom, "the origin was left stale");
        Assert.True(RevisionOf(in src, to) > atTo);
    }

    [Fact]
    public void A_reference_moves_the_node_that_holds_it()
    {
        Source src = Source.Create(64);
        KindId kind = new KindId(1);
        SourceBuild.AddRoot(ref src, kind);

        NodeId holder = SourceBuild.AddChild(ref src, src.Root, kind);
        NodeId target = SourceBuild.AddChild(ref src, src.Root, kind);

        int atHolder = RevisionOf(in src, holder);
        int atTarget = RevisionOf(in src, target);

        SourceBuild.AddRef(ref src, holder, new RelationId(0), target);

        Assert.True(RevisionOf(in src, holder) > atHolder);
        Assert.Equal(atTarget, RevisionOf(in src, target));
    }

    static int RevisionOf(in Source src, NodeId id)
    {
        return src.NodeRevision[id.Index];
    }
}
