using System.Text;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Kinds.Tests;

public class Spec_Property
{
    [Fact]
    public void Shared_vocabularies_do_not_choose_a_palette()
    {
        KindTable table = KindBootstrap.CreateTable(out _);
        var properties = PropertyKinds.Install(ref table);
        var relate = Supersource.Relate.RelateLibrary.Install(ref table);
        var derive = Supersource.Derive.DeriveKinds.Install(ref table);
        foreach (var kind in new[] { properties.Tag, properties.Property, relate.Span,
            relate.Tag, relate.Embed, relate.Original, derive.Derivation, derive.BuildTarget })
            Assert.Equal(0u, KindTable.SchemaOf(in table, kind).DisplayColor);
    }

    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindBootstrap.Ids Boot;
        public PropertyKinds.Ids Prop;
        public NodeId Root;
        public NodeId Vocabulary;
        public KindId Thing;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);
        w.Prop = PropertyKinds.Install(ref w.Table);

        SourceId library = SourceId.Mint();
        w.Thing = KindTable.Intern(ref w.Table, new QualifiedName(library, "thing"));
        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Thing);
        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("project"));

        w.Vocabulary = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.SetPayload(ref w.Src, w.Vocabulary, Encoding.UTF8.GetBytes("words"));

        return w;
    }

    static NodeId Thing(ref World w, string name)
    {
        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.SetPayload(ref w.Src, node, Encoding.UTF8.GetBytes(name));
        return node;
    }

    [Fact]
    public void A_tag_is_a_property_with_no_value()
    {
        World w = Build();

        NodeId deprecated = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "deprecated");
        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, deprecated, PropertyValue.None);

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, deprecated, out PropertyValue said));
        Assert.True(said.IsBare);
    }

    [Fact]
    public void A_property_carries_a_value_a_stranger_can_read()
    {
        World w = Build();

        NodeId max = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "max");
        NodeId radix = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "radix");
        NodeId hidden = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "hidden");
        NodeId unit = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "unit");

        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, max, PropertyValue.Of(1d));
        PropertyKinds.Set(ref w.Src, in w.Prop, speed, radix, PropertyValue.Of(16L));
        PropertyKinds.Set(ref w.Src, in w.Prop, speed, hidden, PropertyValue.Of(true));
        PropertyKinds.Set(ref w.Src, in w.Prop, speed, unit, PropertyValue.Of("m/s"));

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, max, out PropertyValue real));
        Assert.Equal(PayloadFormat.Float64, real.Format);
        Assert.Equal(1d, real.Real);

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, radix, out PropertyValue whole));
        Assert.Equal(16L, whole.Whole);

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, hidden, out PropertyValue truth));
        Assert.True(truth.Truth);

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, unit, out PropertyValue text));
        Assert.Equal("m/s", text.Text);
    }

    [Fact]
    public void A_reader_that_does_not_know_the_word_still_reads_the_value()
    {
        World w = Build();

        NodeId strange = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "kerning-bias");
        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, strange, PropertyValue.Of(-0.5d));

        // Reached the way a stranger reaches it: walk the children, find the kind, decode.
        NodeId found = PropertyKinds.Find(in w.Src, in w.Prop, speed, strange);

        Assert.True(found.IsSome);
        Assert.True(PropertyKinds.TryDecode(SourceWalk.Payload(in w.Src, found), out PropertyValue said));
        Assert.Equal(-0.5d, said.Real);
        Assert.Equal("kerning-bias", PropertyKinds.WordOf(in w.Src, strange));
    }

    [Fact]
    public void Saying_it_again_replaces_rather_than_adds()
    {
        World w = Build();

        NodeId max = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "max");
        NodeId speed = Thing(ref w, "speed");

        NodeId first = PropertyKinds.Set(ref w.Src, in w.Prop, speed, max, PropertyValue.Of(1d));
        NodeId again = PropertyKinds.Set(ref w.Src, in w.Prop, speed, max, PropertyValue.Of(360d));

        Assert.Equal(first.Index, again.Index);
        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, max, out PropertyValue said));
        Assert.Equal(360d, said.Real);
    }

    [Fact]
    public void Two_vocabularies_may_share_a_spelling()
    {
        World w = Build();

        NodeId theirs = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.SetPayload(ref w.Src, theirs, Encoding.UTF8.GetBytes("their words"));

        NodeId ours = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "min");
        NodeId other = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, theirs, "min");

        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, ours, PropertyValue.Of(0d));
        PropertyKinds.Set(ref w.Src, in w.Prop, speed, other, PropertyValue.Of(-1d));

        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, ours, out PropertyValue mine));
        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, other, out PropertyValue yours));

        Assert.Equal(0d, mine.Real);
        Assert.Equal(-1d, yours.Real);
    }

    [Fact]
    public void A_word_that_went_away_leaves_a_dangling_edge_rather_than_silence()
    {
        World w = Build();

        NodeId max = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "max");
        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, max, PropertyValue.Of(1d));
        SourceBuild.Delete(ref w.Src, max);

        NodeId[] dangling = new NodeId[8];
        int count = SourceWalk.Dangling(in w.Src, ref dangling);

        Assert.True(count > 0);
        Assert.False(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, max, out _));
    }

    [Fact]
    public void Clearing_a_property_removes_it()
    {
        World w = Build();

        NodeId hidden = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "hidden");
        NodeId speed = Thing(ref w, "speed");

        PropertyKinds.Set(ref w.Src, in w.Prop, speed, hidden, PropertyValue.Of(true));

        Assert.True(PropertyKinds.Clear(ref w.Src, in w.Prop, speed, hidden));
        Assert.False(PropertyKinds.Clear(ref w.Src, in w.Prop, speed, hidden));
        Assert.False(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, hidden, out _));
    }

    [Fact]
    public void A_format_this_build_does_not_know_reads_as_the_word_alone()
    {
        World w = Build();

        NodeId strange = PropertyKinds.DeclareTag(ref w.Src, in w.Prop, w.Vocabulary, "from-the-future");
        NodeId speed = Thing(ref w, "speed");

        NodeId property = PropertyKinds.Set(ref w.Src, in w.Prop, speed, strange, PropertyValue.None);
        SourceBuild.SetPayload(ref w.Src, property, new byte[] { 200, 1, 2, 3 });

        Assert.False(PropertyKinds.TryDecode(SourceWalk.Payload(in w.Src, property), out PropertyValue said));
        Assert.True(said.IsBare);
        Assert.True(PropertyKinds.TryRead(in w.Src, in w.Prop, speed, strange, out _));
    }
}
