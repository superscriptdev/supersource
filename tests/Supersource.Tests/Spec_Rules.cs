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
        public KindBootstrap.Ids Boot;
        public KindId Thing, Note;
        public RelationId About;
        public NodeId Root;
    }

    static World Build()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);

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

        RuleSet strict = Set(StockRule.NoHoles, Severity.Block);
        int strictCount = Run(in strict, in w, ref found);
        Assert.True(RuleSet.Blocks(found, strictCount));

        RuleSet relaxed = Set(StockRule.NoHoles, Severity.Warn);
        found = null;
        int relaxedCount = Run(in relaxed, in w, ref found);
        Assert.Equal(1, relaxedCount);
        Assert.False(RuleSet.Blocks(found, relaxedCount));
    }

    [Fact]
    public void Requiring_a_text_label_is_a_project_rule_and_the_document_is_valid_without_one()
    {
        World w = Build();
        NodeId unlabelled = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);

        // Structurally fine.
        Assert.True(w.Src.IsLive(unlabelled));

        RuleSet rules = Set(StockRule.EveryDeclarationHasATextLabel, Severity.Block);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(unlabelled, found[0].Node);
    }

    [Fact]
    public void A_declaration_that_holds_a_record_is_not_missing_a_text_label()
    {
        World w = Build();

        KindId definition = KindTable.Intern(
            ref w.Table, new QualifiedName(SourceId.Mint(), "style-def"));
        KindTable.DefineSchema(ref w.Table, definition,
            StructuralFact.Declaration, PayloadFormat.Record, DisplayShape.Leaf, 0, default);

        NodeId styled = SourceBuild.AddChild(ref w.Src, w.Root, definition);
        SourceBuild.SetPayload(ref w.Src, styled, new byte[] { 1, 0, 0 });

        RuleSet rules = Set(StockRule.EveryDeclarationHasATextLabel, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        for (int i = 0; i < count; i++)
        {
            Assert.NotEqual(styled, found[i].Node);
        }
    }

    [Fact]
    public void An_empty_record_declaration_is_the_other_rules_business()
    {
        World w = Build();

        KindId definition = KindTable.Intern(
            ref w.Table, new QualifiedName(SourceId.Mint(), "style-def"));
        KindTable.DefineSchema(ref w.Table, definition,
            StructuralFact.Declaration, PayloadFormat.Record, DisplayShape.Leaf, 0, default);

        NodeId styled = SourceBuild.AddChild(ref w.Src, w.Root, definition);

        RuleSet rules = Set(StockRule.EveryDeclarationHasATextLabel, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        for (int i = 0; i < count; i++)
        {
            Assert.NotEqual(styled, found[i].Node);
        }
    }

    [Fact]
    public void Rules_read_structural_facts_and_so_work_over_a_library_they_never_linked()
    {
        World w = Build();

        QualifiedName alien = new QualifiedName(SourceId.Mint(), "widget");
        KindId widget = KindTable.Intern(ref w.Table, alien);
        KindTable.DefineSchema(ref w.Table, widget, StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);

        SourceBuild.AddChild(ref w.Src, w.Root, widget);   // no label

        RuleSet rules = Set(StockRule.EveryDeclarationHasATextLabel, Severity.Block);
        RuleViolation[] found = null;
        Assert.Equal(1, Run(in rules, in w, ref found));
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

        NodeId unlabelled = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        SourceBuild.AddUnboundRef(ref w.Src, unlabelled, w.About, Encoding.UTF8.GetBytes("gone"));

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.EveryDeclarationHasATextLabel, Severity.Warn);
        RuleSet.Add(ref rules, StockRule.NoHoles, Severity.Block);

        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(2, count);
        Assert.Contains(found, v => v.Rule == RuleSet.NameOf(StockRule.EveryDeclarationHasATextLabel)
            && v.Severity == Severity.Warn);
        Assert.Contains(found, v => v.Rule == RuleSet.NameOf(StockRule.NoHoles)
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
        RuleSet.Add(ref rules, StockRule.EveryDeclarationHasATextLabel, Severity.Block);
        RuleSet.Add(ref rules, StockRule.NoHoles, Severity.Block);
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
            PayloadFormat.None, DisplayShape.Block, 0, default);

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
            PayloadFormat.None, DisplayShape.Block, 0, default);

        SourceBuild.AddChild(ref w.Src, w.Root, quiet);
        SourceBuild.AddChild(ref w.Src, w.Root, w.Note);

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.PayloadMatchesTheSchema, Severity.Warn);

        RuleViolation[] found = null;
        Assert.Equal(0, Run(in rules, in w, ref found));
    }

    [Fact]
    public void A_declaration_nothing_uses_is_reported()
    {
        World w = Build();
        KindId ghost = KindTable.Intern(ref w.Table, new QualifiedName(Library, "ghost"));
        KindTable.DefineSchema(ref w.Table, ghost, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.SchemaDeclaresOnlyWhatIsUsed, Severity.Warn);

        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Contains(found.AsSpan(0, count).ToArray(), v => v.Message.Contains("ghost"));
    }

    [Fact]
    public void A_kind_only_a_tombstone_is_is_not_surplus()
    {
        World w = Build();

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.SchemaDeclaresOnlyWhatIsUsed, Severity.Warn);

        RuleViolation[] baseline = null;
        int before = Run(in rules, in w, ref baseline);
        Assert.Contains(baseline.AsSpan(0, before).ToArray(), v => v.Message.Contains("note"));

        // `note` is used by exactly one node, and then by none that is alive.
        NodeId note = SourceBuild.AddChild(ref w.Src, w.Root, w.Note);
        Text(ref w.Src, note, "gone");
        SourceBuild.Delete(ref w.Src, note);

        RuleViolation[] found = null;
        int after = Run(in rules, in w, ref found);
        Assert.DoesNotContain(found.AsSpan(0, after).ToArray(), v => v.Message.Contains("note"));
    }

    [Fact]
    public void A_finding_about_an_edge_names_the_edge()
    {
        World w = Build();
        NodeId thing = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);
        Text(ref w.Src, thing, "incomplete");
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("first"));
        SourceBuild.AddUnboundRef(ref w.Src, thing, w.About, Encoding.UTF8.GetBytes("second"));

        RuleSet rules = Set(StockRule.NoHoles, Severity.Warn);
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
    public void A_finding_about_a_kind_names_the_kind()
    {
        World w = Build();

        RuleSet rules = Set(StockRule.SchemaDeclaresOnlyWhatIsUsed, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        bool sawKind = false;
        for (int i = 0; i < count; i++)
        {
            if (found[i].Subject != SubjectKind.Kind || found[i].Kind != w.Note)
            {
                continue;
            }
            sawKind = true;
            Assert.Equal("note", RuleSet.Describe(in found[i], in w.Table));
        }

        Assert.True(sawKind, "no finding named the surplus kind");
    }

    [Fact]
    public void A_finding_about_a_relation_names_the_relation()
    {
        World w = Build();

        RuleSet rules = Set(StockRule.SchemaDeclaresOnlyWhatIsUsed, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        bool sawRelation = false;
        for (int i = 0; i < count; i++)
        {
            if (found[i].Subject != SubjectKind.Relation || found[i].Relation != w.About)
            {
                continue;
            }
            sawRelation = true;
            Assert.Equal("about", RuleSet.Describe(in found[i], in w.Table));
        }

        Assert.True(sawRelation, "no finding named the surplus relation");
    }

    [Fact]
    public void A_finding_about_a_node_still_names_the_node()
    {
        World w = Build();
        NodeId unlabelled = SourceBuild.AddChild(ref w.Src, w.Root, w.Thing);

        RuleSet rules = Set(StockRule.EveryDeclarationHasATextLabel, Severity.Warn);
        RuleViolation[] found = null;
        int count = Run(in rules, in w, ref found);

        Assert.Equal(1, count);
        Assert.Equal(SubjectKind.Node, found[0].Subject);
        Assert.Equal(unlabelled, found[0].Node);
        Assert.Equal(unlabelled.ToString(), RuleSet.Describe(in found[0], in w.Table));
    }

    static World Formats(PayloadFormat format, PayloadStride stride, byte[] bytes)
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);

        w.Thing = KindTable.Intern(ref w.Table, ThingName);
        KindTable.DefineSchema(ref w.Table, w.Thing, StructuralFact.None,
            format, DisplayShape.Block, 0, default, stride);

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
    [InlineData(PayloadFormat.Int64, new byte[] { 1, 2, 3 })]
    [InlineData(PayloadFormat.Float64, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData(PayloadFormat.Bool, new byte[] { 0, 0 })]
    [InlineData(PayloadFormat.Bool, new byte[] { 7 })]
    [InlineData(PayloadFormat.Utf8, new byte[] { 0xC3, 0x28 })]
    public void A_payload_that_does_not_match_its_declared_format_is_reported(
        PayloadFormat format, byte[] bytes)
    {
        World w = Formats(format, default, bytes);
        Assert.Equal(1, Conformance(ref w));
    }

    [Theory]
    [InlineData(PayloadFormat.Int64, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(PayloadFormat.Float64, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(PayloadFormat.Bool, new byte[] { 1 })]
    [InlineData(PayloadFormat.Utf8, new byte[] { 0xE2, 0x9C, 0x93 })]
    [InlineData(PayloadFormat.Opaque, new byte[] { 0xFF, 0x00, 0xC3 })]
    public void A_payload_that_matches_is_not(PayloadFormat format, byte[] bytes)
    {
        World w = Formats(format, default, bytes);
        Assert.Equal(0, Conformance(ref w));
    }

    [Theory]
    [InlineData(PayloadFormat.Int64)]
    [InlineData(PayloadFormat.Bool)]
    [InlineData(PayloadFormat.Utf8)]
    [InlineData(PayloadFormat.Record)]
    public void An_empty_payload_is_ordinary_whatever_the_format(PayloadFormat format)
    {
        World w = Formats(format, default, Array.Empty<byte>());
        Assert.Equal(0, Conformance(ref w));
    }

    [Fact]
    public void Bytes_under_a_kind_declaring_no_payload_are_still_reported()
    {
        World w = Formats(PayloadFormat.None, default, new byte[] { 1 });
        Assert.Equal(1, Conformance(ref w));

        World empty = Formats(PayloadFormat.None, default, Array.Empty<byte>());
        Assert.Equal(0, Conformance(ref empty));
    }

    [Fact]
    public void A_record_that_declares_a_stride_must_hold_whole_elements()
    {
        PayloadStride stride = PayloadStride.Of(2, 4);

        World whole = Formats(PayloadFormat.Record, stride, new byte[2 + 4 + 4]);
        Assert.Equal(0, Conformance(ref whole));

        World partial = Formats(PayloadFormat.Record, stride, new byte[2 + 4 + 1]);
        Assert.Equal(1, Conformance(ref partial));

        World shorterThanItsHeader = Formats(PayloadFormat.Record, stride, new byte[1]);
        Assert.Equal(1, Conformance(ref shorterThanItsHeader));
    }

    [Fact]
    public void A_record_with_no_declared_stride_is_not_second_guessed()
    {
        World w = Formats(PayloadFormat.Record, default, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
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
