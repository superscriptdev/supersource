using System;
using System.Text;
using Supersource.Derive;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Derive.Tests;

public class Spec_Derive
{
    static class Cad
    {
        public static readonly SourceId Library = SourceId.Mint();
        public static readonly QualifiedName Geometry = new QualifiedName(Library, "geometry");
        public static readonly QualifiedName Sketch = new QualifiedName(Library, "sketch");
        public static readonly QualifiedName Mesh = new QualifiedName(Library, "mesh");
        public static readonly QualifiedName Gcode = new QualifiedName(Library, "gcode");
        public static readonly QualifiedName Fn = new QualifiedName(Library, "fn");
        public static readonly QualifiedName Doc = new QualifiedName(Library, "doc");
        public static readonly QualifiedName Documents = new QualifiedName(Library, "documents");
    }

    struct Bracket
    {
        public Source Src;
        public KindTable Table;
        public KindBootstrap.Ids Boot;
        public DeriveKinds.Ids D;

        public NodeId Root;
        public NodeId Model, Profile;     // authored
        public NodeId ToMesh, Tessellate; // derivation + transform
        public NodeId MeshNode;           // derived
        public NodeId Slice, Slicer;
        public NodeId GcodeNode;          // derived
        public NodeId Note;
    }

    /// <summary>
    /// Two-stage derivation fixture for testing transitive provenance.
    ///
    /// <code>
    /// source "bracket"
    /// ├── geometry "bracket-model"          ← AUTHORED
    /// │   ├── sketch "base-profile"
    /// │   └── extrude --profile--> sketch
    /// ├── derivation "to-mesh"
    /// │   ├── --input-----> geometry
    /// │   └── --transform--> fn "tessellate"
    /// ├── mesh "bracket-mesh"      [derived]
    /// │   └── --produced-by--> derivation "to-mesh"
    /// ├── derivation "slice"
    /// │   ├── --input-----> mesh
    /// │   └── --transform--> fn "slice"
    /// ├── gcode "bracket.gcode"    [derived, opaque]
    /// │   └── --produced-by--> derivation "slice"
    /// └── doc "0.01 because the M3 holes closed up at 0.05"
    ///         --documents--> derivation "to-mesh"
    /// </code>
    /// </summary>
    static Bracket Build()
    {
        Bracket b = default;
        b.Table = KindBootstrap.CreateTable(out b.Boot);
        b.D = DeriveKinds.Install(ref b.Table);

        KindId geometry = KindTable.Intern(ref b.Table, Cad.Geometry);
        KindId sketch = KindTable.Intern(ref b.Table, Cad.Sketch);
        KindId mesh = KindTable.Intern(ref b.Table, Cad.Mesh);
        KindId gcode = KindTable.Intern(ref b.Table, Cad.Gcode);
        KindId fn = KindTable.Intern(ref b.Table, Cad.Fn);
        KindId doc = KindTable.Intern(ref b.Table, Cad.Doc);
        RelationId documents = KindTable.InternRelation(ref b.Table, Cad.Documents);

        foreach (KindId k in new[] { geometry, sketch, mesh, gcode, fn, doc })
        {
            KindTable.DefineSchema(ref b.Table, k, StructuralFact.None,
                PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        }

        b.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref b.Src);
        b.Root = SourceBuild.AddRoot(ref b.Src, geometry);
        Text(ref b.Src, b.Root, "bracket");

        b.Model = SourceBuild.AddChild(ref b.Src, b.Root, geometry);
        Text(ref b.Src, b.Model, "bracket-model");
        b.Profile = SourceBuild.AddChild(ref b.Src, b.Model, sketch);
        Text(ref b.Src, b.Profile, "base-profile");

        b.Tessellate = SourceBuild.AddChild(ref b.Src, b.Root, fn);
        Text(ref b.Src, b.Tessellate, "tessellate");

        b.ToMesh = SourceBuild.AddChild(ref b.Src, b.Root, b.D.Derivation);
        SourceBuild.SetPayload(ref b.Src, b.ToMesh, DeriveKinds.EncodeTier(Tier.ContentAddressed));
        SourceBuild.AddRef(ref b.Src, b.ToMesh, b.D.Input, b.Model);
        SourceBuild.AddRef(ref b.Src, b.ToMesh, b.D.Transform, b.Tessellate);

        b.MeshNode = SourceBuild.AddChild(ref b.Src, b.Root, mesh);
        Text(ref b.Src, b.MeshNode, "bracket-mesh");
        b.Src.Nodes[b.MeshNode.Index].Flags |= NodeFlags.Derived;
        SourceBuild.AddRef(ref b.Src, b.MeshNode, b.D.ProducedBy, b.ToMesh);

        b.Slicer = SourceBuild.AddChild(ref b.Src, b.Root, fn);
        Text(ref b.Src, b.Slicer, "slice");

        b.Slice = SourceBuild.AddChild(ref b.Src, b.Root, b.D.Derivation);
        SourceBuild.SetPayload(ref b.Src, b.Slice, DeriveKinds.EncodeTier(Tier.ContentAddressed));
        SourceBuild.AddRef(ref b.Src, b.Slice, b.D.Input, b.MeshNode);
        SourceBuild.AddRef(ref b.Src, b.Slice, b.D.Transform, b.Slicer);

        b.GcodeNode = SourceBuild.AddChild(ref b.Src, b.Root, gcode);
        Text(ref b.Src, b.GcodeNode, "bracket.gcode");
        b.Src.Nodes[b.GcodeNode.Index].Flags |= NodeFlags.Derived;
        SourceBuild.AddRef(ref b.Src, b.GcodeNode, b.D.ProducedBy, b.Slice);

        b.Note = SourceBuild.AddChild(ref b.Src, b.Root, doc);
        Text(ref b.Src, b.Note, "0.01 because the M3 holes closed up at 0.05");
        SourceBuild.AddRef(ref b.Src, b.Note, documents, b.ToMesh);

        return b;
    }

    static void Text(ref Source src, NodeId id, string s)
    {
        SourceBuild.SetPayload(ref src, id, Encoding.UTF8.GetBytes(s));
    }

    [Fact]
    public void Editing_a_sketch_makes_everything_downstream_of_it_stale()
    {
        Bracket b = Build();

        NodeId[] stale = null;
        int count = Staleness.Downstream(in b.Src, in b.D, b.Profile, ref stale);

        Assert.Equal(2, count);
        Assert.Contains(b.MeshNode, new ArraySegment<NodeId>(stale, 0, count));
        Assert.Contains(b.GcodeNode, new ArraySegment<NodeId>(stale, 0, count));

        // Two steps downstream, through a derivation that never mentions the sketch.
        Assert.Equal(b.Slice, Staleness.ProducedBy(in b.Src, in b.D, b.GcodeNode));
    }

    [Fact]
    public void Marking_stale_computes_nothing()
    {
        Bracket b = Build();

        long before = b.Src.NodeCount;
        NodeId[] stale = null;
        Staleness.Downstream(in b.Src, in b.D, b.Profile, ref stale);

        Assert.Equal(before, b.Src.NodeCount);
        // Staleness queries preserve the last computed payload.
        Assert.Equal("bracket-mesh", Encoding.UTF8.GetString(SourceWalk.Payload(in b.Src, b.MeshNode)));
    }

    [Fact]
    public void An_edit_with_no_dependents_makes_nothing_stale()
    {
        Bracket b = Build();

        NodeId[] stale = null;
        Assert.Equal(0, Staleness.Downstream(in b.Src, in b.D, b.Note, ref stale));
    }

    [Fact]
    public void A_derivation_is_authored_and_its_output_is_derived()
    {
        Bracket b = Build();

        Assert.False(b.Src.Nodes[b.ToMesh.Index].IsDerived);
        Assert.True(b.Src.Nodes[b.MeshNode.Index].IsDerived);
        Assert.True(b.Src.Nodes[b.GcodeNode.Index].IsDerived);
    }

    [Fact]
    public void An_artifact_can_be_traced_back_through_every_step_that_made_it()
    {
        Bracket b = Build();

        NodeId[] chain = null;
        int count = Staleness.Provenance(in b.Src, in b.D, b.GcodeNode, ref chain);

        Assert.Equal(2, count);
        Assert.Equal(b.Slice, chain[0]);
        Assert.Equal(b.ToMesh, chain[1]);
    }

    [Fact]
    public void A_derivation_can_be_documented_like_anything_else()
    {
        Bracket b = Build();

        RelationId documents = KindTable.LookupRelation(in b.Table, Cad.Documents);
        Assert.Equal(b.ToMesh, SourceWalk.Target(in b.Src, b.Note, documents));
        Assert.Equal(
            "0.01 because the M3 holes closed up at 0.05",
            Encoding.UTF8.GetString(SourceWalk.Payload(in b.Src, b.Note)));
    }

    [Fact]
    public void The_pipeline_is_edges_not_a_script()
    {
        Bracket b = Build();

        Assert.Equal(b.Tessellate, SourceWalk.Target(in b.Src, b.ToMesh, b.D.Transform));
        Assert.Equal(b.Slicer, SourceWalk.Target(in b.Src, b.Slice, b.D.Transform));

        NodeId[] inputs = null;
        Assert.Equal(1, Staleness.Inputs(in b.Src, in b.D, b.Slice, ref inputs));
        Assert.Equal(b.MeshNode, inputs[0]);
    }

    [Fact]
    public void An_undeclared_tier_reads_as_the_default_not_as_bit_exact()
    {
        Assert.Equal(Tier.ContentAddressed, DeriveKinds.DecodeTier(ReadOnlySpan<byte>.Empty));
        Assert.Equal(Tier.BitExact, DeriveKinds.DecodeTier(DeriveKinds.EncodeTier(Tier.BitExact)));
        Assert.Equal(Tier.Captured, DeriveKinds.DecodeTier(DeriveKinds.EncodeTier(Tier.Captured)));
    }

    [Fact]
    public void A_build_without_the_transform_still_reads_the_pipeline_and_the_staleness()
    {
        Bracket b = Build();

        // Nothing here can run `tessellate` or `slice` — they are just nodes with labels.
        Assert.Equal(
            Tier.ContentAddressed,
            DeriveKinds.DecodeTier(SourceWalk.Payload(in b.Src, b.ToMesh)));

        NodeId[] stale = null;
        Assert.True(Staleness.Downstream(in b.Src, in b.D, b.Model, ref stale) > 0);
    }

    [Fact]
    public void A_long_provenance_chain_is_walked_once_per_edge()
    {
        Bracket w = Build();
        KindId mesh = w.Src.Nodes[w.MeshNode.Index].Kind;

        NodeId previous = SourceBuild.AddChild(ref w.Src, w.Root, mesh);
        NodeId first = previous;

        const int Length = 2000;
        for (int i = 0; i < Length; i++)
        {
            NodeId derivation = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
            SourceBuild.AddRef(ref w.Src, derivation, w.D.Input, previous);

            NodeId artifact = SourceBuild.AddChild(ref w.Src, w.Root, mesh);
            w.Src.Nodes[artifact.Index].Flags |= NodeFlags.Derived;
            SourceBuild.AddRef(ref w.Src, artifact, w.D.ProducedBy, derivation);

            previous = artifact;
        }

        NodeId[] stale = null;
        int found = Staleness.Downstream(in w.Src, in w.D, first, ref stale);

        Assert.Equal(Length, found);
        Assert.Equal(previous, stale[Length - 1]);
    }

    [Fact]
    public void Walking_with_a_supplied_index_answers_the_same()
    {
        Bracket w = Build();
        KindId mesh = w.Src.Nodes[w.MeshNode.Index].Kind;

        NodeId sketch = SourceBuild.AddChild(ref w.Src, w.Root, mesh);
        NodeId derivation = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
        SourceBuild.AddRef(ref w.Src, derivation, w.D.Input, sketch);
        NodeId artifact = SourceBuild.AddChild(ref w.Src, w.Root, mesh);
        w.Src.Nodes[artifact.Index].Flags |= NodeFlags.Derived;
        SourceBuild.AddRef(ref w.Src, artifact, w.D.ProducedBy, derivation);

        NodeId[] built = null;
        int one = Staleness.Downstream(in w.Src, in w.D, sketch, ref built);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        NodeId[] supplied = null;
        int two = Staleness.Downstream(in w.Src, in w.D, in index, sketch, ref supplied);

        Assert.Equal(one, two);
        for (int i = 0; i < one; i++)
        {
            Assert.Equal(built[i], supplied[i]);
        }
    }

    [Fact]
    public void A_stale_index_is_refused_rather_than_trusted()
    {
        Bracket w = Build();
        NodeId sketch = SourceBuild.AddChild(
            ref w.Src, w.Root, w.Src.Nodes[w.MeshNode.Index].Kind);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);

        NodeId[] stale = null;
        Assert.Throws<InvalidOperationException>(
            () => Staleness.Downstream(in w.Src, in w.D, in index, sketch, ref stale));
    }
}
