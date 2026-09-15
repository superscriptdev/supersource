using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Compare;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Rules;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Kinds.Tests;

public class Spec_EdgeData
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName RasterName = new QualifiedName(Library, "raster");
    static readonly QualifiedName NoteName = new QualifiedName(Library, "note");
    static readonly QualifiedName MarksName = new QualifiedName(Library, "marks");

    struct World
    {
        public SourceFile File;
        public KindId Raster, Note;
        public RelationId Marks;
        public NodeId Root, Image;
    }

    static World Build()
    {
        World w = default;
        w.File = SourceFile.Create(32);

        w.Raster = KindTable.Intern(ref w.File.Table, RasterName);
        w.Note = KindTable.Intern(ref w.File.Table, NoteName);
        w.Marks = KindTable.InternRelation(ref w.File.Table, MarksName);

        // The image is one node whose payload is an array of four-byte pixels...
        KindTable.DefineSchema(ref w.File.Table, w.Raster, StructuralFact.None,
            PayloadFormat.Record, DisplayShape.Leaf, 0, default, PayloadStride.Of(8, 4));
        KindTable.DefineSchema(ref w.File.Table, w.Note, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.Marks });

        // ...and an edge that marks one says which, as an element index.
        KindTable.DefineRelation(ref w.File.Table, w.Marks, PayloadFormat.Int64);

        SourceBuild.MintIdentity(ref w.File.Src);
        w.Root = SourceBuild.AddRoot(ref w.File.Src, w.Note);
        SourceBuild.SetPayload(ref w.File.Src, w.Root, Encoding.UTF8.GetBytes("document"));

        w.Image = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Raster);
        SourceBuild.SetPayload(ref w.File.Src, w.Image, new byte[8 + 4 * 1000]);
        return w;
    }

    static byte[] Index(long element)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, element);
        return bytes;
    }

    static long IndexOf(ReadOnlySpan<byte> bytes)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    [Fact]
    public void A_reference_can_name_part_of_a_node_without_a_node_per_part()
    {
        World w = Build();

        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.SetPayload(ref w.File.Src, note, Encoding.UTF8.GetBytes("this pixel is wrong"));
        SourceBuild.AddRef(ref w.File.Src, note, w.Marks, w.Image, Index(407));

        Assert.Equal(3, w.File.Src.NodeCount);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in w.File.Src, note);
        Assert.Equal(w.Image, refs[0].Target);
        Assert.Equal(407, IndexOf(SourceWalk.EdgeBytes(in w.File.Src, in refs[0])));
    }

    [Fact]
    public void A_stranger_resolves_the_qualifier_to_a_byte_range()
    {
        World w = Build();
        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, note, w.Marks, w.Image, Index(407));
        KindSchemaCodec.Write(ref w.File.Src, in w.File.Table, in w.File.Boot, w.Root);

        SourceFile back = SourceRead.FromBytes(SourceWrite.ToBytes(in w.File));

        RelationId marks = KindTable.LookupRelation(in back.Table, MarksName);
        Assert.Equal(PayloadFormat.Int64, KindTable.RelationSchemaOf(in back.Table, marks).Payload);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, note);
        long element = IndexOf(SourceWalk.EdgeBytes(in back.Src, in refs[0]));

        PayloadStride stride = KindTable.SchemaOf(
            in back.Table, back.Src.Nodes[refs[0].Target.Index].Kind).Stride;

        Assert.Equal(8 + 407 * 4, stride.Header + element * stride.Unit);
        Assert.Equal(1000, stride.ElementCount(SourceWalk.Payload(in back.Src, w.Image).Length));
    }

    [Fact]
    public void Edge_data_survives_a_save_and_a_load()
    {
        World w = Build();
        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, note, w.Marks, w.Image, Index(12));
        SourceBuild.AddUnboundRef(ref w.File.Src, note, w.Marks, Encoding.UTF8.GetBytes("a hole"));
        KindSchemaCodec.Write(ref w.File.Src, in w.File.Table, in w.File.Boot, w.Root);

        byte[] bytes = SourceWrite.ToBytes(in w.File);
        SourceFile back = SourceRead.FromBytes(bytes);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, note);
        Assert.Equal(2, refs.Length);

        Assert.True(refs[0].IsBound);
        Assert.Equal(12, IndexOf(SourceWalk.EdgeBytes(in back.Src, in refs[0])));

        // A hole's label is the same run, and still reads as a label.
        Assert.True(refs[1].IsUnbound);
        Assert.Equal("a hole", Encoding.UTF8.GetString(SourceWalk.Label(in back.Src, in refs[1])));

        Assert.Equal(bytes, SourceWrite.ToBytes(in back));
    }

    [Fact]
    public void Compaction_carries_edge_data_with_its_edge()
    {
        World w = Build();
        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, note, w.Marks, w.Image, Index(99));

        Source tight = SourceCompact.Compact(in w.File.Src);
        ReadOnlySpan<Ref> refs = SourceWalk.References(in tight, note);
        Assert.Equal(99, IndexOf(SourceWalk.EdgeBytes(in tight, in refs[0])));
    }

    [Fact]
    public void A_changed_qualifier_is_a_change()
    {
        World w = Build();
        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, note, w.Marks, w.Image, Index(1));

        Source before = SourceCompact.Compact(in w.File.Src);

        Source after = SourceCompact.Compact(in before);
        SourceBuild.RemoveRef(ref after, note, 0);
        SourceBuild.AddRef(ref after, note, w.Marks, w.Image, Index(2));

        Operation[] ops = null;
        int count = StructuralDiff.Between(in before, in after, in w.File.Table, ref ops);

        Assert.True(count > 0, "moving the qualifier produced no operations");

        Source replayed = SourceCompact.Compact(in before);
        for (int i = 0; i < count; i++)
        {
            Operation op = ops[i];
            OpApply.Apply(ref replayed, ref op);
        }

        ReadOnlySpan<Ref> refs = SourceWalk.References(in replayed, note);
        Assert.Equal(2, IndexOf(SourceWalk.EdgeBytes(in replayed, in refs[0])));
    }

    [Fact]
    public void An_edge_that_disagrees_with_its_relation_is_reported()
    {
        World w = Build();
        NodeId good = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, good, w.Marks, w.Image, Index(1));

        NodeId bad = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, bad, w.Marks, w.Image, new byte[] { 1, 2, 3 });

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.EdgeDataMatchesTheRelation, Severity.Warn);

        RuleViolation[] found = null;

        int count = RuleSet.Run(in rules, in w.File.Src, in w.File.Table, ref found);

        Assert.Equal(1, count);
        Assert.Equal(SubjectKind.Reference, found[0].Subject);
        Assert.Equal(bad, found[0].Node);
        Assert.Equal(0, found[0].RefIndex);
        Assert.Contains("marks", found[0].Message);
    }

    [Fact]
    public void A_hole_is_not_asked_to_agree_with_anything()
    {
        World w = Build();
        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddUnboundRef(ref w.File.Src, note, w.Marks, Encoding.UTF8.GetBytes("the wrong pixel"));

        RuleSet rules = RuleSet.Create();
        RuleSet.Add(ref rules, StockRule.EdgeDataMatchesTheRelation, Severity.Warn);

        RuleViolation[] found = null;

        Assert.Equal(0, RuleSet.Run(in rules, in w.File.Src, in w.File.Table, ref found));
    }

    [Fact]
    public void A_relation_that_carries_nothing_is_declared_as_it_always_was()
    {
        World w = Build();
        RelationId plain = KindTable.InternRelation(ref w.File.Table, new QualifiedName(Library, "plain"));
        KindTable.DefineSchema(ref w.File.Table, w.Note, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.Marks, plain });

        NodeId note = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Note);
        SourceBuild.AddRef(ref w.File.Src, note, plain, w.Image);
        KindSchemaCodec.Write(ref w.File.Src, in w.File.Table, in w.File.Boot, w.Root);

        SourceFile back = SourceRead.FromBytes(SourceWrite.ToBytes(in w.File));

        RelationId here = KindTable.LookupRelation(in back.Table, new QualifiedName(Library, "plain"));
        Assert.Equal(PayloadFormat.None, KindTable.RelationSchemaOf(in back.Table, here).Payload);

        RelationId marks = KindTable.LookupRelation(in back.Table, MarksName);
        Assert.Equal(PayloadFormat.Int64, KindTable.RelationSchemaOf(in back.Table, marks).Payload);
    }
}
