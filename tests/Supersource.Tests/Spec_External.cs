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
        public KindBootstrap.Ids Boot;
        public ExternalKinds.Ids Ext;
        public NodeId Root;
        public KindId Thing;
        public RelationId Uses;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);
        w.Ext = ExternalKinds.Install(ref w.Table);

        SourceId library = SourceId.Mint();
        w.Thing = KindTable.Intern(ref w.Table, new QualifiedName(library, "thing"));
        w.Uses = KindTable.InternRelation(ref w.Table, new QualifiedName(library, "uses"));
        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.Uses });

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Thing);
        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("project"));
        return w;
    }

    static External Binary(string locator, byte[] content, string pin)
    {
        External e = default;
        e.Locator = locator;
        e.Hash = ContentHash.Of(content);
        e.Pin = pin;
        return e;
    }

    [Fact]
    public void An_external_reference_is_an_ordinary_node_pointed_at_by_an_ordinary_edge()
    {
        World w = Build();

        byte[] texture = Encoding.UTF8.GetBytes("...pretend this is a PNG...");
        NodeId external = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/brick.png", texture, "v3"));

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
            Binary("https://cdn.example/brick.png", new byte[] { 1 }, "v3"));

        NodeId a = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        NodeId b = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddRef(ref w.Src, a, w.Uses, b);

        // An intra-source reference is still nothing but an id.
        Ref edge = SourceWalk.RefAt(in w.Src, a, 0);
        Assert.Equal(b, edge.Target);
        Assert.Equal(0, edge.LabelLength);
    }

    [Fact]
    public void Content_from_a_mirror_verifies_because_the_hash_is_the_identity()
    {
        byte[] content = Encoding.UTF8.GetBytes("the actual bytes");
        External reference = Binary("https://origin.example/thing", content, "v1");

        // Same bytes from somewhere else entirely.
        Assert.True(Manifest.Verify(in reference, content));

        // Different bytes from the right place are still wrong.
        Assert.False(Manifest.Verify(in reference, Encoding.UTF8.GetBytes("tampered")));
    }

    [Fact]
    public void A_reference_with_no_hash_verifies_nothing()
    {
        External unhashed = default;
        unhashed.Locator = "https://example/thing";
        Assert.False(Manifest.Verify(in unhashed, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void An_external_reference_round_trips_through_its_payload()
    {
        World w = Build();

        External original = default;
        original.Locator = "https://libs.example/geometry.ss";
        original.Hash = ContentHash.Of(Encoding.UTF8.GetBytes("library bytes"));
        original.Pin = "2.1.0";
        original.Source = SourceId.Mint();
        original.Node = 4242;

        NodeId node = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, original);

        External decoded;
        Assert.True(ExternalKinds.TryDecode(SourceWalk.Payload(in w.Src, node), out decoded));
        Assert.Equal(original.Locator, decoded.Locator);
        Assert.Equal(original.Hash, decoded.Hash);
        Assert.Equal(original.Pin, decoded.Pin);
        Assert.Equal(original.Source, decoded.Source);
        Assert.Equal(original.Node, decoded.Node);
    }

    [Fact]
    public void The_manifest_is_computed_from_the_graph_rather_than_stored()
    {
        World w = Build();

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }, "v1"));

        External library = default;
        library.Locator = "https://libs.example/geometry.ss";
        library.Hash = ContentHash.Of(new byte[] { 2 });
        library.Pin = "2.1.0";
        library.Source = SourceId.Mint();
        library.Node = 7;
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root, library);

        ManifestEntry[] entries = null;
        int count = Manifest.Compute(in w.Src, in w.Ext, ref entries);

        Assert.Equal(2, count);
        Assert.Contains(entries[0..count], e => !e.IsSource);   // the binary
        Assert.Contains(entries[0..count], e => e.IsSource);    // the other source

        // And it tracks the graph: add one more and the answer changes with no bookkeeping.
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/b.png", new byte[] { 3 }, "v1"));
        entries = null;
        Assert.Equal(3, Manifest.Compute(in w.Src, in w.Ext, ref entries));
    }

    [Fact]
    public void The_graph_wins_because_there_is_no_stored_manifest_to_disagree_with_it()
    {
        World w = Build();
        NodeId first = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }, "v1"));
        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/b.png", new byte[] { 2 }, "v1"));

        SourceBuild.Delete(ref w.Src, first);

        ManifestEntry[] entries = null;
        Assert.Equal(1, Manifest.Compute(in w.Src, in w.Ext, ref entries));
    }

    [Fact]
    public void An_unpinned_cross_document_reference_is_reported()
    {
        World w = Build();

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/pinned.png", new byte[] { 1 }, "v1"));
        NodeId drifting = ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/latest.png", new byte[] { 2 }, null));

        NodeId[] unpinned = null;
        int count = Manifest.Unpinned(in w.Src, in w.Ext, ref unpinned);

        Assert.Equal(1, count);
        Assert.Equal(drifting, unpinned[0]);

        // Missing pins do not make the source structurally invalid.
        Assert.True(w.Src.IsLive(drifting));
    }

    [Fact]
    public void How_self_contained_a_document_is_is_a_query()
    {
        World w = Build();
        Assert.Equal(0, Manifest.LinkedCount(in w.Src, in w.Ext));

        ExternalKinds.Declare(ref w.Src, in w.Ext, w.Root,
            Binary("https://cdn.example/a.png", new byte[] { 1 }, "v1"));

        Assert.Equal(1, Manifest.LinkedCount(in w.Src, in w.Ext));
    }
}
