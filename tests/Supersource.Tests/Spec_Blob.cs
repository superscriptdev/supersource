using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Tests;

public class Spec_Blob
{
    static readonly SourceId Library = SourceId.Mint();

    static Source Fresh(out KindTable table, out KindId kind)
    {
        table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        kind = KindTable.Intern(ref table, new QualifiedName(Library, "thing"));

        Source src = Source.Create(64);
        SourceBuild.MintIdentity(ref src);
        SourceBuild.AddRoot(ref src, kind);
        return src;
    }

    static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 + seed);
        }
        return bytes;
    }

    [Fact]
    public void An_address_says_which_segment_and_how_far_in()
    {
        long at = Source.BlobAddress(3, 1234);
        Assert.Equal(3, Source.SegmentOf(at));
        Assert.Equal(1234, Source.OffsetOf(at));

        // The offset half is unsigned: a segment may be larger than two gigabytes without
        // the address folding back on itself.
        long high = Source.BlobAddress(1, int.MaxValue);
        Assert.Equal(1, Source.SegmentOf(high));
        Assert.Equal(int.MaxValue, Source.OffsetOf(high));
    }

    [Fact]
    public void Payloads_past_one_segment_spill_into_more_and_all_read_back()
    {
        Source src = Fresh(out KindTable table, out KindId kind);

        const int Each = 64 * 1024;
        int many = (Source.BlobSegmentBytes / Each) * 3;

        NodeId[] made = new NodeId[many];
        for (int i = 0; i < many; i++)
        {
            made[i] = SourceBuild.AddChild(ref src, src.Root, kind);
            SourceBuild.SetPayload(ref src, made[i], Pattern(Each, i));
        }

        Assert.True(src.BlobSegments > 1,
            "expected more than one segment, got " + src.BlobSegments);

        for (int i = 0; i < many; i++)
        {
            Assert.Equal(Pattern(Each, i), SourceWalk.Payload(in src, made[i]).ToArray());
        }
    }

    [Fact]
    public void A_payload_larger_than_a_segment_is_still_one_span()
    {
        Source src = Fresh(out KindTable table, out KindId kind);

        NodeId huge = SourceBuild.AddChild(ref src, src.Root, kind);
        byte[] bytes = Pattern(Source.BlobSegmentBytes * 2 + 7, 5);
        SourceBuild.SetPayload(ref src, huge, bytes);

        ReadOnlySpan<byte> read = SourceWalk.Payload(in src, huge);
        Assert.Equal(bytes.Length, read.Length);
        Assert.Equal(bytes, read.ToArray());
    }

    [Fact]
    public void Splicing_a_payload_in_a_later_segment_still_works()
    {
        Source src = Fresh(out KindTable table, out KindId kind);

        // Fill past the first segment so the node below lands in a later one.
        for (int i = 0; i < 3; i++)
        {
            NodeId filler = SourceBuild.AddChild(ref src, src.Root, kind);
            SourceBuild.SetPayload(ref src, filler, Pattern(Source.BlobSegmentBytes / 2, i));
        }

        NodeId node = SourceBuild.AddChild(ref src, src.Root, kind);
        SourceBuild.SetPayload(ref src, node, Encoding.UTF8.GetBytes("the quick brown fox"));
        Assert.True(Source.SegmentOf(src.Nodes[node.Index].PayloadOffset) > 0);

        byte[] removed = new byte[16];
        SourceBuild.Splice(ref src, node, 4, 5, Encoding.UTF8.GetBytes("slow"),
            removed, out int removedLength);

        Assert.Equal("quick", Encoding.UTF8.GetString(removed, 0, removedLength));
        Assert.Equal("the slow brown fox",
            Encoding.UTF8.GetString(SourceWalk.Payload(in src, node)));

        // And again, growing it past its capacity so it relocates — possibly into another
        // segment, which is the case that would break if the old one were assumed.
        SourceBuild.Splice(ref src, node, 18, 0,
            Encoding.UTF8.GetBytes(" jumping over something considerably longer than before"),
            removed, out removedLength);

        Assert.Equal(
            "the slow brown fox jumping over something considerably longer than before",
            Encoding.UTF8.GetString(SourceWalk.Payload(in src, node)));
    }

    [Fact]
    public void Labels_are_kept_apart_from_payloads()
    {
        Source src = Fresh(out KindTable table, out KindId kind);
        RelationId about = KindTable.InternRelation(ref table, new QualifiedName(Library, "about"));

        NodeId node = SourceBuild.AddChild(ref src, src.Root, kind);
        SourceBuild.SetPayload(ref src, node, Pattern(4096, 1));
        SourceBuild.AddUnboundRef(ref src, node, about, Encoding.UTF8.GetBytes("looking for foo"));

        Assert.Equal(15, src.LabelsUsed);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in src, node);
        Assert.Equal("looking for foo",
            Encoding.UTF8.GetString(SourceWalk.Label(in src, in refs[0])));
        Assert.Equal(Pattern(4096, 1), SourceWalk.Payload(in src, node).ToArray());
    }

    [Fact]
    public void A_source_spanning_segments_round_trips()
    {
        SourceFile file = SourceFile.Create(64);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "thing"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0, default);
        RelationId about = KindTable.InternRelation(ref file.Table, new QualifiedName(Library, "about"));

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);

        const int Each = 96 * 1024;
        int many = (Source.BlobSegmentBytes / Each) * 2 + 3;
        for (int i = 0; i < many; i++)
        {
            NodeId node = SourceBuild.AddChild(ref file.Src, root, kind);
            SourceBuild.SetPayload(ref file.Src, node, Pattern(Each, i));
            if (i == 0)
            {
                SourceBuild.AddUnboundRef(ref file.Src, node, about,
                    Encoding.UTF8.GetBytes("a hole"));
            }
        }

        Assert.True(file.Src.BlobSegments > 1);

        byte[] bytes = SourceWrite.ToBytes(in file);
        SourceFile back = SourceRead.FromBytes(bytes);

        Assert.Equal(file.Src.NodeCount, back.Src.NodeCount);
        for (int i = 0; i < many; i++)
        {
            NodeId node = SourceWalk.ChildAt(in back.Src, root, i);
            Assert.Equal(Pattern(Each, i), SourceWalk.Payload(in back.Src, node).ToArray());
        }

        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, SourceWalk.ChildAt(in back.Src, root, 0));
        Assert.Equal("a hole", Encoding.UTF8.GetString(SourceWalk.Label(in back.Src, in refs[0])));

        Assert.Equal(bytes, SourceWrite.ToBytes(in back));
    }

    [Fact]
    public void Compaction_brings_a_spread_source_back_into_one_segment()
    {
        Source src = Fresh(out KindTable table, out KindId kind);

        NodeId[] made = new NodeId[24];
        for (int i = 0; i < made.Length; i++)
        {
            made[i] = SourceBuild.AddChild(ref src, src.Root, kind);
            SourceBuild.SetPayload(ref src, made[i], Pattern(96 * 1024, i));
        }
        Assert.True(src.BlobSegments > 1);

        Source tight = SourceCompact.Compact(in src);

        Assert.Equal(1, tight.BlobSegments);
        for (int i = 0; i < made.Length; i++)
        {
            Assert.Equal(Pattern(96 * 1024, i), SourceWalk.Payload(in tight, made[i]).ToArray());
        }
    }
}
