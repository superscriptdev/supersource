using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_Streaming : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    readonly string dir;

    public Spec_Streaming()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-streaming-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    string At(string name)
    {
        return Path.Combine(dir, name);
    }

    static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 37 + seed);
        }
        return bytes;
    }

    static SourceFile Big(int nodes, int payload)
    {
        SourceFile file = SourceFile.Create(64);

        KindId section = KindTable.Intern(ref file.Table, new QualifiedName(Library, "section"));
        KindId leaf = KindTable.Intern(ref file.Table, new QualifiedName(Library, "leaf"));
        RelationId about = KindTable.InternRelation(ref file.Table, new QualifiedName(Library, "about"));

        KindTable.DefineSchema(ref file.Table, section, StructuralFact.OpensScope,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { about });
        KindTable.DefineSchema(ref file.Table, leaf, StructuralFact.None,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0, new RelationId[] { about });

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, section);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("root"));

        for (int i = 0; i < nodes; i++)
        {
            NodeId node = SourceBuild.AddChild(ref file.Src, root, leaf);
            SourceBuild.SetPayload(ref file.Src, node, Pattern(payload, i));

            if (i % 97 == 0)
            {
                SourceBuild.AddRef(ref file.Src, node, about, root);
            }
            if (i % 89 == 0)
            {
                SourceBuild.AddUnboundRef(ref file.Src, node, about,
                    Encoding.UTF8.GetBytes("looking for " + i));
            }
        }

        Operation op = default;
        op.Kind = OpKind.ModifyValue;
        op.Node = root;
        op.Value = Encoding.UTF8.GetBytes("edited root");
        Log.Do(ref file.History, ref file.Src, op, 3);

        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);
        return file;
    }

    [Fact]
    public void The_file_and_the_array_agree_byte_for_byte()
    {
        SourceFile file = Big(4000, 64);

        string path = At("agree.ss");
        SourceWrite.ToFile(in file, path);

        byte[] asArray = SourceWrite.ToBytes(in file);
        Assert.Equal(asArray, File.ReadAllBytes(path));

        // Comfortably past one window, so the slide and the flush both ran many times.
        Assert.True(asArray.Length > 4 * SourceRead.WindowBytes,
            "expected a source several windows long, got " + asArray.Length);

        SourceFile fromFile = SourceRead.FromFile(path);
        SourceFile fromArray = SourceRead.FromBytes(asArray);

        Assert.Equal(fromArray.Src.NodeCount, fromFile.Src.NodeCount);
        Assert.Equal(SourceWrite.ToBytes(in fromArray), SourceWrite.ToBytes(in fromFile));

        SourceFile.Close(ref fromFile);
    }

    [Fact]
    public void A_source_read_from_a_file_is_the_source_that_was_written()
    {
        SourceFile file = Big(2500, 48);
        string path = At("survives.ss");
        SourceWrite.ToFile(in file, path);

        SourceFile back = SourceRead.FromFile(path);

        Assert.Equal(file.Src.Identity, back.Src.Identity);
        Assert.Equal(file.Src.NodeCount, back.Src.NodeCount);
        Assert.Equal(file.History.Count, back.History.Count);

        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            Assert.Equal(file.Src.IsLive(node), back.Src.IsLive(node));
            Assert.Equal(
                SourceWalk.Payload(in file.Src, node).ToArray(),
                SourceWalk.Payload(in back.Src, node).ToArray());
            Assert.Equal(
                SourceWalk.Children(in file.Src, node).ToArray(),
                SourceWalk.Children(in back.Src, node).ToArray());

            ReadOnlySpan<Ref> want = SourceWalk.References(in file.Src, node);
            ReadOnlySpan<Ref> have = SourceWalk.References(in back.Src, node);
            Assert.Equal(want.Length, have.Length);

            for (int r = 0; r < want.Length; r++)
            {
                Assert.Equal(want[r].Target, have[r].Target);
                Assert.Equal(
                    SourceWalk.Label(in file.Src, in want[r]).ToArray(),
                    SourceWalk.Label(in back.Src, in have[r]).ToArray());
            }
        }

        SourceFile.Close(ref back);
    }

    [Fact]
    public void A_payload_larger_than_the_window_still_reads()
    {
        SourceFile file = SourceFile.Create(16);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "blob"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0, default);

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);

        byte[] huge = Pattern(SourceRead.WindowBytes * 5 + 11, 3);
        NodeId node = SourceBuild.AddChild(ref file.Src, root, kind);
        SourceBuild.SetPayload(ref file.Src, node, huge);
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);

        string path = At("huge.ss");
        SourceWrite.ToFile(in file, path);

        SourceFile back = SourceRead.FromFile(path);
        Assert.Equal(huge, SourceWalk.Payload(in back.Src, node).ToArray());
        SourceFile.Close(ref back);
    }

    [Fact]
    public void A_file_round_trips_through_itself()
    {
        SourceFile file = Big(3000, 32);
        string first = At("first.ss");
        SourceWrite.ToFile(in file, first);

        for (int i = 0; i < 3; i++)
        {
            SourceFile loaded = SourceRead.FromFile(first);
            string next = At("next" + i + ".ss");
            SourceWrite.ToFile(in loaded, next);
            SourceFile.Close(ref loaded);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(next));
        }
    }

    [Fact]
    public void An_unreadable_section_survives_a_streamed_round_trip()
    {
        SourceFile file = Big(1200, 32);

        byte[] strange = Pattern(SourceRead.WindowBytes * 2 + 5, 9);
        file.UnknownSections = new UnknownSection[1];
        file.UnknownSections[0].Id = 0xC0FFEE;
        file.UnknownSections[0].Bytes = strange;
        file.UnknownCount = 1;
        file.SectionOrderCount = 0;   // canonical order, which appends the unknown

        string path = At("strange.ss");
        SourceWrite.ToFile(in file, path);

        SourceFile back = SourceRead.FromFile(path);
        Assert.Equal(1, back.UnknownCount);
        Assert.Equal(0xC0FFEEu, back.UnknownSections[0].Id);
        Assert.Equal(strange, back.UnknownSections[0].Bytes);
        SourceFile.Close(ref back);
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused()
    {
        SourceFile file = Big(4, 8);
        byte[] bytes = SourceWrite.ToBytes(in file);

        using (ForwardOnly forward = new ForwardOnly(bytes))
        {
            Assert.Throws<FormatCorruptException>(() => SourceRead.FromStream(forward));
        }

        using (ForwardOnly forward = new ForwardOnly(new byte[0]))
        {
            Assert.Throws<InvalidOperationException>(() => SourceWrite.ToStream(in file, forward));
        }
    }

    sealed class ForwardOnly : MemoryStream
    {
        public ForwardOnly(byte[] bytes) : base(bytes) { }
        public override bool CanSeek { get { return false; } }
    }
}
