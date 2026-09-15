using System;
using System.Text;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

public class Spec_Splice
{
    struct World
    {
        public Source Src;
        public KindTable Table;
        public NodeId Node;
    }

    static World Start(string text)
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        w.Src = Source.Create(8);
        SourceBuild.MintIdentity(ref w.Src);

        KindId paragraph = KindTable.Intern(
            ref w.Table, new QualifiedName(new SourceId(7, 7), "paragraph"));
        w.Node = SourceBuild.AddRoot(ref w.Src, paragraph);
        SourceBuild.SetPayload(ref w.Src, w.Node, Encoding.UTF8.GetBytes(text));
        return w;
    }

    static string Text(in World w)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in w.Src, w.Node));
    }

    static Operation Splice(NodeId node, int at, int removing, string inserting)
    {
        Operation op = default;
        op.Kind = OpKind.Splice;
        op.Node = node;
        op.Index = at;
        op.Count = removing;
        op.Value = Encoding.UTF8.GetBytes(inserting);
        return op;
    }

    [Fact]
    public void Typing_inserts_without_rewriting_the_paragraph()
    {
        World w = Start("the stranger test");

        Operation op = Splice(w.Node, 4, 0, "competent ");
        OpApply.Apply(ref w.Src, ref op);

        Assert.Equal("the competent stranger test", Text(in w));
    }

    [Fact]
    public void Deleting_takes_a_range_out()
    {
        World w = Start("the competent stranger test");

        Operation op = Splice(w.Node, 4, 10, "");
        OpApply.Apply(ref w.Src, ref op);

        Assert.Equal("the stranger test", Text(in w));
    }

    [Fact]
    public void An_applied_splice_inverts_itself_without_a_snapshot()
    {
        World w = Start("the stranger test");
        string before = Text(in w);

        Operation op = Splice(w.Node, 4, 8, "competent reader");
        OpApply.Apply(ref w.Src, ref op);
        Assert.Equal("the competent reader test", Text(in w));

        Operation back = OpApply.Invert(in op);
        OpApply.Apply(ref w.Src, ref back);

        Assert.Equal(before, Text(in w));
    }

    [Fact]
    public void Undo_and_redo_round_trip_any_number_of_times()
    {
        World w = Start("one two three");
        string before = Text(in w);

        Operation op = Splice(w.Node, 4, 3, "TWO");
        OpApply.Apply(ref w.Src, ref op);

        for (int i = 0; i < 3; i++)
        {
            Operation back = OpApply.Invert(in op);
            OpApply.Apply(ref w.Src, ref back);
            Assert.Equal(before, Text(in w));

            Operation forward = OpApply.Invert(in back);
            OpApply.Apply(ref w.Src, ref forward);
            Assert.Equal("one TWO three", Text(in w));
        }
    }

    [Fact]
    public void Two_edits_to_disjoint_ranges_both_survive()
    {
        World w = Start("alpha and omega");

        Operation tail = Splice(w.Node, 10, 5, "OMEGA");
        Operation head = Splice(w.Node, 0, 5, "ALPHA");

        // Applied later-first, because a splice does not shift what precedes it.
        OpApply.Apply(ref w.Src, ref tail);
        OpApply.Apply(ref w.Src, ref head);

        Assert.Equal("ALPHA and OMEGA", Text(in w));
    }

    [Fact]
    public void Nonsense_offsets_do_not_throw()
    {
        World w = Start("short");

        Operation past = Splice(w.Node, 500, 0, "!");
        OpApply.Apply(ref w.Src, ref past);
        Assert.Equal("short!", Text(in w));

        Operation over = Splice(w.Node, 3, 999, "");
        OpApply.Apply(ref w.Src, ref over);
        Assert.Equal("sho", Text(in w));

        Operation negative = Splice(w.Node, -4, -1, "X");
        OpApply.Apply(ref w.Src, ref negative);
        Assert.Equal("Xsho", Text(in w));
    }

    [Fact]
    public void A_splice_at_a_dead_node_is_ignored_rather_than_fatal()
    {
        World w = Start("gone");
        SourceBuild.Delete(ref w.Src, w.Node);

        Operation op = Splice(w.Node, 0, 0, "x");
        OpApply.Apply(ref w.Src, ref op);   // must not throw
    }

    [Fact]
    public void Offsets_are_bytes_so_multibyte_text_survives()
    {
        World w = Start("héllo wörld");

        int at = Encoding.UTF8.GetByteCount("héllo ");
        int len = Encoding.UTF8.GetByteCount("wörld");

        Operation op = Splice(w.Node, at, len, "welt");
        OpApply.Apply(ref w.Src, ref op);

        Assert.Equal("héllo welt", Text(in w));
    }
}
