using System;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

/// <summary>
/// One payload may be larger than an array. Past one chunk an owned payload is held in chunks,
/// and read, written, spliced, compacted and saved in windows. A source is given small chunks
/// here, so every path a multi-gigabyte payload takes runs on a few kilobytes.
/// </summary>
public class Spec_LargePayloads : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    readonly string dir = Path.Combine(Path.GetTempPath(), "supersource-large-" + Guid.NewGuid().ToString("N"));

    public Spec_LargePayloads() { Directory.CreateDirectory(dir); }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static SourceFile Fresh(int chunkBytes, out NodeId node)
    {
        SourceFile file = SourceFile.Create(16);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "blob"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, PayloadFormat.Opaque, ReadOnlySpan<RelationId>.Empty);
        SourceBuild.MintIdentity(ref file.Src);
        file.Src.ChunkBytes = chunkBytes;
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        node = SourceBuild.AddChild(ref file.Src, root, kind);
        return file;
    }

    static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 131 + (i >> 8));
        return bytes;
    }

    static byte[] All(in Source src, NodeId node)
    {
        var bytes = new byte[SourceWalk.PayloadLength(in src, node)];
        SourceWalk.ReadPayload(in src, node, 0, bytes);
        return bytes;
    }

    [Fact]
    public void A_payload_larger_than_a_chunk_is_held_in_chunks_and_read_in_windows()
    {
        SourceFile file = Fresh(64, out NodeId node);
        byte[] expected = Pattern(1000);
        SourceBuild.SetPayload(ref file.Src, node, expected);

        Assert.Equal(1000L, SourceWalk.PayloadLength(in file.Src, node));
        Assert.True(file.Src.Blob[Source.SegmentOf(file.Src.Nodes[node.Index].PayloadOffset)].IsChunked);
        Assert.Equal(expected, All(in file.Src, node));

        // A window that straddles chunks reads as one run of bytes.
        var window = new byte[100];
        SourceWalk.ReadPayload(in file.Src, node, 30, window);
        Assert.Equal(expected.AsSpan(30, 100).ToArray(), window);

        // One span cannot cross a chunk, and asking for one says how to read instead.
        var refused = Assert.Throws<InvalidOperationException>(() => SourceWalk.Payload(in file.Src, node).ToArray());
        Assert.Contains("windows", refused.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceWalk.ReadPayload(in file.Src, node, 950, new byte[100]));
    }

    [Fact]
    public void A_payload_streams_in_from_a_stream_of_any_length()
    {
        SourceFile file = Fresh(4096, out NodeId node);
        byte[] expected = Pattern(3 * 4096 + 17);
        SourceBuild.SetPayload(ref file.Src, node, new MemoryStream(expected), expected.Length);
        Assert.Equal(expected, All(in file.Src, node));

        Assert.Throws<EndOfStreamException>(() =>
            SourceBuild.SetPayload(ref file.Src, node, new MemoryStream(new byte[10]), 20));
    }

    [Fact]
    public void A_splice_at_a_64_bit_offset_reaches_across_chunks()
    {
        SourceFile file = Fresh(64, out NodeId node);
        byte[] before = Pattern(500);
        SourceBuild.SetPayload(ref file.Src, node, before);

        byte[] inserted = Pattern(90);
        var removed = new byte[40];
        SourceBuild.Splice(ref file.Src, node, 60L, 40L, inserted, removed, out long removedLength);

        Assert.Equal(40L, removedLength);
        Assert.Equal(before.AsSpan(60, 40).ToArray(), removed);
        byte[] expected = [.. before.AsSpan(0, 60), .. inserted, .. before.AsSpan(100)];
        Assert.Equal(expected, All(in file.Src, node));
    }

    [Fact]
    public void Compaction_and_saving_keep_a_chunked_payload_byte_for_byte()
    {
        SourceFile file = Fresh(256, out NodeId node);
        byte[] expected = Pattern(5000);
        SourceBuild.SetPayload(ref file.Src, node, expected);

        Source compacted = SourceCompact.Compact(in file.Src);
        Assert.Equal(expected, All(in compacted, node));

        string path = Path.Combine(dir, "large.ss");
        SourceWrite.ToFile(in file, path);

        // Read back in memory, where it fits one segment, and through a mapping.
        SourceFile copied = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal(expected, All(in copied.Src, node));
        SourceFile mapped = SourceRead.FromFile(path);
        try { Assert.Equal(expected, All(in mapped.Src, node)); }
        finally { SourceFile.Close(ref mapped); }

        // And written again from either, the same bytes: how a payload is held is not stored.
        Assert.Equal(File.ReadAllBytes(path), SourceWrite.ToBytes(in copied));
    }

    static SourceFile Prose(int chunkBytes, out NodeId node)
    {
        SourceFile file = SourceFile.Create(16);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "prose"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, PayloadFormat.Text, ReadOnlySpan<RelationId>.Empty);
        SourceBuild.MintIdentity(ref file.Src);
        file.Src.ChunkBytes = chunkBytes;
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        node = SourceBuild.AddChild(ref file.Src, root, kind);
        return file;
    }

    [Fact]
    public void The_state_hash_reads_a_chunked_payload_as_the_bytes_it_is()
    {
        SourceFile chunked = Fresh(64, out NodeId node);
        SourceFile whole = Fresh(0, out _);
        whole.Src.Identity = chunked.Src.Identity;
        byte[] bytes = Pattern(1000);
        SourceBuild.SetPayload(ref chunked.Src, node, bytes);
        SourceBuild.SetPayload(ref whole.Src, node, bytes);

        Assert.False(SourceWalk.IsContiguous(in chunked.Src, node));
        Assert.True(SourceWalk.IsContiguous(in whole.Src, node));
        Assert.Equal(SourceHash.Of(in whole), SourceHash.Of(in chunked));
    }

    [Fact]
    public void Rules_read_a_chunked_text_payload_as_one_stream_of_utf8()
    {
        // Two-byte characters at an odd start, so chunk boundaries fall inside them.
        SourceFile file = Prose(64, out NodeId node);
        byte[] text = System.Text.Encoding.UTF8.GetBytes("a" + new string('é', 600));
        SourceBuild.SetPayload(ref file.Src, node, text);

        Rules.RuleViolation[] found = null;
        Assert.Equal(0, Rules.RuleSet.Check(Rules.StockRule.PayloadMatchesTheSchema, in file.Src, in file.Table, ref found, 0));

        text[700] = 0xff;
        SourceBuild.SetPayload(ref file.Src, node, text);
        Assert.Equal(1, Rules.RuleSet.Check(Rules.StockRule.PayloadMatchesTheSchema, in file.Src, in file.Table, ref found, 0));
        Assert.Contains("utf-8", found[0].Message);
    }

    [Fact]
    public void A_diff_of_a_chunked_payload_is_the_splice_that_changed_it()
    {
        SourceFile file = Fresh(64, out NodeId node);
        byte[] before = Pattern(1000);
        SourceBuild.SetPayload(ref file.Src, node, before);
        Source after = SourceCompact.Compact(in file.Src);

        byte[] middle = new byte[80];
        Array.Fill(middle, (byte)7);
        SourceBuild.Splice(ref after, node, 400L, 50L, middle, new byte[50], out _);

        Operation[] ops = null;
        int count = Compare.StructuralDiff.Between(in file.Src, in after, in file.Table, ref ops);
        Assert.Equal(1, count);
        Assert.Equal(OpKind.Splice, ops[0].Kind);

        Source replayed = SourceCompact.Compact(in file.Src);
        for (int i = 0; i < count; i++) OpApply.Apply(ref replayed, ref ops[i]);
        Assert.Equal(All(in after, node), All(in replayed, node));
    }

    [Fact]
    public void A_merge_takes_their_change_to_a_chunked_payload_or_keeps_ours_when_both_changed()
    {
        SourceFile file = Fresh(64, out NodeId node);
        SourceBuild.SetPayload(ref file.Src, node, Pattern(1000));
        Source theirs = SourceCompact.Compact(in file.Src);
        SourceBuild.Splice(ref theirs, node, 10L, 5L, new byte[] { 1, 2, 3 }, new byte[5], out _);

        Compare.MergeNote[] notes = null;
        Source merged = Compare.Merge.Between(in file.Src, in file.Src, in theirs, in file.Table, ref notes, out _);
        Assert.Equal(All(in theirs, node), All(in merged, node));

        Source ours = SourceCompact.Compact(in file.Src);
        SourceBuild.Splice(ref ours, node, 900L, 1L, new byte[] { 9 }, new byte[1], out _);
        notes = null;
        merged = Compare.Merge.Between(in file.Src, in ours, in theirs, in file.Table, ref notes, out int decided);
        Assert.Equal(All(in ours, node), All(in merged, node));
        Assert.Contains(notes[0..decided], n => n.Facet == Compare.Facet.Payload && n.How == Compare.Decision.Conflict);
    }
}
