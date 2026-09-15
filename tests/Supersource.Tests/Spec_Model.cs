using System;
using System.Text;
using Xunit;

namespace Supersource.Tests;

public class Spec_Model
{

    [Fact]
    public void Sum_program_has_the_shape_3_4_describes()
    {
        SumProgram.Built b = SumProgram.Build();

        Assert.Equal(2, SourceWalk.ChildCount(in b.Src, b.Function));   // [xs, body]
        Assert.Equal(3, SourceWalk.ChildCount(in b.Src, b.FnBody));     // [total, loop, return]
        Assert.Equal(3, SourceWalk.ChildCount(in b.Src, b.Loop));       // [x, use xs, body]
        Assert.Equal(2, SourceWalk.ChildCount(in b.Src, b.Add));        // [use total, use x]

        Assert.Equal(b.Xs, SourceWalk.ChildAt(in b.Src, b.Function, 0));
        Assert.Equal(b.Return, SourceWalk.ChildAt(in b.Src, b.FnBody, 2));
    }

    [Fact]
    public void A_use_deep_in_the_loop_points_at_a_declaration_above_it()
    {
        SumProgram.Built b = SumProgram.Build();

        Assert.Equal(b.Total, SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl));

        // And the pointer really does cross the containment tree: neither is an ancestor
        // of the other.
        Assert.False(SourceWalk.IsAncestorOf(in b.Src, b.Total, b.UseTotal));
        Assert.False(SourceWalk.IsAncestorOf(in b.Src, b.UseTotal, b.Total));
    }

    [Fact]
    public void Rename_is_one_field_write_and_no_reference_moves()
    {
        SumProgram.Built b = SumProgram.Build();

        NodeId before1 = SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl);
        NodeId before2 = SourceWalk.Target(in b.Src, b.UseTotal2, SumProgram.Decl);

        SumProgram.Label(ref b.Src, b.Total, "runningTotal");

        Assert.Equal("runningTotal", SumProgram.LabelOf(in b.Src, b.Total));
        Assert.Equal(before1, SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl));
        Assert.Equal(before2, SourceWalk.Target(in b.Src, b.UseTotal2, SumProgram.Decl));
    }

    [Fact]
    public void Two_declarations_may_share_a_label_and_stay_distinct()
    {
        SumProgram.Built b = SumProgram.Build();

        NodeId shadow = SourceBuild.AddChild(ref b.Src, b.LoopBody, SumProgram.Local);
        SumProgram.Label(ref b.Src, shadow, "total");

        Assert.NotEqual(b.Total, shadow);
        Assert.Equal(SumProgram.LabelOf(in b.Src, b.Total), SumProgram.LabelOf(in b.Src, shadow));

        // The existing uses still name the outer one.
        Assert.Equal(b.Total, SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl));
    }

    [Fact]
    public void Find_all_references_is_a_scan_of_the_edges()
    {
        SumProgram.Built b = SumProgram.Build();

        NodeId[] found = null;
        int count = SourceWalk.Referrers(in b.Src, b.Total, ref found);

        Assert.Equal(3, count);   // assign target, use in add, use in return
        Assert.Contains(b.Assign, new ArraySegment<NodeId>(found, 0, count));
        Assert.Contains(b.UseTotal, new ArraySegment<NodeId>(found, 0, count));
        Assert.Contains(b.UseTotal2, new ArraySegment<NodeId>(found, 0, count));
    }

    [Fact]
    public void Ids_survive_arbitrary_edits()
    {
        SumProgram.Built b = SumProgram.Build();

        NodeId total = b.Total;
        NodeId useTotal = b.UseTotal;
        NodeId fnBody = b.FnBody;

        // Insert ahead of everything.
        NodeId early = SourceBuild.AddNode(ref b.Src, SumProgram.Local);
        SourceBuild.InsertChild(ref b.Src, b.FnBody, 0, early);

        // Delete something in the middle.
        SourceBuild.Delete(ref b.Src, b.Return);

        // Move a subtree.
        SourceBuild.Move(ref b.Src, b.Loop, b.Root, 0);

        // Reorder siblings.
        SourceBuild.ReorderChild(ref b.Src, b.Root, 0, 1);

        Assert.Equal(total, b.Total);
        Assert.Equal(useTotal, b.UseTotal);
        Assert.Equal(fnBody, b.FnBody);

        // And the reference still lands on the same node.
        Assert.Equal(b.Total, SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl));
        Assert.Equal("total", SumProgram.LabelOf(in b.Src, b.Total));
    }

    [Fact]
    public void A_deleted_id_is_never_handed_out_again()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));

        NodeId first = SourceBuild.AddChild(ref src, root, new KindId(1));
        SourceBuild.Delete(ref src, first);

        for (int i = 0; i < 20; i++)
        {
            NodeId fresh = SourceBuild.AddChild(ref src, root, new KindId(1));
            Assert.NotEqual(first, fresh);
        }

        Assert.False(src.IsLive(first));
        Assert.True(src.WasAllocated(first));   // deleted, not "never existed"
    }

    [Fact]
    public void Deleting_a_node_deletes_what_it_contains()
    {
        SumProgram.Built b = SumProgram.Build();

        SourceBuild.Delete(ref b.Src, b.Loop);

        Assert.False(b.Src.IsLive(b.Loop));
        Assert.False(b.Src.IsLive(b.X));          // contained
        Assert.False(b.Src.IsLive(b.LoopBody));   // contained
        Assert.False(b.Src.IsLive(b.Assign));     // contained, transitively
        Assert.False(b.Src.IsLive(b.UseX));       // contained, transitively
    }

    [Fact]
    public void A_tombstone_keeps_its_shape_and_no_parent()
    {
        SumProgram.Built b = SumProgram.Build();
        int contents = SourceWalk.ChildCount(in b.Src, b.Loop);

        SourceBuild.Delete(ref b.Src, b.Loop);

        Assert.Equal(NodeId.None, SourceWalk.Parent(in b.Src, b.Loop));
        Assert.Equal(contents, SourceWalk.ChildCount(in b.Src, b.Loop));
        Assert.Equal(b.Loop, SourceWalk.Parent(in b.Src, b.X));

        // And a second delete, or a detach, is a no-op rather than an error — and leaves
        // the shape alone, because a dead node's links are not anybody's to edit.
        SourceBuild.Delete(ref b.Src, b.Loop);
        SourceBuild.Detach(ref b.Src, b.X);

        Assert.Equal(contents, SourceWalk.ChildCount(in b.Src, b.Loop));
    }

    [Fact]
    public void Deleting_a_subject_leaves_what_referenced_it_standing()
    {
        SumProgram.Built b = SumProgram.Build();

        // Documentation references the function.
        RelationId sources = new RelationId(9);
        NodeId note = SourceBuild.AddChild(ref b.Src, b.Root, new KindId(20));
        SourceBuild.AddRef(ref b.Src, note, sources, b.Function);

        SourceBuild.Delete(ref b.Src, b.Function);

        Assert.True(b.Src.IsLive(note));
        Assert.Equal(1, SourceWalk.RefCount(in b.Src, note));

        NodeId[] dangling = null;
        int count = SourceWalk.Dangling(in b.Src, ref dangling);
        Assert.Contains(note, new ArraySegment<NodeId>(dangling, 0, count));
    }

    [Fact]
    public void A_node_cannot_have_two_parents()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId a = SourceBuild.AddChild(ref src, root, new KindId(1));
        NodeId b = SourceBuild.AddChild(ref src, root, new KindId(1));
        NodeId shared = SourceBuild.AddChild(ref src, a, new KindId(2));

        Assert.Throws<InvalidOperationException>(
            () => SourceBuild.AppendChild(ref src, b, shared));
    }

    [Fact]
    public void Containment_cannot_be_made_cyclic()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId outer = SourceBuild.AddChild(ref src, root, new KindId(1));
        NodeId inner = SourceBuild.AddChild(ref src, outer, new KindId(1));

        Assert.Throws<InvalidOperationException>(
            () => SourceBuild.Move(ref src, outer, inner, 0));
    }

    [Fact]
    public void References_may_form_cycles()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId a = SourceBuild.AddChild(ref src, root, new KindId(1));
        NodeId b = SourceBuild.AddChild(ref src, root, new KindId(1));

        RelationId calls = new RelationId(3);
        SourceBuild.AddRef(ref src, a, calls, b);
        SourceBuild.AddRef(ref src, b, calls, a);   // mutual recursion is ordinary

        Assert.Equal(b, SourceWalk.Target(in src, a, calls));
        Assert.Equal(a, SourceWalk.Target(in src, b, calls));
    }

    [Fact]
    public void A_node_may_reference_itself()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId fn = SourceBuild.AddChild(ref src, root, new KindId(1));

        RelationId calls = new RelationId(3);
        SourceBuild.AddRef(ref src, fn, calls, fn);

        Assert.Equal(fn, SourceWalk.Target(in src, fn, calls));
    }

    [Fact]
    public void An_unbound_reference_carries_a_label_and_is_a_hole()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId use = SourceBuild.AddChild(ref src, root, new KindId(7));

        RelationId decl = new RelationId(0);
        SourceBuild.AddUnboundRef(ref src, use, decl, Encoding.UTF8.GetBytes("foo"));

        Ref r = SourceWalk.RefAt(in src, use, 0);
        Assert.True(r.IsUnbound);
        Assert.False(r.IsBound);
        Assert.Equal("foo", Encoding.UTF8.GetString(SourceWalk.Label(in src, in r)));
    }

    [Fact]
    public void A_hole_binds_when_the_thing_it_named_appears()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId use = SourceBuild.AddChild(ref src, root, new KindId(7));

        RelationId decl = new RelationId(0);
        int slot = SourceBuild.AddUnboundRef(ref src, use, decl, Encoding.UTF8.GetBytes("foo"));

        NodeId foo = SourceBuild.AddChild(ref src, root, new KindId(4));
        SourceBuild.BindRef(ref src, use, slot, foo);

        Ref bound = SourceWalk.RefAt(in src, use, 0);
        Assert.True(bound.IsBound);
        Assert.Equal(foo, bound.Target);
        Assert.True(SourceWalk.Label(in src, in bound).IsEmpty);
    }

    [Fact]
    public void Unbinding_restores_a_labelled_hole()
    {
        SumProgram.Built b = SumProgram.Build();

        SourceBuild.UnbindRef(ref b.Src, b.UseTotal, 0, Encoding.UTF8.GetBytes("total"));

        Ref r = SourceWalk.RefAt(in b.Src, b.UseTotal, 0);
        Assert.True(r.IsUnbound);
        Assert.Equal("total", Encoding.UTF8.GetString(SourceWalk.Label(in b.Src, in r)));
    }

    [Fact]
    public void A_node_of_an_unrecognised_kind_stores_children_refs_and_payload()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));

        // A kind from a library this build has never linked.
        KindId alien = new KindId(9_999_999);
        RelationId alienRelation = new RelationId(8_888_888);

        NodeId weird = SourceBuild.AddChild(ref src, root, alien);
        SourceBuild.SetPayload(ref src, weird, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        NodeId inner = SourceBuild.AddChild(ref src, weird, alien);
        SourceBuild.AddRef(ref src, weird, alienRelation, root);

        Assert.Equal(alien, src.Nodes[weird.Index].Kind);
        Assert.Equal(1, SourceWalk.ChildCount(in src, weird));
        Assert.Equal(inner, SourceWalk.ChildAt(in src, weird, 0));
        Assert.Equal(1, SourceWalk.RefCount(in src, weird));
        Assert.Equal(root, SourceWalk.Target(in src, weird, alienRelation));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, SourceWalk.Payload(in src, weird).ToArray());
    }

    [Fact]
    public void Payloads_of_any_size_round_trip_through_the_arena()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));

        byte[] big = new byte[5000];
        for (int i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i * 31);
        }

        NodeId blob = SourceBuild.AddChild(ref src, root, new KindId(50));
        SourceBuild.SetPayload(ref src, blob, big);
        Assert.Equal(big, SourceWalk.Payload(in src, blob).ToArray());

        // Shrinking then growing again must not read stale bytes.
        SourceBuild.SetPayload(ref src, blob, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, SourceWalk.Payload(in src, blob).ToArray());

        SourceBuild.SetPayload(ref src, blob, big);
        Assert.Equal(big, SourceWalk.Payload(in src, blob).ToArray());
    }

    [Fact]
    public void Child_runs_survive_relocation()
    {
        Source src = Source.Create(4);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));

        NodeId[] made = new NodeId[64];
        for (int i = 0; i < made.Length; i++)
        {
            made[i] = SourceBuild.AddChild(ref src, root, new KindId(1));
            SourceBuild.SetPayload(ref src, made[i], Encoding.UTF8.GetBytes("n" + i));
        }

        Assert.Equal(made.Length, SourceWalk.ChildCount(in src, root));
        for (int i = 0; i < made.Length; i++)
        {
            Assert.Equal(made[i], SourceWalk.ChildAt(in src, root, i));
            Assert.Equal("n" + i, Encoding.UTF8.GetString(SourceWalk.Payload(in src, made[i])));
            Assert.Equal(root, SourceWalk.Parent(in src, made[i]));
        }
    }

    [Fact]
    public void Ref_runs_survive_relocation()
    {
        Source src = Source.Create(4);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));
        NodeId from = SourceBuild.AddChild(ref src, root, new KindId(1));

        NodeId[] targets = new NodeId[40];
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = SourceBuild.AddChild(ref src, root, new KindId(2));
            SourceBuild.AddRef(ref src, from, new RelationId(i), targets[i]);
        }

        Assert.Equal(targets.Length, SourceWalk.RefCount(in src, from));
        for (int i = 0; i < targets.Length; i++)
        {
            Assert.Equal(targets[i], SourceWalk.Target(in src, from, new RelationId(i)));
        }
    }

    [Fact]
    public void Insert_and_reorder_keep_the_child_order_they_claim()
    {
        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, new KindId(0));

        NodeId a = SourceBuild.AddChild(ref src, root, new KindId(1));
        NodeId c = SourceBuild.AddChild(ref src, root, new KindId(1));

        NodeId b = SourceBuild.AddNode(ref src, new KindId(1));
        SourceBuild.InsertChild(ref src, root, 1, b);

        Assert.Equal(a, SourceWalk.ChildAt(in src, root, 0));
        Assert.Equal(b, SourceWalk.ChildAt(in src, root, 1));
        Assert.Equal(c, SourceWalk.ChildAt(in src, root, 2));

        SourceBuild.ReorderChild(ref src, root, 0, 2);
        Assert.Equal(b, SourceWalk.ChildAt(in src, root, 0));
        Assert.Equal(c, SourceWalk.ChildAt(in src, root, 1));
        Assert.Equal(a, SourceWalk.ChildAt(in src, root, 2));

        Assert.Equal(0, SourceWalk.IndexInParent(in src, b));
        Assert.Equal(2, SourceWalk.IndexInParent(in src, a));
    }

    [Fact]
    public void Descendants_visits_containment_depth_first()
    {
        SumProgram.Built b = SumProgram.Build();

        NodeId[] all = null;
        int count = SourceWalk.Descendants(in b.Src, b.Function, ref all);

        Assert.Equal(15, count);
        Assert.Equal(b.Function, all[0]);
        Assert.Contains(b.UseX, new ArraySegment<NodeId>(all, 0, count));
        Assert.DoesNotContain(b.Root, new ArraySegment<NodeId>(all, 0, count));
    }

    [Fact]
    public void Re_minting_the_document_identity_leaves_every_internal_reference_alone()
    {
        SumProgram.Built b = SumProgram.Build();
        SourceId original = b.Src.Identity;

        SourceBuild.MintIdentity(ref b.Src);   // what copying a source does

        Assert.NotEqual(original, b.Src.Identity);
        Assert.Equal(b.Total, SourceWalk.Target(in b.Src, b.UseTotal, SumProgram.Decl));
        Assert.Equal(b.Xs, SourceWalk.Target(in b.Src, b.UseXs, SumProgram.Decl));
    }

    [Fact]
    public void Minted_identities_do_not_collide()
    {
        SourceId a = SourceId.Mint();
        SourceId b = SourceId.Mint();

        Assert.NotEqual(a, b);
        Assert.False(a.IsNone);
        Assert.False(b.IsNone);
    }
}
