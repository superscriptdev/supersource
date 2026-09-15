using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_Mapped : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    readonly string dir;

    public Spec_Mapped()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-mapped-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    string Written(string name, int nodes, int payload)
    {
        SourceFile file = SourceFile.Create(64);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "leaf"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0, default);

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("root"));

        for (int i = 0; i < nodes; i++)
        {
            NodeId node = SourceBuild.AddChild(ref file.Src, root, kind);
            SourceBuild.SetPayload(ref file.Src, node, Pattern(payload, i));
        }

        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);

        string path = Path.Combine(dir, name);
        SourceWrite.ToFile(in file, path);
        return path;
    }

    static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 53 + seed);
        }
        return bytes;
    }

    static bool PointsAtTheFile(in Source src, NodeId node)
    {
        return src.Blob[Source.SegmentOf(src.Nodes[node.Index].PayloadOffset)].IsMapped;
    }

    [Fact]
    public void A_payload_read_from_a_file_points_into_it()
    {
        string path = Written("mapped.ss", 40, 128);
        byte[] raw = File.ReadAllBytes(path);

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            NodeId first = SourceWalk.ChildAt(in file.Src, file.Src.Root, 0);
            Assert.True(PointsAtTheFile(in file.Src, first));

            // The address is a place in the file, so the bytes there are the bytes here.
            long at = Source.OffsetOf(file.Src.Nodes[first.Index].PayloadOffset);
            ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, first);

            Assert.Equal(Pattern(128, 0), payload.ToArray());
            Assert.Equal(payload.ToArray(), new ReadOnlySpan<byte>(raw, (int)at, payload.Length).ToArray());
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    [Fact]
    public void Loading_a_mapped_source_copies_no_payload_bytes()
    {
        string path = Written("nocopy.ss", 200, 512);

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            long owned = 0;
            for (int i = 0; i < file.Src.BlobSegments; i++)
            {
                owned += file.Src.Blob[i].IsMapped ? 0 : file.Src.Blob[i].Used;
            }

            Assert.Equal(0, owned);

            // Borrowed payloads remain readable while the mapping is open.
            for (int i = 0; i < 200; i++)
            {
                NodeId node = SourceWalk.ChildAt(in file.Src, file.Src.Root, i);
                Assert.Equal(Pattern(512, i), SourceWalk.Payload(in file.Src, node).ToArray());
            }
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    [Fact]
    public void Editing_a_mapped_payload_moves_it_and_leaves_the_file_alone()
    {
        string path = Written("edit.ss", 8, 64);
        byte[] before = File.ReadAllBytes(path);

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            NodeId node = SourceWalk.ChildAt(in file.Src, file.Src.Root, 3);
            Assert.True(PointsAtTheFile(in file.Src, node));

            SourceBuild.SetPayload(ref file.Src, node, Encoding.UTF8.GetBytes("mine now"));

            Assert.False(PointsAtTheFile(in file.Src, node));
            Assert.Equal("mine now", Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, node)));

            // Its neighbours are still the file's.
            NodeId neighbour = SourceWalk.ChildAt(in file.Src, file.Src.Root, 4);
            Assert.True(PointsAtTheFile(in file.Src, neighbour));
            Assert.Equal(Pattern(64, 4), SourceWalk.Payload(in file.Src, neighbour).ToArray());
        }
        finally
        {
            SourceFile.Close(ref file);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Splicing_a_mapped_payload_moves_it_too()
    {
        string path = Written("splice.ss", 4, 32);
        byte[] before = File.ReadAllBytes(path);

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            NodeId node = SourceWalk.ChildAt(in file.Src, file.Src.Root, 1);
            byte[] original = SourceWalk.Payload(in file.Src, node).ToArray();

            byte[] removed = new byte[8];
            SourceBuild.Splice(ref file.Src, node, 4, 4, new byte[] { 1, 2, 3 },
                removed, out int removedLength);

            Assert.False(PointsAtTheFile(in file.Src, node));
            Assert.Equal(4, removedLength);
            Assert.Equal(new ReadOnlySpan<byte>(original, 4, 4).ToArray(),
                new ReadOnlySpan<byte>(removed, 0, 4).ToArray());

            byte[] expected = new byte[original.Length - 4 + 3];
            Array.Copy(original, 0, expected, 0, 4);
            expected[4] = 1; expected[5] = 2; expected[6] = 3;
            Array.Copy(original, 8, expected, 7, original.Length - 8);

            Assert.Equal(expected, SourceWalk.Payload(in file.Src, node).ToArray());
        }
        finally
        {
            SourceFile.Close(ref file);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Compacting_detaches_a_source_from_the_file_it_was_read_from()
    {
        string path = Written("detach.ss", 30, 96);

        Source detached;
        SourceFile file = SourceRead.FromFile(path);
        try
        {
            Assert.True(PointsAtTheFile(in file.Src, SourceWalk.ChildAt(in file.Src, file.Src.Root, 0)));
            detached = SourceCompact.Compact(in file.Src);
        }
        finally
        {
            SourceFile.Close(ref file);
        }

        // The file is closed and deleted; the source is still whole.
        File.Delete(path);

        for (int i = 0; i < 30; i++)
        {
            NodeId node = SourceWalk.ChildAt(in detached, detached.Root, i);
            Assert.False(detached.Blob[Source.SegmentOf(detached.Nodes[node.Index].PayloadOffset)].IsMapped);
            Assert.Equal(Pattern(96, i), SourceWalk.Payload(in detached, node).ToArray());
        }
    }

    [Fact]
    public void A_mapped_source_writes_back_what_it_read()
    {
        string path = Written("rewrite.ss", 120, 200);
        byte[] original = File.ReadAllBytes(path);

        SourceFile file = SourceRead.FromFile(path);
        try
        {
            Assert.Equal(original, SourceWrite.ToBytes(in file));
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    [Fact]
    public void Closing_is_safe_to_do_twice_and_on_a_source_that_never_mapped()
    {
        SourceFile never = SourceFile.Create(4);
        SourceFile.Close(ref never);
        SourceFile.Close(ref never);

        string path = Written("twice.ss", 2, 8);
        SourceFile file = SourceRead.FromFile(path);
        SourceFile.Close(ref file);
        SourceFile.Close(ref file);
    }
}
