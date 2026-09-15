using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Relate;
using Xunit;

namespace Supersource.Relate.Tests;

public class Spec_Span
{
    static SpanRange R(int offset, int length, ushort edge)
    {
        SpanRange r;
        r.Offset = offset;
        r.Length = length;
        r.Edge = edge;
        return r;
    }

    [Fact]
    public void Ranges_survive_a_round_trip()
    {
        SpanRange[] written = { R(0, 5, 0), R(12, 3, 1), R(40, 0, SpanRange.NoEdge) };

        byte[] payload = SpanCodec.Write(written);

        SpanRange[] read = null;
        int count = SpanCodec.Read(payload, ref read);

        Assert.Equal(3, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(written[i].Offset, read[i].Offset);
            Assert.Equal(written[i].Length, read[i].Length);
            Assert.Equal(written[i].Edge, read[i].Edge);
        }
    }

    [Fact]
    public void An_empty_span_is_legal_and_costs_three_bytes()
    {
        byte[] payload = SpanCodec.Write(ReadOnlySpan<SpanRange>.Empty);

        Assert.Equal(3, payload.Length);
        Assert.Equal(0, SpanCodec.Count(payload));
    }

    [Fact]
    public void A_truncated_payload_reports_what_it_can_hold_rather_than_throwing()
    {
        byte[] full = SpanCodec.Write(new SpanRange[] { R(0, 5, 0), R(6, 5, 0), R(12, 5, 0) });
        byte[] cut = new byte[full.Length - 7];
        Array.Copy(full, cut, cut.Length);

        Assert.Equal(2, SpanCodec.Count(cut));

        SpanRange[] read = null;
        Assert.Equal(2, SpanCodec.Read(cut, ref read));
    }

    [Fact]
    public void A_payload_of_another_version_reads_as_empty_rather_than_as_garbage()
    {
        byte[] payload = SpanCodec.Write(new SpanRange[] { R(0, 5, 0) });
        payload[0] = 99;

        Assert.Equal(0, SpanCodec.Count(payload));
    }

    [Fact]
    public void Nonsense_bytes_do_not_throw()
    {
        Assert.Equal(0, SpanCodec.Count(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, SpanCodec.Count(new byte[] { 1 }));
        Assert.Equal(0, SpanCodec.Count(new byte[] { 1, 0xFF }));
    }

    [Fact]
    public void Overlapping_ranges_resolve_to_the_innermost()
    {
        SpanRange[] ranges = { R(0, 20, 0), R(5, 4, 1) };

        Assert.Equal(1, SpanCodec.At(ranges, 6));
        Assert.Equal(0, SpanCodec.At(ranges, 15));
        Assert.Equal(-1, SpanCodec.At(ranges, 40));
    }

    [Fact]
    public void Offsets_are_bytes_not_characters()
    {
        string text = "héllo wörld";
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        // "wörld" starts at char index 6 and byte index 7 — é is two bytes.
        int chars = text.IndexOf("wörld", StringComparison.Ordinal);
        int bytes = Encoding.UTF8.GetByteCount(text.Substring(0, chars));
        Assert.NotEqual(chars, bytes);

        SpanRange[] ranges = { R(bytes, Encoding.UTF8.GetByteCount("wörld"), 0) };

        string covered = Encoding.UTF8.GetString(utf8, ranges[0].Offset, ranges[0].Length);
        Assert.Equal("wörld", covered);
    }
}

public class Spec_Relate
{

    [Fact]
    public void The_span_kind_declares_the_layout_SpanCodec_writes()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);
        RelateIds ids = RelateLibrary.Install(ref table);

        SpanRange[] ranges = new SpanRange[3];
        ranges[0].Offset = 0;
        ranges[0].Length = 4;
        ranges[0].Edge = 0;
        ranges[1].Offset = 5;
        ranges[1].Length = 3;
        ranges[1].Edge = SpanRange.NoEdge;
        ranges[2].Offset = 9;
        ranges[2].Length = 1;
        ranges[2].Edge = 1;

        byte[] payload = SpanCodec.Write(new ReadOnlySpan<SpanRange>(ranges));

        PayloadStride stride = KindTable.SchemaOf(in table, ids.Span).Stride;
        Assert.Equal(SpanCodec.Count(payload), stride.ElementCount(payload.Length));
        Assert.Equal(3, stride.ElementCount(payload.Length));
    }
    struct World
    {
        public SourceFile File;
        public RelateIds Ids;
    }

    static World Start()
    {
        World w = default;
        w.File = SourceFile.Create(64);
        SourceBuild.MintIdentity(ref w.File.Src);
        w.Ids = RelateLibrary.Install(ref w.File.Table);
        return w;
    }

    [Fact]
    public void A_tag_is_a_declaration_so_binding_can_find_it()
    {
        World w = Start();
        KindSchema tag = KindTable.SchemaOf(in w.File.Table, w.Ids.Tag);

        Assert.True(tag.IsDeclaration);
        Assert.Equal(PayloadFormat.Utf8, tag.Payload);
    }

    [Fact]
    public void A_span_is_a_record_and_an_original_is_opaque()
    {
        World w = Start();

        Assert.Equal(PayloadFormat.Record,
            KindTable.SchemaOf(in w.File.Table, w.Ids.Span).Payload);
        Assert.Equal(PayloadFormat.Opaque,
            KindTable.SchemaOf(in w.File.Table, w.Ids.Original).Payload);
        Assert.True(KindTable.SchemaOf(in w.File.Table, w.Ids.Original).InteriorOpaque);
    }

    [Fact]
    public void A_span_declares_the_relations_it_actually_uses()
    {
        World w = Start();
        ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in w.File.Table, w.Ids.Span);

        Assert.Contains(w.Ids.Tagged, used.ToArray());
        Assert.Contains(w.Ids.RefersTo, used.ToArray());
    }

    [Fact]
    public void A_tagged_range_survives_the_format_layer()
    {
        World w = Start();

        NodeId paragraph = SourceBuild.AddRoot(ref w.File.Src, w.Ids.Span);
        SourceBuild.SetPayload(ref w.File.Src, paragraph,
            Encoding.UTF8.GetBytes("the stranger test"));

        NodeId tag = SourceBuild.AddChild(ref w.File.Src, paragraph, w.Ids.Tag);
        SourceBuild.SetPayload(ref w.File.Src, tag, Encoding.UTF8.GetBytes("key-term"));

        NodeId span = SourceBuild.AddChild(ref w.File.Src, paragraph, w.Ids.Span);
        SourceBuild.AddRef(ref w.File.Src, span, w.Ids.Tagged, tag);
        SourceBuild.SetPayload(ref w.File.Src, span,
            SpanCodec.Write(new SpanRange[] { R(4, 13, 0) }));

        byte[] bytes = SourceWrite.ToBytes(in w.File);
        SourceFile back = SourceRead.FromBytes(bytes);

        ReadOnlySpan<byte> payload = SourceWalk.Payload(in back.Src, span);
        SpanRange[] read = null;
        int count = SpanCodec.Read(payload, ref read);

        Assert.Equal(1, count);
        Assert.Equal(4, read[0].Offset);
        Assert.Equal(13, read[0].Length);

        // And the edge the range names still points at the tag.
        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, span);
        Assert.Equal(tag, refs[read[0].Edge].Target);
    }

    static SpanRange R(int offset, int length, ushort edge)
    {
        SpanRange r;
        r.Offset = offset;
        r.Length = length;
        r.Edge = edge;
        return r;
    }
}
