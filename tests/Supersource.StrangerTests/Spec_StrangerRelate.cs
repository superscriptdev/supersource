using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>Reads tagged ranges using published layouts without linking the relation vocabulary.</summary>
public class Spec_StrangerRelate
{
    // Exactly what a stranger would have: names copied off the wire, not linked.
    static readonly SourceId Relate =
        new SourceId(0x5375706572736372UL, 0x6970745F72656C6EUL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Relate, name);
    }

    static byte[] Authored(out long paragraph, out long span, out long tag)
    {
        SourceFile file = SourceFile.Create(16);
        SourceBuild.MintIdentity(ref file.Src);

        RelationId tagged = KindTable.InternRelation(ref file.Table, N("tagged"));
        KindId spanKind = KindTable.Intern(ref file.Table, N("span"));
        KindId tagKind = KindTable.Intern(ref file.Table, N("tag"));
        KindId paraKind = KindTable.Intern(
            ref file.Table, new QualifiedName(new SourceId(1, 2), "paragraph"));

        KindTable.DefineSchema(ref file.Table, paraKind,
            StructuralFact.ChildOrderSignificant, PayloadFormat.Utf8,
            DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref file.Table, spanKind,
            StructuralFact.None, PayloadFormat.Record, DisplayShape.Inline, 0,
            new ReadOnlySpan<RelationId>(new RelationId[] { tagged }));
        KindTable.DefineSchema(ref file.Table, tagKind,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);

        NodeId p = SourceBuild.AddRoot(ref file.Src, paraKind);
        SourceBuild.SetPayload(ref file.Src, p,
            Encoding.UTF8.GetBytes("the stranger test decides every case"));

        NodeId t = SourceBuild.AddChild(ref file.Src, p, tagKind);
        SourceBuild.SetPayload(ref file.Src, t, Encoding.UTF8.GetBytes("key-term"));

        NodeId s = SourceBuild.AddChild(ref file.Src, p, spanKind);
        SourceBuild.AddRef(ref file.Src, s, tagged, t);

        // Encode the published range layout independently of SpanCodec: u8 version, u16 count,
        // then i32 offset, i32 length and u16 reference index.
        byte[] payload = new byte[3 + 10];
        payload[0] = 1;
        payload[1] = 1; payload[2] = 0;
        WriteI32(payload, 3, 4);      // offset: "stranger" starts at byte 4
        WriteI32(payload, 7, 8);      // length: eight bytes
        payload[11] = 0; payload[12] = 0;   // edge 0 — the `tagged` reference above
        SourceBuild.SetPayload(ref file.Src, s, payload);

        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, p);

        paragraph = p.Index;
        span = s.Index;
        tag = t.Index;
        return SourceWrite.ToBytes(in file);
    }

    static void WriteI32(byte[] into, int at, int value)
    {
        into[at] = (byte)value;
        into[at + 1] = (byte)(value >> 8);
        into[at + 2] = (byte)(value >> 16);
        into[at + 3] = (byte)(value >> 24);
    }

    static int ReadI32(ReadOnlySpan<byte> from, int at)
    {
        return from[at] | (from[at + 1] << 8) | (from[at + 2] << 16) | (from[at + 3] << 24);
    }

    [Fact]
    public void A_stranger_reads_a_marked_range_and_follows_it_to_its_tag()
    {
        long paragraph, span, tag;
        SourceFile file = SourceRead.FromBytes(Authored(out paragraph, out span, out tag));

        // The schema arrived with the source, so the span's payload is legible.
        KindId spanKind = file.Src.Nodes[span].Kind;
        Assert.True(KindTable.HasSchema(in file.Table, spanKind));
        Assert.Equal(PayloadFormat.Record,
            KindTable.SchemaOf(in file.Table, spanKind).Payload);

        ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, new NodeId(span));
        Assert.Equal(1, payload[0]);
        Assert.Equal(1, payload[1] | (payload[2] << 8));

        int offset = ReadI32(payload, 3);
        int length = ReadI32(payload, 7);
        int edge = payload[11] | (payload[12] << 8);

        // Which characters are marked, straight out of the parent's text.
        ReadOnlySpan<byte> text = SourceWalk.Payload(in file.Src, new NodeId(paragraph));
        Assert.Equal("stranger",
            Encoding.UTF8.GetString(text.Slice(offset, length)));

        // And what they are marked as, by following the edge the range names.
        ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, new NodeId(span));
        NodeId target = refs[edge].Target;
        Assert.Equal("key-term",
            Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, target)));
    }

    [Fact]
    public void A_stranger_writes_it_back_unchanged()
    {
        long paragraph, span, tag;
        byte[] original = Authored(out paragraph, out span, out tag);

        SourceFile file = SourceRead.FromBytes(original);
        Assert.Equal(original, SourceWrite.ToBytes(in file));
    }
}
