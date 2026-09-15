using System;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Tests;

public class Spec_Deep
{
    const int Depth = 200_000;

    static Source Chain(out NodeId top, out NodeId deepest)
    {
        KindTable table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        KindId kind = KindTable.Intern(ref table, new QualifiedName(SourceId.Mint(), "link"));

        Source src = Source.Create(Depth + 2);
        SourceBuild.MintIdentity(ref src);

        Source.EnsureNodeCapacity(ref src, Depth + 1);
        src.NodeCount = Depth + 1;

        for (int i = 0; i <= Depth; i++)
        {
            src.Nodes[i] = default;
            src.Nodes[i].Kind = kind;
            src.Nodes[i].Parent = i == 0 ? NodeId.None : new NodeId(i - 1);
            src.Nodes[i].Flags = NodeFlags.Live;

            if (i < Depth)
            {
                int at = Source.ReserveChildren(ref src, 1);
                src.Children[at] = i + 1;
                src.Nodes[i].ChildOffset = at;
                src.Nodes[i].ChildCount = 1;
                src.Capacities[i].Child = 1;
            }
        }

        src.Root = new NodeId(0);
        top = new NodeId(1);
        deepest = new NodeId(Depth);
        return src;
    }

    [Fact]
    public void Deleting_a_very_deep_subtree_does_not_take_the_process_down()
    {
        Source src = Chain(out NodeId top, out NodeId deepest);

        SourceBuild.Delete(ref src, top);

        Assert.False(src.IsLive(top));
        Assert.False(src.IsLive(deepest));
        Assert.True(src.IsLive(src.Root));
    }

    [Fact]
    public void Reviving_a_very_deep_subtree_does_not_either()
    {
        Source src = Chain(out NodeId top, out NodeId deepest);

        SourceBuild.Delete(ref src, top);
        SourceBuild.InsertChild(ref src, src.Root, 0, top);
        SourceBuild.Revive(ref src, top);

        Assert.True(src.IsLive(top));
        Assert.True(src.IsLive(deepest));
    }

    [Fact]
    public void A_very_deep_chain_walks_end_to_end()
    {
        Source src = Chain(out NodeId top, out NodeId deepest);

        int walked = 0;
        NodeId at = deepest;
        while (at.IsSome)
        {
            walked++;
            at = SourceWalk.Parent(in src, at);
        }

        Assert.Equal(Depth + 1, walked);
    }
}
