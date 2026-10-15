using System;
using System.Text;
using Supersource.Kinds;
using Supersource.Resolve;
using Xunit;

namespace Supersource.Tests;

/// <summary>
/// A workspace registration is a copy of the <see cref="Source"/> struct, and that struct holds
/// array references and counts. Editing a registered source replaces those arrays, so the
/// registration goes stale: it keeps answering from the graph as it stood when it was added.
/// These checks fix what the workspace does about that.
/// </summary>
public class Spec_WorkspaceFreshness
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName Leaf = new QualifiedName(Library, "leaf");

    static Source Grown(out KindId kind, int children)
    {
        Source src = Source.Create(4);
        SourceBuild.MintIdentity(ref src);
        kind = new KindId(1);
        NodeId root = SourceBuild.AddRoot(ref src, kind);
        for (int i = 0; i < children; i++)
        {
            SourceBuild.AddChild(ref src, root, kind);
        }
        return src;
    }

    static External Pin(in Source src, NodeId node)
    {
        External external = default;
        external.Source = src.Identity;
        external.Node = node.Index;
        return external;
    }

    [Fact]
    public void A_registration_made_before_an_edit_does_not_see_nodes_added_after_it()
    {
        Source src = Grown(out KindId kind, 2);
        Workspace.Set set = Workspace.Create(2);
        Workspace.Add(ref set, src.Identity, default, in src);

        // Enough growth to reallocate the node array out from under the registered copy.
        NodeId added = default;
        for (int i = 0; i < 64; i++)
        {
            added = SourceBuild.AddChild(ref src, src.Root, kind);
        }

        CrossRef stale = Workspace.Resolve(in set, Pin(in src, added));
        Assert.Equal(CrossResolution.NodeMissing, stale.How);
    }

    [Fact]
    public void Refreshing_the_registration_makes_it_answer_from_the_current_graph()
    {
        Source src = Grown(out KindId kind, 2);
        Workspace.Set set = Workspace.Create(2);
        int at = Workspace.Add(ref set, src.Identity, default, in src);

        NodeId added = default;
        for (int i = 0; i < 64; i++)
        {
            added = SourceBuild.AddChild(ref src, src.Root, kind);
        }

        Workspace.Update(ref set, at, in src);

        CrossRef fresh = Workspace.Resolve(in set, Pin(in src, added));
        Assert.Equal(CrossResolution.Bound, fresh.How);
        Assert.Equal(added, fresh.Node);
        Assert.Equal(at, fresh.Source);
    }

    [Fact]
    public void Refreshing_keeps_the_identity_and_hash_the_loader_supplied()
    {
        Source src = Grown(out KindId kind, 2);
        ContentHash hash = ContentHash.Of(Encoding.UTF8.GetBytes("the bytes it was loaded from"));

        Workspace.Set set = Workspace.Create(2);
        int at = Workspace.Add(ref set, src.Identity, hash, in src);

        SourceBuild.AddChild(ref src, src.Root, kind);
        Workspace.Update(ref set, at, in src);

        Assert.Equal(src.Identity, set.Sources[at].Identity);
        Assert.Equal(hash, set.Sources[at].Hash);

        External pinned = Pin(in src, src.Root);
        pinned.Hash = hash;
        Assert.Equal(CrossResolution.Bound, Workspace.Resolve(in set, pinned).How);
    }

    [Fact]
    public void A_registration_reports_whether_a_source_has_moved_past_it()
    {
        Source src = Grown(out KindId kind, 2);
        Workspace.Set set = Workspace.Create(2);
        int at = Workspace.Add(ref set, src.Identity, default, in src);

        Assert.False(Workspace.IsStale(in set, at, in src));

        SourceBuild.AddChild(ref src, src.Root, kind);
        Assert.True(Workspace.IsStale(in set, at, in src));

        Workspace.Update(ref set, at, in src);
        Assert.False(Workspace.IsStale(in set, at, in src));
    }

    [Fact]
    public void Staleness_is_asked_about_one_registration_and_one_live_source()
    {
        Source one = Grown(out _, 2);
        Source two = Grown(out _, 2);

        Workspace.Set set = Workspace.Create(2);
        int first = Workspace.Add(ref set, one.Identity, default, in one);

        // A different source is not "the same registration, moved on".
        Assert.True(Workspace.IsStale(in set, first, in two));
        Assert.False(Workspace.IsStale(in set, first, in one));
    }

    [Fact]
    public void An_index_outside_the_set_is_refused_rather_than_silently_ignored()
    {
        Source src = Grown(out _, 2);
        Workspace.Set set = Workspace.Create(2);
        Workspace.Add(ref set, src.Identity, default, in src);

        Assert.Throws<ArgumentOutOfRangeException>(() => Workspace.Update(ref set, 7, in src));
        Assert.Throws<ArgumentOutOfRangeException>(() => Workspace.Update(ref set, -1, in src));
    }

    [Fact]
    public void A_registration_is_not_refreshed_from_a_different_source()
    {
        Source one = Grown(out _, 2);
        Source two = Grown(out _, 2);

        Workspace.Set set = Workspace.Create(2);
        int at = Workspace.Add(ref set, one.Identity, default, in one);

        Assert.Throws<ArgumentException>(() => Workspace.Update(ref set, at, in two));
        Assert.Equal(one.Identity, set.Sources[at].Src.Identity);
    }

    [Fact]
    public void Following_an_external_edge_into_an_edited_source_needs_that_source_refreshed()
    {
        Source target = Grown(out KindId kind, 2);
        NodeId added = default;
        Workspace.Set set = Workspace.Create(2);
        int at = Workspace.Add(ref set, target.Identity, default, in target);

        for (int i = 0; i < 64; i++)
        {
            added = SourceBuild.AddChild(ref target, target.Root, kind);
        }

        // A referring source holding one external-ref node that names the new node.
        Source home = Source.Create(8);
        SourceBuild.MintIdentity(ref home);
        KindTable table = KindTable.Create(8);
        KindId leaf = KindTable.Intern(ref table, Leaf);
        KindId externalRef = KindTable.Intern(ref table, ExternalKinds.ExternalRef);
        RelationId uses = KindTable.InternRelation(ref table, new QualifiedName(Library, "uses"));

        NodeId root = SourceBuild.AddRoot(ref home, leaf);
        NodeId pin = SourceBuild.AddChild(ref home, root, externalRef);
        SourceBuild.SetPayload(ref home, pin, ExternalKinds.Encode(Pin(in target, added)));
        SourceBuild.AddRef(ref home, root, uses, pin);

        int homeAt = Workspace.Add(ref set, home.Identity, default, in home);
        Reach reach = Reach.Of(in set, homeAt, externalRef);

        Reach.At from = Reach.Here(in reach, root);
        Assert.True(Reach.To(in reach, in home, in from, uses).Node.IsNone);

        Workspace.Update(ref set, at, in target);

        Reach.At landed = Reach.To(in reach, in home, in from, uses);
        Assert.Equal(added, landed.Node);
        Assert.Equal(at, landed.Source);
    }
}
