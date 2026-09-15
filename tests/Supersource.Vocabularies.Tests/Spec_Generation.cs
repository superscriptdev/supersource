using System;
using System.Text;
using Supersource.Derive;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Derive.Tests;

public class Spec_Generation
{
    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindBootstrap.Ids Boot;
        public DeriveKinds.Ids D;
        public KindId Geometry, Size, Note;
        public RelationId About;
        public NodeId Root, Sizes, Generator;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);
        w.D = DeriveKinds.Install(ref w.Table);

        SourceId library = SourceId.Mint();
        w.Geometry = KindTable.Intern(ref w.Table, new QualifiedName(library, "geometry"));
        w.Size = KindTable.Intern(ref w.Table, new QualifiedName(library, "hole-size"));
        w.Note = KindTable.Intern(ref w.Table, new QualifiedName(library, "note"));
        w.About = KindTable.InternRelation(ref w.Table, new QualifiedName(library, "about"));

        KindTable.DefineSchema(ref w.Table, w.Geometry, StructuralFact.Declaration,
            PayloadFormat.Opaque, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Size, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Note, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About });

        w.Src = Source.Create(64);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Geometry);
        w.Sizes = SourceBuild.AddChild(ref w.Src, w.Root, w.Geometry);

        w.Generator = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
        SourceBuild.SetPayload(ref w.Src, w.Generator, DeriveKinds.EncodeTier(Tier.BitExact));

        return w;
    }

    static NodeId Size(ref World w, string value)
    {
        byte[] wanted = Encoding.UTF8.GetBytes(value);

        ReadOnlySpan<long> children = SourceWalk.Children(in w.Src, w.Sizes);
        for (int i = 0; i < children.Length; i++)
        {
            NodeId child = new NodeId(children[i]);
            if (SourceWalk.Payload(in w.Src, child).SequenceEqual(wanted))
            {
                return child;
            }
        }

        NodeId made = SourceBuild.AddChild(ref w.Src, w.Sizes, w.Size);
        SourceBuild.SetPayload(ref w.Src, made, wanted);
        return made;
    }

    static int Run(ref World w, ref NodeId[] into, params string[] values)
    {
        NodeId[] keys = new NodeId[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            keys[i] = Size(ref w, values[i]);
        }

        int count = Generation.Regenerate(ref w.Src, in w.D, w.Generator, w.Root,
            new ReadOnlySpan<NodeId>(keys), w.Geometry, ref into);

        for (int i = 0; i < count; i++)
        {
            SourceBuild.SetPayload(ref w.Src, into[i],
                Encoding.UTF8.GetBytes("bracket with a " + values[i] + " mm hole"));
        }

        return count;
    }

    [Fact]
    public void Adding_a_variant_leaves_every_other_id_untouched()
    {
        World w = Build();
        NodeId[] first = null;
        int count = Run(ref w, ref first, "3", "4", "5", "6");
        Assert.Equal(4, count);

        NodeId three = first[0], four = first[1], five = first[2], six = first[3];

        NodeId[] second = null;
        Assert.Equal(5, Run(ref w, ref second, "3", "4", "5", "6", "8"));

        Assert.Equal(three, second[0]);
        Assert.Equal(four, second[1]);
        Assert.Equal(five, second[2]);
        Assert.Equal(six, second[3]);
        Assert.NotEqual(six, second[4]);   // the only new node
    }

    [Fact]
    public void Reordering_the_input_changes_nothing()
    {
        World w = Build();
        NodeId[] first = null;
        Run(ref w, ref first, "3", "4", "5", "6");
        NodeId three = first[0], six = first[3];

        NodeId[] second = null;
        Run(ref w, ref second, "6", "5", "4", "3");

        Assert.Equal(six, second[0]);
        Assert.Equal(three, second[3]);
    }

    [Fact]
    public void A_note_on_a_generated_node_survives_an_insertion_at_the_front()
    {
        World w = Build();
        NodeId[] first = null;
        Run(ref w, ref first, "4", "5", "6");
        NodeId five = first[1];

        NodeId note = SourceBuild.AddChild(ref w.Src, w.Root, w.Note);
        SourceBuild.SetPayload(ref w.Src, note, Encoding.UTF8.GetBytes("this one needs a chamfer"));
        SourceBuild.AddRef(ref w.Src, note, w.About, five);

        // Insert at the FRONT — the exact shape that destroys index-keyed generation.
        NodeId[] second = null;
        Run(ref w, ref second, "3", "4", "5", "6");

        Assert.Equal(five, SourceWalk.Target(in w.Src, note, w.About));
        Assert.True(w.Src.IsLive(five));
        Assert.Equal(five, second[2]);
    }

    [Fact]
    public void Removing_a_key_removes_exactly_that_node()
    {
        World w = Build();
        NodeId[] first = null;
        Run(ref w, ref first, "3", "4", "5");
        NodeId three = first[0], four = first[1], five = first[2];

        NodeId[] second = null;
        Assert.Equal(2, Run(ref w, ref second, "3", "5"));

        Assert.True(w.Src.IsLive(three));
        Assert.False(w.Src.IsLive(four));
        Assert.True(w.Src.IsLive(five));
    }

    [Fact]
    public void A_generated_node_names_the_node_it_came_from()
    {
        World w = Build();
        NodeId[] made = null;
        Run(ref w, ref made, "3", "4");

        Assert.Equal(Size(ref w, "3"), Generation.KeyOf(in w.Src, in w.D, made[0]));
        Assert.Equal(Size(ref w, "4"), Generation.KeyOf(in w.Src, in w.D, made[1]));
    }

    [Fact]
    public void A_generated_node_carries_its_own_content()
    {
        World w = Build();
        NodeId[] made = null;
        Run(ref w, ref made, "5");

        Assert.Equal(
            "bracket with a 5 mm hole",
            Encoding.UTF8.GetString(SourceWalk.Payload(in w.Src, made[0])));

        // And regenerating does not overwrite it.
        NodeId[] again = null;
        Run(ref w, ref again, "5");
        Assert.Equal(made[0], again[0]);
    }

    [Fact]
    public void Two_generators_over_one_input_do_not_collide()
    {
        World w = Build();
        NodeId five = Size(ref w, "5");

        NodeId other = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
        SourceBuild.SetPayload(ref w.Src, other, DeriveKinds.EncodeTier(Tier.BitExact));

        NodeId[] mine = null;
        Run(ref w, ref mine, "5");

        NodeId[] theirs = null;
        Generation.Regenerate(ref w.Src, in w.D, other, w.Root,
            new ReadOnlySpan<NodeId>(new NodeId[] { five }), w.Geometry, ref theirs);

        Assert.NotEqual(mine[0], theirs[0]);
        Assert.Equal(five, Generation.KeyOf(in w.Src, in w.D, mine[0]));
        Assert.Equal(five, Generation.KeyOf(in w.Src, in w.D, theirs[0]));

        // And neither generator sweeps the other's node.
        NodeId[] second = null;
        Run(ref w, ref second, "5");
        Assert.True(w.Src.IsLive(theirs[0]));
    }

    [Fact]
    public void Two_people_regenerating_independently_derive_the_same_keys()
    {
        World a = Build();
        World b = Build();

        string[] values = new string[] { "3", "4", "5" };

        NodeId[] mine = null;
        NodeId[] theirs = null;
        Run(ref a, ref mine, values);
        Run(ref b, ref theirs, values);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(Size(ref a, values[i]), Generation.KeyOf(in a.Src, in a.D, mine[i]));
            Assert.Equal(Size(ref b, values[i]), Generation.KeyOf(in b.Src, in b.D, theirs[i]));
        }
    }

    [Fact]
    public void Optional_sync_convention_excludes_generated_content()
    {
        World w = Build();
        NodeId[] made = null;
        Run(ref w, ref made, "3");

        Assert.True(w.Src.Nodes[made[0].Index].IsGenerated);
        Assert.False(Generation.IsSynced(in w.Src, made[0]));

        // The generator itself IS authored — it is the recipe. So are its inputs.
        Assert.True(Generation.IsSynced(in w.Src, w.Generator));
        Assert.True(Generation.IsSynced(in w.Src, Size(ref w, "3")));
    }

    [Fact]
    public void A_materialized_node_stops_regenerating_and_keeps_its_provenance()
    {
        World w = Build();
        NodeId[] first = null;
        Run(ref w, ref first, "3", "4");
        NodeId four = first[1];

        Generation.Materialize(ref w.Src, four);

        Assert.False(w.Src.Nodes[four.Index].IsGenerated);
        Assert.True(Generation.IsSynced(in w.Src, four));

        // The link is visible rather than lost — both halves of it.
        Assert.Equal(w.Generator, Staleness.ProducedBy(in w.Src, in w.D, four));
        Assert.Equal(Size(ref w, "4"), Generation.KeyOf(in w.Src, in w.D, four));

        // And dropping its key no longer deletes it — it is authored content now, which is
        // not a generator's to remove.
        NodeId[] second = null;
        Run(ref w, ref second, "3");
        Assert.True(w.Src.IsLive(four));
    }

    [Fact]
    public void A_reader_without_an_evaluator_sees_ordinary_nodes_with_provenance()
    {
        World w = Build();
        NodeId[] made = null;
        Run(ref w, ref made, "3", "4");

        for (int i = 0; i < 2; i++)
        {
            // Ordinary in every respect: live, contained, walkable, with a payload.
            Assert.True(w.Src.IsLive(made[i]));
            Assert.Equal(w.Root, SourceWalk.Parent(in w.Src, made[i]));
            Assert.NotEqual(0, SourceWalk.Payload(in w.Src, made[i]).Length);

            // And they say where they came from, which is how a reader knows not to edit.
            Assert.Equal(w.Generator, Staleness.ProducedBy(in w.Src, in w.D, made[i]));
            Assert.True(w.Src.Nodes[made[i].Index].IsGenerated);
        }
    }

    [Fact]
    public void Regenerating_unchanged_input_is_a_no_op()
    {
        World w = Build();
        NodeId[] first = null;
        Run(ref w, ref first, "3", "4", "5");
        long slotsBefore = w.Src.NodeCount;

        NodeId[] second = null;
        Run(ref w, ref second, "3", "4", "5");

        Assert.Equal(slotsBefore, w.Src.NodeCount);   // nothing allocated
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }
}
