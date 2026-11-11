using System;
using System.Text;
using Supersource.Kinds;
using Supersource.Rules;
using Xunit;

namespace Supersource.Rules.Tests;

public class Spec_Rules
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName ThingName = new QualifiedName(Library, "thing");
    static readonly QualifiedName NoteName = new QualifiedName(Library, "note");
    static readonly QualifiedName AboutName = new QualifiedName(Library, "about");

    struct World
    {
        public Source Src;
        public KindTable Table;
        public TestKinds.Ids Boot;
        public KindId Thing, Note;
        public RelationId About;
        public NodeId Root;
    }

    static World Build()
    {
        World w = default;
        w.Table = TestKinds.CreateTable(out w.Boot);

        w.Thing = KindTable.Intern(ref w.Table, ThingName);
        w.Note = KindTable.Intern(ref w.Table, NoteName);
        w.About = KindTable.InternRelation(ref w.Table, AboutName);

        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.Annotation,
            PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.Table, w.Note, StructuralFact.None,
            PayloadFormat.Text, new RelationId[] { w.About });

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

    static RuleSet Set(StockRule which, Severity severity)
    {
        RuleSet set = RuleSet.Create();
        RuleSet.Add(ref set, which, severity);
        return set;
    }

    static int Run(in RuleSet set, in World w, ref RuleViolation[] found)
    {

        return RuleSet.Run(in set, in w.Src, in w.Table, ref found);
    }

    [Fact]
    public void A_document_the_model_accepts_may_still_violate_a_project_rule()
    {
        World w = Build();

        NodeId subject = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, subject, "subject");
        NodeId note = SourceBuild.AddChild(ref w.Src, w.Root, w.Note);
        Text(ref w.Src, note, "about the subject");
        SourceBuild.AddRef(ref w.Src, note, w.About, subject);

        SourceBuild.Delete(ref w.Src, subject);

        // A dangling reference is structurally valid.
        Assert.True(w.Src.IsLive(note));

        // The project is not.
        RuleSet rules = Set(StockRule.NoDanglingReferences, Severity.Block);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(note, found[0].Node);
        Assert.Equal(RuleSet.NameOf(StockRule.NoDanglingReferences), found[0].Rule);
        Assert.True(RuleSet.Blocks(found, count));
    }

    [Fact]
    public void The_same_finding_can_warn_in_one_project_and_block_in_another()
    {
        World w = Build();
        NodeId thing = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("missing"));
        Text(ref w.Src, thing, "incomplete");

        RuleViolation[] found = null;

        RuleSet strict = Set(StockRule.NoUnboundReferences, Severity.Block);
        int strictCount = Run(in strict, in w, ref found);
        Assert.True(RuleSet.Blocks(found, strictCount));

        RuleSet relaxed = Set(StockRule.NoUnboundReferences, Severity.Warn);
        found = null;
        int relaxedCount = Run(in relaxed, in w, ref found);
        Assert.Equal(1, relaxedCount);
        Assert.False(RuleSet.Blocks(found, relaxedCount));
    }

    [Fact]
    public void An_uninterpretable_kind_can_be_made_a_violation()
    {
        World w = Build();

        KindId ghost = KindTable.Intern(ref w.Table,
            new QualifiedName(SourceId.Mint(), "from-a-library-nobody-shipped"));
        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, ghost);

        RuleSet rules = Set(StockRule.EveryKindIsInterpretable, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(node, found[0].Node);
        Assert.False(RuleSet.Blocks(found, count));
    }

    [Fact]
    public void Several_rules_run_together_and_each_violation_names_its_rule()
    {
        World w = Build();

        NodeId thing = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("gone"));
        KindId ghost = KindTable.Intern(ref w.Table, new QualifiedName(SourceId.Mint(), "unshipped"));
        SourceBuild.AddChild(ref w.Src, w.Root, ghost);

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.EveryKindIsInterpretable, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.NoUnboundReferences, Severity.Block);

        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(2, count);
        Assert.Contains(found, v => v.Rule == RuleSet.NameOf(StockRule.EveryKindIsInterpretable)
            && v.Severity == Severity.Warn);
        Assert.Contains(found, v => v.Rule == RuleSet.NameOf(StockRule.NoUnboundReferences)
            && v.Severity == Severity.Block);
        Assert.True(RuleSet.Blocks(found, count));
    }

    [Fact]
    public void A_clean_project_passes_every_rule()
    {
        World w = Build();
        NodeId thing = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, thing, "fine");

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.NoUnboundReferences, Severity.Block);
        RuleSet.Add(ref rules, StockRule.NoDanglingReferences, Severity.Block);
        RuleSet.Add(ref rules, StockRule.EveryKindIsInterpretable, Severity.Block);

        RuleViolation[] found = null;
        Assert.Equal(0, Run(in rules, in w, ref found));
    }

    [Fact]
    public void A_payload_the_schema_denies_is_a_violation()
    {
        World w = Build();
        KindId quiet = KindTable.Intern(ref w.Table, new QualifiedName(Library, "quiet"));
        KindTable.DefineSchema(ref w.Table, quiet, StructuralFact.None,
            PayloadFormat.None, default);

        NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, quiet);
        Text(ref w.Src, node, "but it speaks");

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.PayloadMatchesTheSchema, Severity.Warn);

        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(node, found[0].Node);
    }

    [Fact]
    public void A_declared_payload_that_is_empty_is_nobody_s_business()
    {
        World w = Build();
        KindId quiet = KindTable.Intern(ref w.Table, new QualifiedName(Library, "quiet"));
        KindTable.DefineSchema(ref w.Table, quiet, StructuralFact.None,
            PayloadFormat.None, default);

        SourceBuild.AddChild(ref w.Src, w.Root, quiet);
        SourceBuild.AddChild(ref w.Src, w.Root, w.Note);

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.PayloadMatchesTheSchema, Severity.Warn);

        RuleViolation[] found = null;
        Assert.Equal(0, Run(in rules, in w, ref found));
    }

    [Fact]
    public void A_finding_about_an_edge_names_the_edge()
    {
        World w = Build();
        NodeId thing = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, thing, "incomplete");
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("first"));
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("second"));

        RuleSet rules = Set(StockRule.NoUnboundReferences, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(2, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(SubjectKind.Reference, found[i].Subject);
            Assert.Equal(thing, found[i].Node);
        }

        Assert.NotEqual(found[0].RefIndex, found[1].RefIndex);

        // The index is an address into the node's own run, so it resolves back to the edge.
        Ref edge = SourceWalk.RefAt(in w.Src, found[0].Node, found[0].RefIndex);
        Assert.True(edge.IsUnbound);
    }

    [Fact]
    public void A_finding_about_a_node_still_names_the_node()
    {
        World w = Build();
        KindId ghost = KindTable.Intern(ref w.Table, new QualifiedName(SourceId.Mint(), "unshipped"));
        NodeId uninterpretable = SourceBuild.AddChild(ref w.Src, w.Root, ghost);

        RuleSet rules = Set(StockRule.EveryKindIsInterpretable, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(SubjectKind.Node, found[0].Subject);
        Assert.Equal(uninterpretable, found[0].Node);
        Assert.Equal(uninterpretable.ToString(), RuleSet.Describe(in found[0], in w.Table));
    }

    /// <summary>A format spelled for an attribute: `none`, `opaque`, `text`, or `one` with a scalar.</summary>
    static PayloadFormat Spelled(string format)
    {
        switch (format)
        {
            case "none": return PayloadFormat.None;
            case "opaque": return PayloadFormat.Opaque;
            case "text": return PayloadFormat.Text;
            default:
                Assert.True(format.StartsWith("one ", StringComparison.Ordinal), format);
                Assert.True(PayloadScalars.TryParse(format.Substring(4), out PayloadScalar scalar), format);
                return PayloadFormat.One(scalar);
        }
    }

    static World Formats(PayloadFormat format, byte[] bytes)
    {
        World w = default;
        w.Table = TestKinds.CreateTable(out w.Boot);

        w.Thing = KindTable.Intern(ref w.Table, ThingName);
        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.None, format, default);

        w.Src = Source.Create(16);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Thing);
        SourceBuild.SetPayload(ref w.Src, w.Root, bytes);
        return w;
    }

    static int Conformance(ref World w)
    {
        RuleSet rules = Set(StockRule.PayloadMatchesTheSchema, Severity.Warn);
        RuleViolation[] found = null;
        return Run(in rules, in w, ref found);
    }

    [Theory]
    [InlineData("one i64", new byte[] { 1, 2, 3 })]
    [InlineData("one f64", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData("one bool", new byte[] { 0, 0 })]
    [InlineData("one bool", new byte[] { 7 })]
    [InlineData("text", new byte[] { 0xC3, 0x28 })]
    public void A_payload_that_does_not_match_its_declared_format_is_reported(string format, byte[] bytes)
    {
        World w = Formats(Spelled(format), bytes);
        Assert.Equal(1, Conformance(ref w));
    }

    [Theory]
    [InlineData("one i64", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData("one f64", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData("one bool", new byte[] { 1 })]
    [InlineData("one u16", new byte[] { 1, 2 })]
    [InlineData("text", new byte[] { 0xE2, 0x9C, 0x93 })]
    [InlineData("opaque", new byte[] { 0xFF, 0x00, 0xC3 })]
    public void A_payload_that_matches_is_not(string format, byte[] bytes)
    {
        World w = Formats(Spelled(format), bytes);
        Assert.Equal(0, Conformance(ref w));
    }

    [Theory]
    [InlineData("one i64")]
    [InlineData("one bool")]
    [InlineData("text")]
    [InlineData("opaque")]
    public void An_empty_payload_is_ordinary_whatever_the_format(string format)
    {
        World w = Formats(Spelled(format), Array.Empty<byte>());
        Assert.Equal(0, Conformance(ref w));
    }

    [Fact]
    public void Bytes_under_a_kind_declaring_no_payload_are_still_reported()
    {
        World w = Formats(PayloadFormat.None, new byte[] { 1 });
        Assert.Equal(1, Conformance(ref w));

        World empty = Formats(PayloadFormat.None, Array.Empty<byte>());
        Assert.Equal(0, Conformance(ref empty));
    }

    [Fact]
    public void Many_elements_after_a_header_must_be_whole_elements()
    {
        PayloadFormat format = PayloadFormat.Many(PayloadElement.Sized(4), PayloadElement.Sized(2));

        World whole = Formats(format, new byte[2 + 4 + 4]);
        Assert.Equal(0, Conformance(ref whole));

        World partial = Formats(format, new byte[2 + 4 + 1]);
        Assert.Equal(1, Conformance(ref partial));

        World shorterThanItsHeader = Formats(format, new byte[1]);
        Assert.Equal(1, Conformance(ref shorterThanItsHeader));
    }

    [Fact]
    public void A_described_field_is_checked_in_every_element()
    {
        PayloadFormat format = PayloadFormat.Many(PayloadElement.Of(4,
            PayloadField.Of("count", PayloadScalar.U16, 0),
            PayloadField.Of("visible", PayloadScalar.Bool, 2)));

        World fine = Formats(format, new byte[] { 1, 0, 1, 0, 2, 0, 0, 0 });
        Assert.Equal(0, Conformance(ref fine));

        // The second element's `visible` is 7, which no boolean is.
        World wrong = Formats(format, new byte[] { 1, 0, 1, 0, 2, 0, 7, 0 });
        Assert.Equal(1, Conformance(ref wrong));
    }

    [Fact]
    public void An_opaque_payload_is_not_second_guessed()
    {
        World w = Formats(PayloadFormat.Opaque, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(0, Conformance(ref w));
    }

    [Fact]
    public void A_finding_with_nothing_narrower_to_blame_names_the_source()
    {
        World w = Build();

        RuleViolation[] found = new RuleViolation[4];
        int count = RuleSet.Report(ref found, 0, NodeId.None, "the source is wrong somehow");

        Assert.Equal(1, count);
        Assert.Equal(SubjectKind.Source, found[0].Subject);
        Assert.Equal("source", RuleSet.Describe(in found[0], in w.Table));
    }
}
