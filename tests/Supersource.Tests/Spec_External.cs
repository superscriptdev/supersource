using System;
using System.Text;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Kinds.Tests;

public class Spec_External
{
    struct World
    {
        public Source Src;
        public KindTable Table;
        public TestKinds.Ids Boot;
        public ExternalKinds.Ids Ext;
        public NodeId Root;
        public KindId Thing;
        public RelationId Uses;
    }

    static World Build()
    {
        World w = default;
        w.Table = TestKinds.CreateTable(out w.Boot);
        w.Ext = ExternalKinds.Install(ref w.Table);

        SourceId library = SourceId.Mint();
        w.Thing = KindTable.Intern(ref w.Table, new QualifiedName(library, "thing"));
        w.Uses = KindTable.InternRelation(ref w.Table, new QualifiedName(library, "uses"));
        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.Annotation,
            PayloadFormat.Text, new RelationId[] { w.Uses });

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Thing);
        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("project"));
        return w;
    }

    static External Binary(string locator, byte[] content)
    {
        External e = default;
        e.Locator = locator;
        e.Hash = ContentHash.Of(content);
        return e;
    }

    static External Into(SourceId source, ContentHash version, long node)
    {
        External e = default;
        e.Locator = "https://libs.example/geometry.ss";
        e.Source = source;
        e.Hash = version;
        e.Node = node;
        return e;
    }

    static int Dependencies(in World w)
    {
        int count = 0;
        for (long i = 0; i < w.Src.NodeCount; i++)
        {
            if (w.Src.IsLive(new NodeId(i)) && w.Src.Nodes[i].Kind == w.Ext.Dependency)
            {
                count++;
            }
        }
        return count;
    }

    [Fact]
    public void An_external_reference_is_an_ordinary_node_pointed_at_by_an_ordinary_edge()
    {
        World w = Build();

        byte[] texture = Encoding.UTF8.GetBytes("...pretend this is a PNG...");
        NodeId external = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/brick.png", texture));

        NodeId material = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.SetPayload(ref w.Src, material, Encoding.UTF8.GetBytes("brick"));
        SourceBuild.AddRef(ref w.Src, material, w.Uses, external);

        // Nothing special about the edge — the same mechanism as any other reference.
        Assert.Equal(external, SourceWalk.Target(in w.Src, material, w.Uses));
        Assert.Equal(w.Root, SourceWalk.Parent(in w.Src, external));
    }

    [Fact]
    public void The_common_case_pays_nothing_for_the_exception()
    {
        World w = Build();
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/brick.png", new byte[] { 1 }));

        NodeId a = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        NodeId b = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddRef(ref w.Src, a, w.Uses, b);

        // An intra-source reference is still nothing but an id.
        Ref edge = SourceWalk.RefAt(in w.Src, a, 0);
        Assert.Equal(b, edge.Target);
        Assert.Equal(0, edge.DataLength);
    }

    [Fact]
    public void An_external_reference_holds_a_node_id_and_its_dependency_holds_the_address()
    {
        World w = Build();
        SourceId geometry = SourceId.Mint();
        ContentHash version = ContentHash.Of(Encoding.UTF8.GetBytes("geometry, some version"));

        NodeId node = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, version, 4242));

        // The reference's own payload is the node id and nothing else.
        Assert.Equal(8, SourceWalk.Payload(in w.Src, node).Length);

        NodeId dependency = ExternalKinds.DependencyOf(in w.Src, node);
        Assert.Equal(w.Ext.Dependency, w.Src.Nodes[dependency.Index].Kind);
        Assert.Equal(w.Ext.Of, SourceWalk.RefAt(in w.Src, node, 0).Relation);

        Assert.True(ExternalKinds.TryDecode(in w.Src, node, out External decoded));
        Assert.Equal("https://libs.example/geometry.ss", decoded.Locator);
        Assert.Equal(version, decoded.Hash);
        Assert.Equal(geometry, decoded.Source);
        Assert.Equal(4242, decoded.Node);
    }

    [Fact]
    public void References_into_one_version_of_a_source_share_its_dependency()
    {
        World w = Build();
        SourceId geometry = SourceId.Mint();
        ContentHash version = ContentHash.Of(new byte[] { 7 });

        NodeId first = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, version, 1));
        NodeId second = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, version, 2));

        Assert.Equal(1, Dependencies(in w));
        Assert.Equal(ExternalKinds.DependencyOf(in w.Src, first), ExternalKinds.DependencyOf(in w.Src, second));
    }

    [Fact]
    public void Two_versions_of_one_source_are_two_dependencies()
    {
        World w = Build();
        SourceId geometry = SourceId.Mint();

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, ContentHash.Of(new byte[] { 1 }), 1));
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, ContentHash.Of(new byte[] { 2 }), 1));

        Assert.Equal(2, Dependencies(in w));
    }

    [Fact]
    public void Unpinned_foreign_files_are_told_apart_by_their_locators()
    {
        World w = Build();
        External a = default;
        a.Locator = "https://cdn.example/a.png";
        External b = default;
        b.Locator = "https://cdn.example/b.png";

        NodeId first = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, a);
        NodeId second = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, b);
        NodeId again = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, a);

        Assert.Equal(2, Dependencies(in w));
        Assert.NotEqual(ExternalKinds.DependencyOf(in w.Src, first), ExternalKinds.DependencyOf(in w.Src, second));
        Assert.Equal(ExternalKinds.DependencyOf(in w.Src, first), ExternalKinds.DependencyOf(in w.Src, again));
    }

    [Fact]
    public void Repinning_a_dependency_moves_every_reference_into_it()
    {
        World w = Build();
        SourceId geometry = SourceId.Mint();
        ContentHash old = ContentHash.Of(new byte[] { 1 });
        ContentHash next = ContentHash.Of(new byte[] { 2 });

        NodeId first = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, old, 1));
        NodeId second = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, Into(geometry, old, 2));

        // One payload edit: the address is stored once.
        NodeId dependency = ExternalKinds.DependencyOf(in w.Src, first);
        SourceBuild.SetPayload(ref w.Src, dependency,
            ExternalKinds.EncodeDependency(geometry, next, "https://libs.example/geometry.ss"));

        Assert.True(ExternalKinds.TryDecode(in w.Src, first, out External a));
        Assert.True(ExternalKinds.TryDecode(in w.Src, second, out External b));
        Assert.Equal(next, a.Hash);
        Assert.Equal(next, b.Hash);
    }

    [Fact]
    public void Retargeting_one_reference_keeps_its_id_and_the_edges_into_it()
    {
        World w = Build();
        SourceId geometry = SourceId.Mint();
        NodeId external = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Into(geometry, ContentHash.Of(new byte[] { 1 }), 1));
        NodeId user = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddRef(ref w.Src, user, w.Uses, external);

        ContentHash next = ContentHash.Of(new byte[] { 2 });
        ExternalKinds.Retarget(ref w.Src, in w.Ext, external, Into(geometry, next, 9));

        Assert.Equal(external, SourceWalk.Target(in w.Src, user, w.Uses));
        Assert.True(ExternalKinds.TryDecode(in w.Src, external, out External now));
        Assert.Equal(next, now.Hash);
        Assert.Equal(9, now.Node);
        Assert.Equal(1, SourceWalk.RefCount(in w.Src, external));
    }

    [Fact]
    public void A_reference_whose_dependency_is_deleted_no_longer_decodes()
    {
        World w = Build();
        NodeId external = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Into(SourceId.Mint(), ContentHash.Of(new byte[] { 1 }), 1));

        SourceBuild.Delete(ref w.Src, ExternalKinds.DependencyOf(in w.Src, external));

        // Deletion means gone: the `of` edge dangles, like any reference to a deleted node.
        Assert.False(ExternalKinds.TryDecode(in w.Src, external, out _));
    }

    [Fact]
    public void Content_from_a_mirror_verifies_because_the_hash_is_the_identity()
    {
        byte[] content = Encoding.UTF8.GetBytes("the actual bytes");
        External reference = Binary("https://origin.example/thing", content);

        // Same bytes from somewhere else entirely.
        Assert.True(Externals.Verify(in reference, content));

        // Different bytes from the right place are still wrong.
        Assert.False(Externals.Verify(in reference, Encoding.UTF8.GetBytes("tampered")));
    }

    [Fact]
    public void A_reference_with_no_hash_verifies_nothing()
    {
        External unhashed = default;
        unhashed.Locator = "https://example/thing";
        Assert.False(Externals.Verify(in unhashed, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void What_a_source_depends_on_is_computed_from_the_graph_rather_than_stored()
    {
        World w = Build();

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }));
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Into(SourceId.Mint(), ContentHash.Of(new byte[] { 2 }), 7));

        ExternalUse[] uses = null;
        int count = Externals.Compute(in w.Src, in w.Ext, ref uses);

        Assert.Equal(2, count);
        Assert.Contains(uses[0..count], e => !e.IsSource);   // the binary
        Assert.Contains(uses[0..count], e => e.IsSource);    // the other source

        // And it tracks the graph: add one more and the answer changes with no bookkeeping.
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/b.png", new byte[] { 3 }));
        uses = null;
        Assert.Equal(3, Externals.Compute(in w.Src, in w.Ext, ref uses));
    }

    [Fact]
    public void Deleting_a_reference_drops_it_from_what_the_source_depends_on()
    {
        World w = Build();
        NodeId first = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }));
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/b.png", new byte[] { 2 }));

        SourceBuild.Delete(ref w.Src, first);

        ExternalUse[] uses = null;
        Assert.Equal(1, Externals.Compute(in w.Src, in w.Ext, ref uses));
    }

    [Fact]
    public void An_unpinned_reference_is_reported()
    {
        World w = Build();

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/pinned.png", new byte[] { 1 }));
        External latest = default;
        latest.Locator = "https://cdn.example/latest.png";
        NodeId drifting = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, latest);

        NodeId[] unpinned = null;
        int count = Externals.Unpinned(in w.Src, in w.Ext, ref unpinned);

        Assert.Equal(1, count);
        Assert.Equal(drifting, unpinned[0]);

        // Missing pins do not make the source structurally invalid.
        Assert.True(w.Src.IsLive(drifting));
    }

    [Fact]
    public void How_self_contained_a_source_is_is_a_query()
    {
        World w = Build();
        Assert.Equal(0, Externals.LinkedCount(in w.Src, in w.Ext));

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }));

        Assert.Equal(1, Externals.LinkedCount(in w.Src, in w.Ext));
    }
}
