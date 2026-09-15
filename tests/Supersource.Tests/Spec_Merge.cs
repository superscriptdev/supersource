using System;
using System.Text;
using Supersource.Compare;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Compare.Tests;

public class Spec_Merge
{
    static readonly SourceId Library = SourceId.Mint();

    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindId Section, Paragraph;
        public RelationId About, Calls;
        public NodeId Root, One, Two, Three;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);

        w.Section = KindTable.Intern(ref w.Table, new QualifiedName(Library, "section"));
        w.Paragraph = KindTable.Intern(ref w.Table, new QualifiedName(Library, "paragraph"));
        w.About = KindTable.InternRelation(ref w.Table, new QualifiedName(Library, "about"));
        w.Calls = KindTable.InternRelation(ref w.Table, new QualifiedName(Library, "calls"));

        KindTable.DefineSchema(ref w.Table, w.Section, StructuralFact.OpensScope,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About, w.Calls });
        KindTable.DefineSchema(ref w.Table, w.Paragraph, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About, w.Calls });

        w.Src = Source.Create(64);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Section);
        Text(ref w.Src, w.Root, "document");

        w.One = Para(ref w, "one");
        w.Two = Para(ref w, "two");
        w.Three = Para(ref w, "three");
        return w;
    }

    static NodeId Para(ref World w, string text)
    {
        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
        Text(ref w.Src, node, text);
        return node;
    }

    static void Text(ref Source src, NodeId node, string text)
    {
        SourceBuild.SetPayload(ref src, node, Encoding.UTF8.GetBytes(text));
    }

    static string TextOf(in Source src, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, node));
    }

    static Source Copy(in Source src)
    {
        return SourceCompact.Compact(in src);
    }

    static Source Merged(in World w, in Source common, in Source ours, in Source theirs,
        out MergeNote[] notes, out int count)
    {
        notes = null;
        return Merge.Between(in common, in ours, in theirs, in w.Table, ref notes, out count);
    }

    static int Conflicts(MergeNote[] notes, int count)
    {
        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (notes[i].How == Decision.Conflict || notes[i].How == Decision.Unrepresentable)
            {
                found++;
            }
        }
        return found;
    }

    [Fact]
    public void Edits_to_different_nodes_both_survive_with_nothing_to_reconcile()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "mine");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Three, "theirs");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal("mine", TextOf(in merged, w.One));
        Assert.Equal("two", TextOf(in merged, w.Two));
        Assert.Equal("theirs", TextOf(in merged, w.Three));
        Assert.Equal(0, Conflicts(notes, count));
    }

    [Fact]
    public void The_same_change_on_both_sides_is_agreement()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "same");
        Text(ref ours, w.One, "only mine");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Two, "same");
        Text(ref theirs, w.Three, "only theirs");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal("same", TextOf(in merged, w.Two));
        Assert.Equal("only mine", TextOf(in merged, w.One));
        Assert.Equal("only theirs", TextOf(in merged, w.Three));

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Contains(notes.AsSpan(0, count).ToArray(),
            n => n.Node == w.Two && n.How == Decision.Agreed);
    }

    [Fact]
    public void Catching_up_with_work_that_is_already_ours_duplicates_nothing()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        NodeId added = SourceBuild.AddChild(ref ours, w.Root, w.Paragraph);
        Text(ref ours, added, "new work");

        Source theirs = Copy(in ours);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, count);
        Assert.Equal(ours.NodeCount, merged.NodeCount);
        Assert.Equal("new work", TextOf(in merged, added));
    }

    [Fact]
    public void Two_edits_to_one_payload_conflict_and_ours_stands()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "mine");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Two, "theirs");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal("mine", TextOf(in merged, w.Two));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Facet.Payload, notes[0].Facet);
        Assert.Equal(w.Two, notes[0].Node);
    }

    [Fact]
    public void New_nodes_from_both_sides_both_survive_and_do_not_collide()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        NodeId mine = SourceBuild.AddChild(ref ours, w.Root, w.Paragraph);
        Text(ref ours, mine, "my new paragraph");

        Source theirs = Copy(in common);
        NodeId yours = SourceBuild.AddChild(ref theirs, w.Root, w.Paragraph);
        Text(ref theirs, yours, "their new paragraph");

        // The collision this is about: the same slot number on both sides.
        Assert.Equal(mine.Index, yours.Index);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Equal(common.NodeCount + 2, merged.NodeCount);
        Assert.Equal("my new paragraph", TextOf(in merged, mine));

        bool found = false;
        ReadOnlySpan<long> children = SourceWalk.Children(in merged, w.Root);
        for (int i = 0; i < children.Length; i++)
        {
            found = found || TextOf(in merged, new NodeId(children[i])) == "their new paragraph";
        }
        Assert.True(found, "their new paragraph did not survive the merge");
    }

    [Fact]
    public void A_subtree_they_added_arrives_with_its_own_edges_intact()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.AddChild(ref ours, w.Root, w.Paragraph);   // shifts the numbering

        Source theirs = Copy(in common);
        NodeId section = SourceBuild.AddChild(ref theirs, w.Root, w.Section);
        Text(ref theirs, section, "their section");
        NodeId child = SourceBuild.AddChild(ref theirs, section, w.Paragraph);
        Text(ref theirs, child, "their child");
        SourceBuild.AddRef(ref theirs, child, w.About, section);
        SourceBuild.AddRef(ref theirs, section, w.Calls, w.One);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);
        Assert.Equal(0, Conflicts(notes, count));

        NodeId here = Find(in merged, "their section");
        Assert.True(here.IsSome);

        // Its child came with it, and points back at it under its NEW id.
        Assert.Equal(1, SourceWalk.ChildCount(in merged, here));
        NodeId theirChild = SourceWalk.ChildAt(in merged, here, 0);
        Assert.Equal("their child", TextOf(in merged, theirChild));
        Assert.Equal(here, SourceWalk.Target(in merged, theirChild, w.About));

        // And its edge to a node that existed before the split still names that node.
        Assert.Equal(w.One, SourceWalk.Target(in merged, here, w.Calls));
    }

    static NodeId Find(in Source src, string text)
    {
        for (int i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (src.IsLive(node) && TextOf(in src, node) == text)
            {
                return node;
            }
        }
        return NodeId.None;
    }

    [Fact]
    public void A_move_only_they_made_is_taken()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "edited");

        Source theirs = Copy(in common);
        SourceBuild.Move(ref theirs, w.Three, w.One, 0);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Equal(w.One, SourceWalk.Parent(in merged, w.Three));
        Assert.Equal("edited", TextOf(in merged, w.One));
    }

    [Fact]
    public void Two_moves_of_one_node_to_different_places_conflict()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.Move(ref ours, w.Three, w.One, 0);

        Source theirs = Copy(in common);
        SourceBuild.Move(ref theirs, w.Three, w.Two, 0);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(w.One, SourceWalk.Parent(in merged, w.Three));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Facet.Containment, notes[0].Facet);
    }

    [Fact]
    public void A_move_that_would_make_a_cycle_is_reported_rather_than_made()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.Move(ref ours, w.Two, w.One, 0);

        Source theirs = Copy(in common);
        SourceBuild.Move(ref theirs, w.One, w.Two, 0);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(w.One, SourceWalk.Parent(in merged, w.Two));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Decision.Unrepresentable, notes[0].How);
    }

    [Fact]
    public void A_reorder_only_they_made_is_taken()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "edited");

        Source theirs = Copy(in common);
        SourceBuild.ReorderChild(ref theirs, w.Root, 2, 0);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Equal(w.Three, SourceWalk.ChildAt(in merged, w.Root, 0));
        Assert.Equal("edited", TextOf(in merged, w.One));
    }

    [Fact]
    public void A_delete_only_they_made_is_taken()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "edited");

        Source theirs = Copy(in common);
        SourceBuild.Delete(ref theirs, w.Three);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.False(merged.IsLive(w.Three));
        Assert.True(merged.IsLive(w.One));
    }

    [Fact]
    public void A_delete_against_an_edit_is_reported()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "still wanted");

        Source theirs = Copy(in common);
        SourceBuild.Delete(ref theirs, w.Two);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.True(merged.IsLive(w.Two));
        Assert.Equal("still wanted", TextOf(in merged, w.Two));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Facet.Existence, notes[0].Facet);
    }

    [Fact]
    public void An_edge_only_they_added_is_taken()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "edited");

        Source theirs = Copy(in common);
        SourceBuild.AddRef(ref theirs, w.Two, w.About, w.Three);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Equal(w.Three, SourceWalk.Target(in merged, w.Two, w.About));
    }

    [Fact]
    public void Two_different_edge_changes_on_one_node_conflict()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.AddRef(ref ours, w.Two, w.About, w.One);

        Source theirs = Copy(in common);
        SourceBuild.AddRef(ref theirs, w.Two, w.About, w.Three);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(w.One, SourceWalk.Target(in merged, w.Two, w.About));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Facet.Edges, notes[0].Facet);
    }

    [Fact]
    public void A_hole_they_bound_is_taken()
    {
        World w = Build();
        SourceBuild.AddUnboundRef(ref w.Src, w.Two, w.About, Encoding.UTF8.GetBytes("something"));
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "edited");

        Source theirs = Copy(in common);
        SourceBuild.BindRef(ref theirs, w.Two, 0, w.Three);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));
        Assert.Equal(w.Three, SourceWalk.RefAt(in merged, w.Two, 0).Target);
    }

    [Fact]
    public void Merging_a_state_with_itself_is_that_state()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source merged = Merged(in w, in common, in common, in common, out MergeNote[] notes, out int count);

        Assert.Equal(0, count);
        Assert.Equal(common.NodeCount, merged.NodeCount);
        Assert.Equal("one", TextOf(in merged, w.One));
    }

    [Fact]
    public void A_side_that_is_not_descended_from_the_common_state_is_refused()
    {
        World w = Build();
        World other = Build();

        Source common = Copy(in w.Src);
        MergeNote[] notes = null;

        Assert.Throws<InvalidOperationException>(() =>
            Merge.Between(in common, in common, in other.Src, in w.Table, ref notes, out int _));
    }

    [Fact]
    public void A_merged_source_is_an_ordinary_source()
    {
        World w = Build();
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        NodeId mine = SourceBuild.AddChild(ref ours, w.Root, w.Paragraph);
        Text(ref ours, mine, "mine");

        Source theirs = Copy(in common);
        NodeId yours = SourceBuild.AddChild(ref theirs, w.Root, w.Paragraph);
        Text(ref theirs, yours, "theirs");
        SourceBuild.AddRef(ref theirs, yours, w.About, w.One);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] _, out int _);

        for (int i = 0; i < merged.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!merged.IsLive(node))
            {
                continue;
            }

            NodeId parent = SourceWalk.Parent(in merged, node);
            if (parent.IsSome)
            {
                Assert.True(SourceWalk.IndexInParent(in merged, node) >= 0,
                    "node " + node + " claims a parent that does not hold it");
            }
        }
    }

    [Fact]
    public void However_two_people_diverge_the_merge_is_a_valid_source()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            World w = Build();
            Churn(ref w.Src, in w, new Random(seed), 25);
            Source common = Copy(in w.Src);

            Source ours = Copy(in common);
            Churn(ref ours, in w, new Random(seed * 31 + 1), 20);

            Source theirs = Copy(in common);
            Churn(ref theirs, in w, new Random(seed * 31 + 2), 20);

            MergeNote[] notes = null;
            Source merged = Merge.Between(in common, in ours, in theirs, in w.Table,
                ref notes, out int count);

            Format.SourceFile file = Format.SourceFile.Create(4);
            file.Src = merged;

            if (!Format.SourceValidate.Check(in file, out Format.Problem[] problems))
            {
                throw new Exception("seed " + seed + ": merged source is invalid — "
                    + problems[0].Kind + " at " + problems[0].Node + ": " + problems[0].Detail);
            }
        }
    }

    [Fact]
    public void Each_side_keeps_what_only_it_changed()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            World w = Build();
            Churn(ref w.Src, in w, new Random(seed), 25);
            Source common = Copy(in w.Src);

            Source ours = Copy(in common);
            Churn(ref ours, in w, new Random(seed * 71 + 3), 15);

            Source theirs = Copy(in common);
            Churn(ref theirs, in w, new Random(seed * 71 + 4), 15);

            MergeNote[] notes = null;
            Source merged = Merge.Between(in common, in ours, in theirs, in w.Table,
                ref notes, out int count);

            for (int i = 0; i < common.NodeCount; i++)
            {
                NodeId node = new NodeId(i);
                if (!common.IsLive(node) || !merged.IsLive(node)
                    || !ours.IsLive(node) || !theirs.IsLive(node))
                {
                    continue;
                }

                string was = TextOf(in common, node);
                string mine = TextOf(in ours, node);
                string yours = TextOf(in theirs, node);
                string got = TextOf(in merged, node);

                if (mine != was && yours == was)
                {
                    Assert.True(got == mine,
                        "seed " + seed + ": " + node + " lost our change");
                }
                else if (yours != was && mine == was)
                {
                    Assert.True(got == yours,
                        "seed " + seed + ": " + node + " lost their change");
                }
            }
        }
    }

    [Fact]
    public void Merging_our_own_work_against_itself_decides_nothing()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            World w = Build();
            Source common = Copy(in w.Src);

            Source ours = Copy(in common);
            Churn(ref ours, in w, new Random(seed), 20);

            Source theirs = Copy(in ours);

            MergeNote[] notes = null;
            Source merged = Merge.Between(in common, in ours, in theirs, in w.Table,
                ref notes, out int count);

            Assert.Equal(0, Conflicts(notes, count));
            Assert.Equal(ours.NodeCount, merged.NodeCount);

            for (int i = 0; i < ours.NodeCount; i++)
            {
                NodeId node = new NodeId(i);
                Assert.Equal(ours.IsLive(node), merged.IsLive(node));
                if (ours.IsLive(node))
                {
                    Assert.Equal(TextOf(in ours, node), TextOf(in merged, node));
                }
            }
        }
    }

    static void Churn(ref Source src, in World w, Random random, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            NodeId[] live = Live(in src);
            NodeId pick = live[random.Next(live.Length)];

            switch (random.Next(6))
            {
                case 0:
                {
                    NodeId made = SourceBuild.AddChild(ref src, pick,
                        random.Next(2) == 0 ? w.Section : w.Paragraph);
                    SourceBuild.SetPayload(ref src, made,
                        Encoding.UTF8.GetBytes("added " + step));
                    break;
                }

                case 1:
                    SourceBuild.SetPayload(ref src, pick,
                        Encoding.UTF8.GetBytes("edit " + random.Next(1000)));
                    break;

                case 2:
                {
                    NodeId into = live[random.Next(live.Length)];
                    if (pick == w.Root || into == pick || IsUnder(in src, pick, into))
                    {
                        break;
                    }

                    int at = SourceWalk.ChildCount(in src, into);
                    if (SourceWalk.Parent(in src, pick) == into)
                    {
                        at--;
                    }
                    SourceBuild.Move(ref src, pick, into, at);
                    break;
                }

                case 3:
                    SourceBuild.AddRef(ref src, pick,
                        random.Next(2) == 0 ? w.About : w.Calls,
                        live[random.Next(live.Length)]);
                    break;

                case 4:
                {
                    int count = SourceWalk.ChildCount(in src, pick);
                    if (count > 1)
                    {
                        SourceBuild.ReorderChild(ref src, pick,
                            random.Next(count), random.Next(count));
                    }
                    break;
                }

                default:
                    if (pick != w.Root && random.Next(3) == 0)
                    {
                        SourceBuild.Delete(ref src, pick);
                    }
                    break;
            }
        }
    }

    static NodeId[] Live(in Source src)
    {
        int count = 0;
        for (int i = 0; i < src.NodeCount; i++)
        {
            if (src.IsLive(new NodeId(i)))
            {
                count++;
            }
        }

        NodeId[] live = new NodeId[count];
        int at = 0;
        for (int i = 0; i < src.NodeCount; i++)
        {
            if (src.IsLive(new NodeId(i)))
            {
                live[at] = new NodeId(i);
                at++;
            }
        }
        return live;
    }

    static bool IsUnder(in Source src, NodeId maybeAncestor, NodeId node)
    {
        NodeId walk = node;
        while (walk.IsSome)
        {
            if (walk == maybeAncestor)
            {
                return true;
            }
            walk = SourceWalk.Parent(in src, walk);
        }
        return false;
    }

    [Fact]
    public void What_the_merge_decided_is_recorded_in_the_source_it_produced()
    {
        World w = Build();
        MergeIds ids = MergeKinds.Install(ref w.Table);

        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "mine");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Two, "theirs");
        Text(ref theirs, w.Three, "only theirs");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        NodeId record = MergeRecord.Write(ref merged, in ids, merged.Root, notes, count);
        Assert.True(record.IsSome);

        MergeNote[] readBack = null;
        int read = MergeRecord.Read(in merged, in ids, record, ref readBack);
        Assert.Equal(count, read);

        bool sawConflict = false;
        for (int i = 0; i < read; i++)
        {
            Assert.Equal(notes[i].Facet, readBack[i].Facet);
            Assert.Equal(notes[i].How, readBack[i].How);
            Assert.Equal(notes[i].Detail, readBack[i].Detail);
            Assert.Equal(notes[i].Node, readBack[i].Node);

            sawConflict = sawConflict
                || (readBack[i].Node == w.Two && readBack[i].How == Decision.Conflict);
        }

        Assert.True(sawConflict, "the payload conflict was not recorded");
    }

    [Fact]
    public void A_merge_that_decided_nothing_records_nothing()
    {
        World w = Build();
        MergeIds ids = MergeKinds.Install(ref w.Table);

        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.One, "mine");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Three, "theirs");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);
        Assert.Equal(0, Conflicts(notes, count));

        long before = merged.NodeCount;
        Assert.True(MergeRecord.Write(ref merged, in ids, merged.Root, notes, 0).IsNone);
        Assert.Equal(before, merged.NodeCount);
    }

    [Fact]
    public void The_record_survives_a_save_and_a_load()
    {
        World w = Build();
        MergeIds ids = MergeKinds.Install(ref w.Table);

        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.Move(ref ours, w.Three, w.One, 0);

        Source theirs = Copy(in common);
        SourceBuild.Move(ref theirs, w.Three, w.Two, 0);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Format.SourceFile file = Format.SourceFile.Create(8);
        file.Table = w.Table;
        file.Src = merged;
        MergeRecord.Write(ref file.Src, in ids, file.Src.Root, notes, count);
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, file.Src.Root);

        Format.SourceFile reloaded = Format.SourceRead.FromBytes(Format.SourceWrite.ToBytes(in file));

        // A reader interns the library by name; the handles are its own.
        MergeIds here;
        here.Merge = KindTable.Lookup(in reloaded.Table, MergeKinds.Merge);
        here.MergeNote = KindTable.Lookup(in reloaded.Table, MergeKinds.MergeNote);
        here.Decided = KindTable.LookupRelation(in reloaded.Table, MergeKinds.Decided);

        NodeId[] merges = null;
        Assert.Equal(1, MergeRecord.Merges(in reloaded.Src, in here, ref merges));

        MergeNote[] readBack = null;
        int read = MergeRecord.Read(in reloaded.Src, in here, merges[0], ref readBack);

        Assert.Equal(count, read);
        Assert.Contains(readBack.AsSpan(0, read).ToArray(),
            n => n.Node == w.Three && n.Facet == Facet.Containment
                && n.How == Decision.Conflict);

        // And its schema travelled with it, so the reader is not guessing.
        Assert.True(KindTable.HasSchema(in reloaded.Table, here.MergeNote));
    }

    [Fact]
    public void A_note_round_trips_through_its_own_bytes()
    {
        MergeNote note = default;
        note.Facet = Facet.Edges;
        note.How = Decision.Unrepresentable;
        note.Detail = "a détail with non-ascii bytes";

        Assert.True(MergeRecord.Decode(MergeRecord.Encode(in note), out MergeNote back));
        Assert.Equal(note.Facet, back.Facet);
        Assert.Equal(note.How, back.How);
        Assert.Equal(note.Detail, back.Detail);

        Assert.False(MergeRecord.Decode(new byte[] { 1, 2 }, out MergeNote _));
    }

    [Fact]
    public void Sides_loaded_against_different_tables_keep_their_kinds()
    {
        World w = Build();
        Format.SourceFile authored = Format.SourceFile.Create(16);
        authored.Table = w.Table;
        authored.Src = w.Src;
        KindSchemaCodec.Write(ref authored.Src, in authored.Table, in authored.Boot, w.Root);

        byte[] bytes = Format.SourceWrite.ToBytes(in authored);

        Format.SourceFile common = Format.SourceRead.FromBytes(bytes);
        Format.SourceFile ours = Format.SourceRead.FromBytes(bytes);
        Format.SourceFile theirs = Format.SourceRead.FromBytes(bytes);

        // Their handles land in a table that interned something else first, so every one of
        // them is a different number from ours — exactly what two independent sessions
        // produce, and what `KindRemap` exists to translate.
        KindTable shuffled = KindBootstrap.CreateTable(out KindBootstrap.Ids _);
        KindTable.Intern(ref shuffled, new QualifiedName(Library, "something-else-entirely"));
        KindTable.InternRelation(ref shuffled, new QualifiedName(Library, "some-other-relation"));
        KindRemap.Apply(ref theirs.Src, in theirs.Table, ref shuffled);
        theirs.Table = shuffled;

        KindId theirParagraph = KindTable.Lookup(in theirs.Table, new QualifiedName(Library, "paragraph"));
        KindId ourParagraph = KindTable.Lookup(in ours.Table, new QualifiedName(Library, "paragraph"));
        Assert.NotEqual(theirParagraph.Value, ourParagraph.Value);

        RelationId theirAbout = KindTable.LookupRelation(in theirs.Table, new QualifiedName(Library, "about"));

        NodeId added = SourceBuild.AddChild(ref theirs.Src, theirs.Src.Root, theirParagraph);
        SourceBuild.SetPayload(ref theirs.Src, added, Encoding.UTF8.GetBytes("theirs"));
        SourceBuild.AddRef(ref theirs.Src, added, theirAbout, theirs.Src.Root);

        SourceBuild.SetPayload(ref ours.Src, w.One, Encoding.UTF8.GetBytes("mine"));

        MergeNote[] notes = null;
        Source merged = Merge.Between(in common.Src, in ours.Src, in theirs.Src,
            ref ours.Table, in theirs.Table, ref notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));

        NodeId here = Find(in merged, "theirs");
        Assert.True(here.IsSome);

        // Read back through OUR table: the kind and the relation are the ones they meant.
        Assert.Equal(
            new QualifiedName(Library, "paragraph"),
            KindTable.NameOf(in ours.Table, merged.Nodes[here.Index].Kind));

        ReadOnlySpan<Ref> refs = SourceWalk.References(in merged, here);
        Assert.Equal(1, refs.Length);
        Assert.Equal(
            new QualifiedName(Library, "about"),
            KindTable.RelationNameOf(in ours.Table, refs[0].Relation));
        Assert.Equal(merged.Root, refs[0].Target);
    }

    [Fact]
    public void Two_edits_to_different_parts_of_one_payload_both_land()
    {
        World w = Build();
        Text(ref w.Src, w.Two, "the quick brown fox jumps over the lazy dog");
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "the SLOW brown fox jumps over the lazy dog");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Two, "the quick brown fox jumps over the lazy CAT");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal("the SLOW brown fox jumps over the lazy CAT", TextOf(in merged, w.Two));
        Assert.Equal(0, Conflicts(notes, count));
        Assert.Contains(notes.AsSpan(0, count).ToArray(),
            n => n.Node == w.Two && n.How == Decision.Combined);
    }

    [Fact]
    public void Two_edits_to_the_same_part_of_one_payload_conflict()
    {
        World w = Build();
        Text(ref w.Src, w.Two, "the quick brown fox");
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        Text(ref ours, w.Two, "the SLOW brown fox");

        Source theirs = Copy(in common);
        Text(ref theirs, w.Two, "the LIVELY brown fox");

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal("the SLOW brown fox", TextOf(in merged, w.Two));
        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(Decision.Conflict, notes[0].How);
    }

    [Fact]
    public void Two_edits_to_different_elements_of_an_array_both_land()
    {
        World w = Build();

        KindId raster = KindTable.Intern(ref w.Table, new QualifiedName(Library, "raster"));
        KindTable.DefineSchema(ref w.Table, raster, StructuralFact.None,
            PayloadFormat.Record, DisplayShape.Leaf, 0, default, PayloadStride.Of(2, 4));

        NodeId image = SourceBuild.AddChild(ref w.Src, w.Root, raster);
        byte[] pixels = new byte[2 + 4 * 8];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }
        SourceBuild.SetPayload(ref w.Src, image, pixels);

        Source common = Copy(in w.Src);

        byte[] oursPixels = (byte[])pixels.Clone();
        oursPixels[2 + 4 * 1 + 1] = 0xAA;
        Source ours = Copy(in common);
        SourceBuild.SetPayload(ref ours, image, oursPixels);

        byte[] theirsPixels = (byte[])pixels.Clone();
        theirsPixels[2 + 4 * 6 + 2] = 0xBB;
        Source theirs = Copy(in common);
        SourceBuild.SetPayload(ref theirs, image, theirsPixels);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));

        ReadOnlySpan<byte> result = SourceWalk.Payload(in merged, image);
        Assert.Equal(pixels.Length, result.Length);
        Assert.Equal(0xAA, result[2 + 4 * 1 + 1]);
        Assert.Equal(0xBB, result[2 + 4 * 6 + 2]);
    }

    [Fact]
    public void Two_edits_to_one_number_conflict_however_the_bytes_fall()
    {
        World w = Build();

        KindId count64 = KindTable.Intern(ref w.Table, new QualifiedName(Library, "count"));
        KindTable.DefineSchema(ref w.Table, count64, StructuralFact.None,
            PayloadFormat.Int64, DisplayShape.Leaf, 0, default);

        NodeId number = SourceBuild.AddChild(ref w.Src, w.Root, count64);
        SourceBuild.SetPayload(ref w.Src, number, new byte[8]);
        Source common = Copy(in w.Src);

        Source ours = Copy(in common);
        SourceBuild.SetPayload(ref ours, number, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });

        Source theirs = Copy(in common);
        SourceBuild.SetPayload(ref theirs, number, new byte[] { 0, 0, 0, 0, 0, 0, 0, 2 });

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
            SourceWalk.Payload(in merged, number).ToArray());
    }

    [Theory]
    [InlineData(PayloadFormat.Opaque)]
    [InlineData(PayloadFormat.Record)]
    public void Edits_to_a_payload_nobody_has_described_are_not_combined(PayloadFormat format)
    {
        World w = Build();

        KindId blob = KindTable.Intern(ref w.Table, new QualifiedName(Library, "blob-" + format));
        KindTable.DefineSchema(ref w.Table, blob, StructuralFact.None,
            format, DisplayShape.Leaf, 0, default);

        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, blob);
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }
        SourceBuild.SetPayload(ref w.Src, node, bytes);

        Source common = Copy(in w.Src);

        byte[] mine = (byte[])bytes.Clone();
        mine[2] = 0xAA;
        Source ours = Copy(in common);
        SourceBuild.SetPayload(ref ours, node, mine);

        byte[] yours = (byte[])bytes.Clone();
        yours[29] = 0xBB;
        Source theirs = Copy(in common);
        SourceBuild.SetPayload(ref theirs, node, yours);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(1, Conflicts(notes, count));
        Assert.Equal(mine, SourceWalk.Payload(in merged, node).ToArray());
    }

    [Fact]
    public void The_same_edits_to_a_declared_array_are_combined()
    {
        World w = Build();

        KindId array = KindTable.Intern(ref w.Table, new QualifiedName(Library, "declared-array"));
        KindTable.DefineSchema(ref w.Table, array, StructuralFact.None,
            PayloadFormat.Record, DisplayShape.Leaf, 0, default, PayloadStride.Of(0, 4));

        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, array);
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }
        SourceBuild.SetPayload(ref w.Src, node, bytes);

        Source common = Copy(in w.Src);

        byte[] mine = (byte[])bytes.Clone();
        mine[2] = 0xAA;
        Source ours = Copy(in common);
        SourceBuild.SetPayload(ref ours, node, mine);

        byte[] yours = (byte[])bytes.Clone();
        yours[29] = 0xBB;
        Source theirs = Copy(in common);
        SourceBuild.SetPayload(ref theirs, node, yours);

        Source merged = Merged(in w, in common, in ours, in theirs, out MergeNote[] notes, out int count);

        Assert.Equal(0, Conflicts(notes, count));

        ReadOnlySpan<byte> result = SourceWalk.Payload(in merged, node);
        Assert.Equal(0xAA, result[2]);
        Assert.Equal(0xBB, result[29]);
    }
}
