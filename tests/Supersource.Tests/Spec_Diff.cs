using System;
using System.Text;
using Supersource.Compare;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Compare.Tests;

public class Spec_Diff
{
    static readonly SourceId Library = SourceId.Mint();

    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindId Section, Paragraph;
        public RelationId About, Calls;
        public NodeId Root;
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
        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("document"));
        return w;
    }

    static Source Copy(in Source src)
    {
        return SourceCompact.Compact(in src);
    }

    static byte[] Text(string s)
    {
        return Encoding.UTF8.GetBytes(s);
    }

    static Source Replay(in World w, in Source before, in Source after, out int count)
    {
        Operation[] ops = null;
        count = StructuralDiff.Between(in before, in after, in w.Table, ref ops);

        Source replayed = Copy(in before);
        for (int i = 0; i < count; i++)
        {
            Operation op = ops[i];
            OpApply.Apply(ref replayed, ref op);
        }
        return replayed;
    }

    static void AssertSameState(in Source expected, in Source actual)
    {
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.NodeCount, actual.NodeCount);

        for (int i = 0; i < expected.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            Assert.Equal(expected.IsLive(node), actual.IsLive(node));
            if (!expected.IsLive(node))
            {
                continue;
            }

            Assert.Equal(expected.Nodes[i].Kind, actual.Nodes[i].Kind);
            Assert.Equal(expected.Nodes[i].Parent, actual.Nodes[i].Parent);

            Assert.Equal(
                SourceWalk.Payload(in expected, node).ToArray(),
                SourceWalk.Payload(in actual, node).ToArray());
            Assert.Equal(
                SourceWalk.Children(in expected, node).ToArray(),
                SourceWalk.Children(in actual, node).ToArray());

            ReadOnlySpan<Ref> want = SourceWalk.References(in expected, node);
            ReadOnlySpan<Ref> have = SourceWalk.References(in actual, node);
            Assert.Equal(want.Length, have.Length);

            for (int r = 0; r < want.Length; r++)
            {
                Assert.Equal(want[r].Relation, have[r].Relation);
                Assert.Equal(want[r].Target, have[r].Target);
                Assert.Equal(
                    SourceWalk.Label(in expected, in want[r]).ToArray(),
                    SourceWalk.Label(in actual, in have[r]).ToArray());
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(90210)]
    public void Applying_a_diff_reproduces_the_state_it_was_taken_against(int seed)
    {
        World w = Build();
        Churn(ref w, new Random(seed), 40);

        Source before = Copy(in w.Src);
        Churn(ref w, new Random(seed * 31 + 5), 40);
        Source after = Copy(in w.Src);

        Source replayed = Replay(in w, in before, in after, out int count);
        AssertSameState(in after, in replayed);
    }

    static void Churn(ref World w, Random random, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            NodeId[] live = Live(in w.Src);
            NodeId pick = live[random.Next(live.Length)];

            switch (random.Next(7))
            {
                case 0:
                {
                    NodeId made = SourceBuild.AddChild(ref w.Src, pick,
                        random.Next(2) == 0 ? w.Section : w.Paragraph);
                    SourceBuild.SetPayload(ref w.Src, made, Text("n" + step));
                    break;
                }

                case 1:
                    SourceBuild.SetPayload(ref w.Src, pick, Text("edited " + step));
                    break;

                case 2:
                {
                    // A move, refused where it would make containment cyclic.
                    NodeId into = live[random.Next(live.Length)];
                    if (pick == w.Root || into == pick || Contains(in w.Src, pick, into))
                    {
                        break;
                    }

                    // Move detaches before it inserts, so a node already under `into` makes
                    // the run one shorter by the time the index is used.
                    int at = SourceWalk.ChildCount(in w.Src, into);
                    if (SourceWalk.Parent(in w.Src, pick) == into)
                    {
                        at--;
                    }

                    SourceBuild.Move(ref w.Src, pick, into, at);
                    break;
                }

                case 3:
                {
                    NodeId target = live[random.Next(live.Length)];
                    SourceBuild.AddRef(ref w.Src, pick,
                        random.Next(2) == 0 ? w.About : w.Calls, target);
                    break;
                }

                case 4:
                    SourceBuild.AddUnboundRef(ref w.Src, pick, w.About, Text("hole " + step));
                    break;

                case 5:
                {
                    int count = SourceWalk.RefCount(in w.Src, pick);
                    if (count > 0)
                    {
                        SourceBuild.RemoveRef(ref w.Src, pick, random.Next(count));
                    }
                    break;
                }

                case 6:
                {
                    int count = SourceWalk.ChildCount(in w.Src, pick);
                    if (count > 1)
                    {
                        SourceBuild.ReorderChild(ref w.Src, pick,
                            random.Next(count), random.Next(count));
                    }
                    else if (pick != w.Root && random.Next(3) == 0)
                    {
                        SourceBuild.Delete(ref w.Src, pick);
                    }
                    break;
                }
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

    static bool Contains(in Source src, NodeId maybeAncestor, NodeId node)
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
    public void Two_identical_states_differ_by_nothing()
    {
        World w = Build();
        SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);

        Source before = Copy(in w.Src);
        Source after = Copy(in w.Src);

        Operation[] ops = null;
        Assert.Equal(0, StructuralDiff.Between(in before, in after, in w.Table, ref ops));
    }

    [Fact]
    public void A_new_node_is_an_insert()
    {
        World w = Build();
        Source before = Copy(in w.Src);

        NodeId added = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
        SourceBuild.SetPayload(ref w.Src, added, Text("new"));
        Source after = Copy(in w.Src);

        Source replayed = Replay(in w, in before, in after, out int count);
        Assert.Equal(1, count);
        AssertSameState(in after, in replayed);
    }

    [Fact]
    public void A_removed_node_is_a_delete()
    {
        World w = Build();
        NodeId doomed = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
        Source before = Copy(in w.Src);

        SourceBuild.Delete(ref w.Src, doomed);
        Source after = Copy(in w.Src);

        Source replayed = Replay(in w, in before, in after, out int count);
        Assert.Equal(1, count);
        AssertSameState(in after, in replayed);
    }

    [Fact]
    public void A_reparented_node_is_a_move_and_keeps_its_id()
    {
        World w = Build();
        NodeId one = SourceBuild.AddChild(ref w.Src, w.Root, w.Section);
        NodeId two = SourceBuild.AddChild(ref w.Src, w.Root, w.Section);
        NodeId moving = SourceBuild.AddChild(ref w.Src, one, w.Paragraph);
        SourceBuild.SetPayload(ref w.Src, moving, Text("travels"));

        Source before = Copy(in w.Src);
        SourceBuild.Move(ref w.Src, moving, two, 0);
        Source after = Copy(in w.Src);

        Operation[] ops = null;
        int count = StructuralDiff.Between(in before, in after, in w.Table, ref ops);

        Assert.Equal(1, count);
        Assert.Equal(OpKind.Move, ops[0].Kind);
        Assert.Equal(moving, ops[0].Node);
        Assert.Equal(two, ops[0].Parent);
    }

    [Fact]
    public void An_edited_payload_is_one_operation_naming_one_node()
    {
        World w = Build();
        for (int i = 0; i < 20; i++)
        {
            NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
            SourceBuild.SetPayload(ref w.Src, node, Text("paragraph " + i));
        }

        Source before = Copy(in w.Src);
        NodeId ninth = SourceWalk.ChildAt(in w.Src, w.Root, 9);
        SourceBuild.SetPayload(ref w.Src, ninth, Text("rewritten"));
        Source after = Copy(in w.Src);

        Operation[] ops = null;
        int count = StructuralDiff.Between(in before, in after, in w.Table, ref ops);

        Assert.Equal(1, count);
        Assert.Equal(OpKind.ModifyValue, ops[0].Kind);
        Assert.Equal(ninth, ops[0].Node);
        Assert.Equal("rewritten", Encoding.UTF8.GetString(ops[0].Value));
    }

    [Fact]
    public void A_reordered_child_run_is_reorders()
    {
        World w = Build();
        for (int i = 0; i < 5; i++)
        {
            NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
            SourceBuild.SetPayload(ref w.Src, node, Text("p" + i));
        }

        Source before = Copy(in w.Src);
        SourceBuild.ReorderChild(ref w.Src, w.Root, 4, 0);
        SourceBuild.ReorderChild(ref w.Src, w.Root, 3, 1);
        Source after = Copy(in w.Src);

        Source replayed = Replay(in w, in before, in after, out int count);
        Assert.True(count > 0);
        AssertSameState(in after, in replayed);
    }

    [Fact]
    public void A_new_edge_is_a_connect_and_a_removed_one_is_a_disconnect()
    {
        World w = Build();
        NodeId one = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
        NodeId two = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);

        Source before = Copy(in w.Src);
        SourceBuild.AddRef(ref w.Src, one, w.About, two);
        Source after = Copy(in w.Src);

        Operation[] ops = null;
        Assert.Equal(1, StructuralDiff.Between(in before, in after, in w.Table, ref ops));
        Assert.Equal(OpKind.Connect, ops[0].Kind);
        Assert.Equal(w.About, ops[0].Relation);
        Assert.Equal(two, ops[0].Target);

        Assert.Equal(1, StructuralDiff.Between(in after, in before, in w.Table, ref ops));
        Assert.Equal(OpKind.Disconnect, ops[0].Kind);
    }

    [Fact]
    public void A_hole_is_diffed_like_anything_else()
    {
        World w = Build();
        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);

        Source before = Copy(in w.Src);
        SourceBuild.AddUnboundRef(ref w.Src, node, w.About, Text("something called foo"));
        Source after = Copy(in w.Src);

        Source replayed = Replay(in w, in before, in after, out int count);
        Assert.Equal(1, count);
        AssertSameState(in after, in replayed);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in replayed, node);
        Assert.True(refs[0].IsUnbound);
        Assert.Equal(
            "something called foo",
            Encoding.UTF8.GetString(SourceWalk.Label(in replayed, in refs[0])));
    }

    [Fact]
    public void Edits_to_different_nodes_produce_operations_that_do_not_overlap()
    {
        World w = Build();
        NodeId mine = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);
        NodeId theirs = SourceBuild.AddChild(ref w.Src, w.Root, w.Paragraph);

        Source common = Copy(in w.Src);

        Source a = Copy(in common);
        SourceBuild.SetPayload(ref a, mine, Text("my edit"));

        Source b = Copy(in common);
        SourceBuild.SetPayload(ref b, theirs, Text("their edit"));

        Operation[] ours = null;
        int oursCount = StructuralDiff.Between(in common, in a, in w.Table, ref ours);
        Operation[] yours = null;
        int yoursCount = StructuralDiff.Between(in common, in b, in w.Table, ref yours);

        Assert.Equal(1, oursCount);
        Assert.Equal(1, yoursCount);
        Assert.NotEqual(ours[0].Node, yours[0].Node);
    }

    [Fact]
    public void Two_different_sources_are_not_two_states_of_one()
    {
        World a = Build();
        World b = Build();

        Operation[] ops = null;
        Assert.Throws<InvalidOperationException>(
            () => StructuralDiff.Between(in a.Src, in b.Src, in a.Table, ref ops));
    }

    [Fact]
    public void Applying_a_diff_reproduces_the_state_over_two_hundred_edit_sequences()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            World w = Build();
            Churn(ref w, new Random(seed), 120);
            Source before = Copy(in w.Src);

            Churn(ref w, new Random(seed * 7919 + 13), 120);
            Source after = Copy(in w.Src);

            try
            {
                Source replayed = Replay(in w, in before, in after, out int count);
                AssertSameState(in after, in replayed);
            }
            catch (Exception e)
            {
                throw new Exception("seed " + seed + ": " + e.Message, e);
            }
        }
    }
}
