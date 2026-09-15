using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Kinds.Tests;

public class Spec_Stride
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName RasterName = new QualifiedName(Library, "raster");
    static readonly QualifiedName LabelName = new QualifiedName(Library, "label");

    struct World
    {
        public Source Src;
        public KindTable Table;
        public KindBootstrap.Ids Boot;
        public KindId Raster;
        public KindId Label;
        public NodeId Root;
    }

    static World Author()
    {
        World w = default;
        w.Table = KindBootstrap.CreateTable(out w.Boot);

        w.Raster = KindTable.Intern(ref w.Table, RasterName);
        w.Label = KindTable.Intern(ref w.Table, LabelName);

        KindTable.DefineSchema(
            ref w.Table, w.Raster,
            StructuralFact.None, PayloadFormat.Record, DisplayShape.Leaf, 0,
            default, PayloadStride.Of(8, 4));

        KindTable.DefineSchema(
            ref w.Table, w.Label,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Inline, 0,
            default);

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Label);
        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("sprite"));
        SourceBuild.AddChild(ref w.Src, w.Root, w.Raster);

        KindSchemaCodec.Write(ref w.Src, in w.Table, in w.Boot, w.Root);
        return w;
    }

    static KindTable ReadFresh(in World w, out int defined)
    {
        KindBootstrap.Ids ids;
        KindTable fresh = KindBootstrap.CreateTable(out ids);
        defined = KindSchemaCodec.Read(in w.Src, ref fresh, in ids);
        return fresh;
    }

    [Fact]
    public void A_stranger_reads_the_stride_out_of_the_source()
    {
        World w = Author();

        int defined;
        KindTable fresh = ReadFresh(in w, out defined);

        KindId raster = KindTable.Lookup(in fresh, RasterName);
        Assert.False(raster.IsUnknown);

        KindSchema schema = KindTable.SchemaOf(in fresh, raster);
        Assert.Equal(PayloadFormat.Record, schema.Payload);
        Assert.True(schema.Stride.IsArray);
        Assert.Equal(8, schema.Stride.Header);
        Assert.Equal(4, schema.Stride.Unit);
    }

    [Fact]
    public void Element_count_comes_from_the_schema_and_not_from_a_library()
    {
        World w = Author();
        int defined;
        KindTable fresh = ReadFresh(in w, out defined);

        PayloadStride stride = KindTable.SchemaOf(in fresh, KindTable.Lookup(in fresh, RasterName)).Stride;

        Assert.Equal(0, stride.ElementCount(8));
        Assert.Equal(2, stride.ElementCount(8 + 4 + 4));

        // Total, like every other read of a payload here: a truncated buffer reports the
        // elements that are actually there rather than throwing.
        Assert.Equal(2, stride.ElementCount(8 + 4 + 4 + 3));
        Assert.Equal(0, stride.ElementCount(3));
    }

    [Fact]
    public void A_kind_that_is_not_an_array_writes_no_extra_bytes()
    {
        World w = Author();

        int nameBytes = Encoding.UTF8.GetByteCount("label");
        const int BeforeThisFact = 16 + 2 + 1 + 1 + 4 + 2;

        Assert.Equal(BeforeThisFact + nameBytes, SchemaPayloadFor(in w, LabelName).Length);
        Assert.Equal(BeforeThisFact + Encoding.UTF8.GetByteCount("raster") + 4,
            SchemaPayloadFor(in w, RasterName).Length);
    }

    [Fact]
    public void A_schema_written_before_the_fact_reads_as_not_an_array()
    {
        byte[] old = OldStyleKindRecord(Library, "raster", StructuralFact.None,
            PayloadFormat.Record, DisplayShape.Leaf, 0);

        KindSchemaCodec.StoredSchema stored;
        Assert.True(KindSchemaCodec.ReadKind(old, out stored));

        Assert.Equal("raster", stored.Name.Name);
        Assert.Equal(PayloadFormat.Record, stored.Payload);
        Assert.False(stored.Stride.IsArray);
        Assert.Equal(0, stored.Stride.Unit);
    }

    [Fact]
    public void A_stale_stride_is_brought_forward()
    {
        World w = Author();

        // The kind decided its header was longer than it used to be.
        KindTable.DefineSchema(
            ref w.Table, w.Raster,
            StructuralFact.None, PayloadFormat.Record, DisplayShape.Leaf, 0,
            default, PayloadStride.Of(12, 4));

        Assert.Equal(1, KindSchemaCodec.Refresh(ref w.Src, in w.Table, in w.Boot));

        int defined;
        KindTable fresh = ReadFresh(in w, out defined);
        PayloadStride stride = KindTable.SchemaOf(in fresh, KindTable.Lookup(in fresh, RasterName)).Stride;

        Assert.Equal(12, stride.Header);
        Assert.Equal(4, stride.Unit);

        // And running it again finds nothing left to do.
        Assert.Equal(0, KindSchemaCodec.Refresh(ref w.Src, in w.Table, in w.Boot));
    }

    static ReadOnlySpan<byte> SchemaPayloadFor(in World w, QualifiedName name)
    {
        for (int i = 0; i < w.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!w.Src.IsLive(node) || w.Src.Nodes[i].Kind != w.Boot.KindSchema)
            {
                continue;
            }

            KindSchemaCodec.StoredSchema stored;
            ReadOnlySpan<byte> payload = SourceWalk.Payload(in w.Src, node);
            if (KindSchemaCodec.ReadKind(payload, out stored) && stored.Name == name)
            {
                return payload;
            }
        }

        throw new InvalidOperationException("no schema node for " + name);
    }

    static byte[] OldStyleKindRecord(
        SourceId library,
        string name,
        StructuralFact facts,
        PayloadFormat payload,
        DisplayShape shape,
        uint colour)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buffer = new byte[16 + 2 + 1 + 1 + 4 + 2 + nameBytes.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, 0, 8), library.Hi);
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, 8, 8), library.Lo);

        int at = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)facts);
        at += 2;
        buffer[at] = (byte)payload;
        at += 1;
        buffer[at] = (byte)shape;
        at += 1;
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buffer, at, 4), colour);
        at += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)nameBytes.Length);
        at += 2;
        Array.Copy(nameBytes, 0, buffer, at, nameBytes.Length);
        return buffer;
    }
}
