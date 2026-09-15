using System;
using System.Text;
using Supersource.Derive;
using Supersource.Kinds;
using Supersource.Rules;
using Xunit;

namespace Supersource.Rules.Tests;

public class Spec_DerivationRules
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName ThingName = new QualifiedName(Library, "thing");
    static readonly QualifiedName NoteName = new QualifiedName(Library, "note");
    static readonly QualifiedName AboutName = new QualifiedName(Library, "about");

    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindBootstrap.Ids Boot;
        public DeriveKinds.Ids D;
        public KindId Thing, Note;
        public RelationId About;
        public NodeId Root;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);
        w.D = DeriveKinds.Install(ref w.Table);

        w.Thing = KindTable.Intern(ref w.Table, ThingName);
        w.Note = KindTable.Intern(ref w.Table, NoteName);
        w.About = KindTable.InternRelation(ref w.Table, AboutName);

        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Note, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About });

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Thing);
        Text(ref w.Src, w.Root, "project");
        return w;
    }

    static void Text(ref Source src, NodeId id, string s)
    {
        SourceBuild.SetPayload(ref src, id, Encoding.UTF8.GetBytes(s));
    }

    [Fact]
    public void Re_derivability_is_enforceable_by_the_teams_that_need_it()
    {
        World w = Build();

        NodeId derivation = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
        // No transform edge: the recipe names something this source does not contain.

        RuleViolation[] found = null;
        int count = DerivationRules.FullyReDerivable(in w.Src, in w.D, ref found, severity: Severity.Block);

        Assert.Equal(1, count);
        Assert.Equal(derivation, found[0].Node);
        Assert.Equal("fully-re-derivable", found[0].Rule);
        Assert.Equal(Severity.Block, found[0].Severity);

        // Give it one, and the same project is satisfied.
        NodeId fn = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, fn, "tessellate");
        SourceBuild.AddRef(ref w.Src, derivation, w.D.Transform, fn);

        found = null;
        Assert.Equal(0, DerivationRules.FullyReDerivable(in w.Src, in w.D, ref found, severity: Severity.Block));
    }

    [Fact]
    public void A_generator_declaring_a_weak_tier_is_reported()
    {
        World w = Build();

        NodeId generator = SourceBuild.AddChild(ref w.Src, w.Root, w.D.Derivation);
        SourceBuild.SetPayload(ref w.Src, generator,
            DeriveKinds.EncodeTier(Tier.ContentAddressed));

        NodeId generated = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, generated, "variant-3mm");
        w.Src.Nodes[generated.Index].Flags |= NodeFlags.Generated;
        SourceBuild.AddRef(ref w.Src, generated, w.D.ProducedBy, generator);

        RuleViolation[] found = null;
        int count = DerivationRules.GeneratorsAreBitExact(in w.Src, in w.D, ref found, severity: Severity.Block);

        Assert.Equal(1, count);
        Assert.Contains("bit-exact", found[0].Message);
        Assert.Equal("generators-are-bit-exact", found[0].Rule);
        Assert.Equal(Severity.Block, found[0].Severity);

        // The same generator satisfies the rule after its tier is changed.
        SourceBuild.SetPayload(ref w.Src, generator, DeriveKinds.EncodeTier(Tier.BitExact));
        found = null;
        Assert.Equal(0, DerivationRules.GeneratorsAreBitExact(in w.Src, in w.D, ref found, severity: Severity.Block));
    }
}
