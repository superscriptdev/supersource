using System;
using System.Text;
using Supersource.Format;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Timeline.Tests;

public class Spec_Timeline
{
    static readonly KindId Container = new KindId(1);
    static readonly KindId Leaf = new KindId(2);
    static readonly RelationId Points = new RelationId(1);

    struct World
    {
        public Source Src;
        public Log Log;
        public NodeId Root, A, B, C;
    }

    static World Build()
    {
        World w = default;
        w.Src = Source.Create(32);
        w.Log = Log.Create();
        SourceBuild.MintIdentity(ref w.Src);

        w.Root = SourceBuild.AddRoot(ref w.Src, Container);
        w.A = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.SetPayload(ref w.Src, w.A, Encoding.UTF8.GetBytes("a"));
        w.B = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.SetPayload(ref w.Src, w.B, Encoding.UTF8.GetBytes("b"));
        w.C = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.SetPayload(ref w.Src, w.C, Encoding.UTF8.GetBytes("c"));

        return w;
    }

    static Operation Rename(NodeId node, string to)
    {
        Operation op = default;
        op.Kind = OpKind.Rename;
        op.Node = node;
        op.Value = Encoding.UTF8.GetBytes(to);
        return op;
    }

    static string Text(in Source src, NodeId id)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, id));
    }

    static string Order(in Source src, NodeId parent)
    {
        StringBuilder sb = new StringBuilder();
        int count = SourceWalk.ChildCount(in src, parent);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Text(in src, SourceWalk.ChildAt(in src, parent, i)));
        }
        return sb.ToString();
    }

    [Fact]
    public void Undo_many_copy_redo_to_the_present_leaves_the_document_unchanged()
    {
        World w = Build();

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));
        Log.Do(ref w.Log, ref w.Src, Rename(w.B, "bravo"));
        Log.Do(ref w.Log, ref w.Src, Rename(w.C, "charlie"));

        Operation move = default;
        move.Kind = OpKind.Move;
        move.Node = w.C;
        move.Parent = w.Root;
        move.Index = 0;
        Log.Do(ref w.Log, ref w.Src, move);

        string present = Order(in w.Src, w.Root);
        Assert.Equal("charlie,alpha,bravo", present);

        // Undo everything.
        while (Log.Undo(ref w.Log, ref w.Src))
        {
        }
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));

        // "Copy something" — a read, which must not disturb the log.
        string copied = Text(in w.Src, w.A);
        Assert.Equal("a", copied);

        // Redo back to the present.
        while (Log.Redo(ref w.Log, ref w.Src))
        {
        }

        Assert.Equal(present, Order(in w.Src, w.Root));
        Assert.Equal("alpha", Text(in w.Src, w.A));
    }

    [Fact]
    public void A_rename_is_recorded_as_a_rename_not_as_a_change_of_bytes()
    {
        World w = Build();

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));

        Assert.Equal(1, w.Log.Count);
        Assert.Equal(OpKind.Rename, w.Log.Entries[0].Op.Kind);
        Assert.Equal(w.A, w.Log.Entries[0].Op.Node);
        Assert.Equal("a", Encoding.UTF8.GetString(w.Log.Entries[0].Op.OldValue));
        Assert.Equal("alpha", Encoding.UTF8.GetString(w.Log.Entries[0].Op.Value));
    }

    [Fact]
    public void Insert_and_undo()
    {
        World w = Build();

        Operation op = default;
        op.Kind = OpKind.Insert;
        op.Parent = w.Root;
        op.Index = 1;
        op.NodeKind = Leaf;
        op.Value = Encoding.UTF8.GetBytes("new");
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal("a,new,b,c", Order(in w.Src, w.Root));

        Log.Undo(ref w.Log, ref w.Src);
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));
    }

    [Fact]
    public void Undoing_a_delete_restores_the_same_id_so_references_come_back()
    {
        World w = Build();

        NodeId pointer = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.AddRef(ref w.Src, pointer, Points, w.B);

        Operation op = default;
        op.Kind = OpKind.Delete;
        op.Node = w.B;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.False(w.Src.IsLive(w.B));
        Assert.False(w.Src.IsLive(SourceWalk.RefAt(in w.Src, pointer, 0).Target));

        Log.Undo(ref w.Log, ref w.Src);

        Assert.True(w.Src.IsLive(w.B));
        Assert.Equal("b", Text(in w.Src, w.B));
        Assert.Equal(w.B, SourceWalk.RefAt(in w.Src, pointer, 0).Target);
        Assert.True(w.Src.IsLive(SourceWalk.RefAt(in w.Src, pointer, 0).Target));
    }

    [Fact]
    public void Undoing_a_delete_restores_the_whole_subtree_not_just_its_root()
    {
        World w = Build();

        NodeId section = SourceBuild.AddChild(ref w.Src, w.Root, Container);
        SourceBuild.SetPayload(ref w.Src, section, Encoding.UTF8.GetBytes("section"));

        NodeId one = SourceBuild.AddChild(ref w.Src, section, Leaf);
        SourceBuild.SetPayload(ref w.Src, one, Encoding.UTF8.GetBytes("one"));
        NodeId two = SourceBuild.AddChild(ref w.Src, section, Container);
        SourceBuild.SetPayload(ref w.Src, two, Encoding.UTF8.GetBytes("two"));
        NodeId deep = SourceBuild.AddChild(ref w.Src, two, Leaf);
        SourceBuild.SetPayload(ref w.Src, deep, Encoding.UTF8.GetBytes("deep"));

        NodeId pointer = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.AddRef(ref w.Src, pointer, Points, deep);

        Operation op = default;
        op.Kind = OpKind.Delete;
        op.Node = section;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.False(w.Src.IsLive(section));
        Assert.False(w.Src.IsLive(one));
        Assert.False(w.Src.IsLive(deep));

        Assert.True(Log.Undo(ref w.Log, ref w.Src));

        Assert.True(w.Src.IsLive(section));
        Assert.Equal("one,two", Order(in w.Src, section));
        Assert.Equal("deep", Order(in w.Src, two));
        Assert.Equal(deep, SourceWalk.ChildAt(in w.Src, two, 0));
        Assert.True(w.Src.IsLive(SourceWalk.RefAt(in w.Src, pointer, 0).Target));

        SourceFile file = default;
        file.Src = w.Src;
        Problem[] problems;
        Assert.True(SourceValidate.Check(in file, out problems),
            problems.Length == 0 ? "" : problems[0].Kind + " " + problems[0].Detail);
    }

    [Fact]
    public void A_subtree_can_be_deleted_and_restored_repeatedly()
    {
        World w = Build();

        NodeId section = SourceBuild.AddChild(ref w.Src, w.Root, Container);
        NodeId word = SourceBuild.AddChild(ref w.Src, section, Leaf);
        SourceBuild.SetPayload(ref w.Src, word, Encoding.UTF8.GetBytes("word"));

        for (int round = 0; round < 3; round++)
        {
            Operation op = default;
            op.Kind = OpKind.Delete;
            op.Node = section;
            Log.Do(ref w.Log, ref w.Src, op);

            Assert.False(w.Src.IsLive(word));

            Assert.True(Log.Undo(ref w.Log, ref w.Src));

            Assert.True(w.Src.IsLive(word));
            Assert.Equal("word", Order(in w.Src, section));
            Assert.Equal(section, SourceWalk.Parent(in w.Src, word));
        }
    }

    [Fact]
    public void Move_and_undo_restore_both_parent_and_position()
    {
        World w = Build();

        NodeId nest = SourceBuild.AddChild(ref w.Src, w.Root, Container);
        SourceBuild.SetPayload(ref w.Src, nest, Encoding.UTF8.GetBytes("nest"));

        Operation op = default;
        op.Kind = OpKind.Move;
        op.Node = w.B;
        op.Parent = nest;
        op.Index = 0;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal("a,c,nest", Order(in w.Src, w.Root));
        Assert.Equal(nest, SourceWalk.Parent(in w.Src, w.B));

        Log.Undo(ref w.Log, ref w.Src);

        Assert.Equal("a,b,c,nest", Order(in w.Src, w.Root));
        Assert.Equal(1, SourceWalk.IndexInParent(in w.Src, w.B));
    }

    [Fact]
    public void Reorder_is_its_own_inverse()
    {
        World w = Build();

        Operation op = default;
        op.Kind = OpKind.Reorder;
        op.Node = w.A;
        op.Parent = w.Root;
        op.Index = 2;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal("b,c,a", Order(in w.Src, w.Root));

        Log.Undo(ref w.Log, ref w.Src);
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));
    }

    [Fact]
    public void Modify_value_and_undo()
    {
        World w = Build();

        Operation op = default;
        op.Kind = OpKind.ModifyValue;
        op.Node = w.A;
        op.Value = new byte[] { 0xFF, 0xEE };
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal(new byte[] { 0xFF, 0xEE }, SourceWalk.Payload(in w.Src, w.A).ToArray());

        Log.Undo(ref w.Log, ref w.Src);
        Assert.Equal("a", Text(in w.Src, w.A));
    }

    [Fact]
    public void Wrap_keeps_the_wrapped_nodes_identity()
    {
        World w = Build();

        NodeId pointer = SourceBuild.AddChild(ref w.Src, w.Root, Leaf);
        SourceBuild.AddRef(ref w.Src, pointer, Points, w.B);

        Operation op = default;
        op.Kind = OpKind.Wrap;
        op.Parent = w.Root;
        op.Index = 1;          // wrap B, in place
        op.NodeKind = Container;
        Log.Do(ref w.Log, ref w.Src, op);

        NodeId wrapper = SourceWalk.Parent(in w.Src, w.B);
        Assert.NotEqual(w.Root, wrapper);
        Assert.Equal(w.Root, SourceWalk.Parent(in w.Src, wrapper));
        Assert.Equal(1, SourceWalk.IndexInParent(in w.Src, wrapper));

        // The reference never noticed.
        Assert.Equal(w.B, SourceWalk.RefAt(in w.Src, pointer, 0).Target);
        Assert.Equal("b", Text(in w.Src, w.B));
    }

    [Fact]
    public void Wrap_then_undo_restores_the_original_shape()
    {
        World w = Build();

        Operation op = default;
        op.Kind = OpKind.Wrap;
        op.Parent = w.Root;
        op.Index = 1;
        op.NodeKind = Container;
        Log.Do(ref w.Log, ref w.Src, op);

        Log.Undo(ref w.Log, ref w.Src);

        Assert.Equal("a,b,c", Order(in w.Src, w.Root));
        Assert.Equal(w.Root, SourceWalk.Parent(in w.Src, w.B));
        Assert.Equal(1, SourceWalk.IndexInParent(in w.Src, w.B));
    }

    [Fact]
    public void Wrap_takes_in_a_range_of_siblings()
    {
        World w = Build();

        Operation op = default;
        op.Kind = OpKind.Wrap;
        op.Parent = w.Root;
        op.Index = 0;
        op.Count = 3;
        op.NodeKind = Container;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal(1, SourceWalk.ChildCount(in w.Src, w.Root));
        NodeId wrapper = SourceWalk.ChildAt(in w.Src, w.Root, 0);
        Assert.Equal("a,b,c", Order(in w.Src, wrapper));

        Log.Undo(ref w.Log, ref w.Src);
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));
    }

    [Fact]
    public void Unwrap_lifts_children_into_the_wrappers_place()
    {
        World w = Build();

        NodeId group = SourceBuild.AddChild(ref w.Src, w.Root, Container);
        SourceBuild.Move(ref w.Src, w.A, group, 0);
        SourceBuild.Move(ref w.Src, w.B, group, 1);
        Assert.Equal("c,", Order(in w.Src, w.Root));

        Operation op = default;
        op.Kind = OpKind.Unwrap;
        op.Node = group;
        Log.Do(ref w.Log, ref w.Src, op);

        Assert.Equal("c,a,b", Order(in w.Src, w.Root));
        Assert.False(w.Src.IsLive(group));

        Log.Undo(ref w.Log, ref w.Src);
        Assert.True(w.Src.IsLive(group));
        Assert.Equal(w.Root, SourceWalk.Parent(in w.Src, group));
    }

    [Fact]
    public void Every_kind_of_edit_lands_in_the_same_log()
    {
        World w = Build();

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));

        Operation move = default;
        move.Kind = OpKind.Move;
        move.Node = w.C;
        move.Parent = w.Root;
        move.Index = 0;
        Log.Do(ref w.Log, ref w.Src, move);

        Operation del = default;
        del.Kind = OpKind.Delete;
        del.Node = w.B;
        Log.Do(ref w.Log, ref w.Src, del);

        Assert.Equal(3, w.Log.Count);
        Assert.Equal(OpKind.Rename, w.Log.Entries[0].Op.Kind);
        Assert.Equal(OpKind.Move, w.Log.Entries[1].Op.Kind);
        Assert.Equal(OpKind.Delete, w.Log.Entries[2].Op.Kind);

        // Walking back through them restores the start, in one stack.
        while (Log.Undo(ref w.Log, ref w.Src))
        {
        }
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));
    }

    [Fact]
    public void A_new_edit_after_undo_abandons_the_redo_branch()
    {
        World w = Build();

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));
        Log.Do(ref w.Log, ref w.Src, Rename(w.B, "bravo"));

        Log.Undo(ref w.Log, ref w.Src);
        Assert.True(Log.CanRedo(in w.Log));

        Log.Do(ref w.Log, ref w.Src, Rename(w.C, "charlie"));

        Assert.False(Log.CanRedo(in w.Log));
        Assert.Equal(2, w.Log.Count);
        Assert.Equal("b", Text(in w.Src, w.B));
        Assert.Equal("charlie", Text(in w.Src, w.C));
    }

    [Fact]
    public void Undo_and_redo_report_honestly_at_the_boundaries()
    {
        World w = Build();

        Assert.False(Log.CanUndo(in w.Log));
        Assert.False(Log.Undo(ref w.Log, ref w.Src));

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));

        Assert.True(Log.CanUndo(in w.Log));
        Assert.False(Log.CanRedo(in w.Log));
        Assert.False(Log.Redo(ref w.Log, ref w.Src));
    }

    [Fact]
    public void Layout_edits_participate_in_sequential_undo_and_redo()
    {
        World w = Build();

        KindId position = new KindId(99);

        Log.Do(ref w.Log, ref w.Src, Rename(w.A, "alpha"));

        Operation layout = default;
        layout.Kind = OpKind.Insert;
        layout.Parent = w.Root;
        layout.Index = 0;
        layout.NodeKind = position;
        Log.Do(ref w.Log, ref w.Src, layout);

        // It IS in the log — excluding it would mean layout does not sync and cannot be
        // recovered.
        Assert.Equal(2, w.Log.Count);

        Assert.True(Log.Undo(ref w.Log, ref w.Src));
        Assert.Equal(1, w.Log.At);
        Assert.True(Log.Redo(ref w.Log, ref w.Src));
        Assert.Equal(2, w.Log.At);
    }

    [Fact]
    public void Inverting_an_operation_reads_only_the_operation()
    {
        Operation op = default;
        op.Kind = OpKind.Move;
        op.Node = new NodeId(5);
        op.Parent = new NodeId(1);
        op.Index = 2;
        op.OldParent = new NodeId(0);
        op.OldIndex = 7;

        Operation inverse = OpApply.Invert(in op);

        Assert.Equal(OpKind.Move, inverse.Kind);
        Assert.Equal(new NodeId(0), inverse.Parent);
        Assert.Equal(7, inverse.Index);
        Assert.Equal(new NodeId(1), inverse.OldParent);
        Assert.Equal(2, inverse.OldIndex);
    }

    [Fact]
    public void A_long_mixed_session_undoes_and_redoes_exactly()
    {
        World w = Build();

        for (int i = 0; i < 20; i++)
        {
            Log.Do(ref w.Log, ref w.Src, Rename(w.A, "a" + i));

            Operation ins = default;
            ins.Kind = OpKind.Insert;
            ins.Parent = w.Root;
            ins.Index = 0;
            ins.NodeKind = Leaf;
            ins.Value = Encoding.UTF8.GetBytes("x" + i);
            Log.Do(ref w.Log, ref w.Src, ins);

            Operation reorder = default;
            reorder.Kind = OpKind.Reorder;
            reorder.Node = w.C;
            reorder.Parent = w.Root;
            reorder.Index = 0;
            Log.Do(ref w.Log, ref w.Src, reorder);
        }

        string present = Order(in w.Src, w.Root);
        int entries = w.Log.Count;

        while (Log.Undo(ref w.Log, ref w.Src))
        {
        }
        Assert.Equal("a,b,c", Order(in w.Src, w.Root));

        while (Log.Redo(ref w.Log, ref w.Src))
        {
        }
        Assert.Equal(present, Order(in w.Src, w.Root));
        Assert.Equal(entries, w.Log.Count);
    }
}
