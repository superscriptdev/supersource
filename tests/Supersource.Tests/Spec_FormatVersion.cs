using System;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_FormatVersion
{
    static byte[] Legacy(string name)
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "legacy", name));
    }

    static byte[] Current(string name)
    {
        SourceFile file = SourceRead.FromBytes(Legacy(name));
        return SourceWrite.ToBytes(in file);
    }

    [Theory]
    [InlineData("ordinary-v1.ss", 1)]
    [InlineData("hostile-v1.ss", 1)]
    [InlineData("ordinary-v2.ss", 2)]
    [InlineData("hostile-v2.ss", 2)]
    public void The_files_really_are_the_version_they_claim(string name, int major)
    {
        Assert.Equal(major, BitConverter.ToUInt16(Legacy(name), 8));
        Assert.True(SourceWrite.FormatMajor > major, "this build should be writing something newer");
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    public void An_older_file_reads(string name)
    {
        SourceFile file = SourceRead.FromBytes(Legacy(name));

        Assert.True(file.Src.NodeCount > 0);
        Assert.True(file.Src.Root.IsSome);
        Assert.False(file.Src.Identity.IsNone);

        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            NodeId parent = SourceWalk.Parent(in file.Src, node);
            if (parent.IsNone)
            {
                continue;
            }

            // The parent link and the child run agree, which is the thing a delta-coded
            // index would break first if the two decoders disagreed.
            Assert.True(SourceWalk.IndexInParent(in file.Src, node) >= 0);
        }
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    public void Rewriting_a_version_one_file_changes_only_the_framing(string name)
    {
        SourceFile before = SourceRead.FromBytes(Legacy(name));
        SourceFile after = SourceRead.FromBytes(Current(name));

        Assert.Equal(before.Src.Identity, after.Src.Identity);
        Assert.Equal(before.Src.Root, after.Src.Root);
        Assert.Equal(before.Src.NodeCount, after.Src.NodeCount);
        Assert.Equal(before.UnknownCount, after.UnknownCount);
        Assert.Equal(before.SectionOrderCount, after.SectionOrderCount);

        for (int i = 0; i < before.UnknownCount; i++)
        {
            Assert.Equal(before.UnknownSections[i].Id, after.UnknownSections[i].Id);
            Assert.Equal(before.UnknownSections[i].Bytes, after.UnknownSections[i].Bytes);
        }

        for (int i = 0; i < before.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            Assert.Equal(before.Src.Nodes[i].Flags, after.Src.Nodes[i].Flags);
            Assert.Equal(before.Src.Nodes[i].Parent, after.Src.Nodes[i].Parent);
            Assert.Equal(
                SourceWalk.Payload(in before.Src, node).ToArray(),
                SourceWalk.Payload(in after.Src, node).ToArray());

            // Kinds and relations are interned per load, so the durable name is what has to
            // match — comparing handles would compare two tables and prove nothing.
            Assert.Equal(
                KindTable.NameOf(in before.Table, before.Src.Nodes[i].Kind),
                KindTable.NameOf(in after.Table, after.Src.Nodes[i].Kind));

            Assert.Equal(
                SourceWalk.Children(in before.Src, node).ToArray(),
                SourceWalk.Children(in after.Src, node).ToArray());

            ReadOnlySpan<Ref> mine = SourceWalk.References(in before.Src, node);
            ReadOnlySpan<Ref> theirs = SourceWalk.References(in after.Src, node);
            Assert.Equal(mine.Length, theirs.Length);

            for (int e = 0; e < mine.Length; e++)
            {
                Assert.Equal(mine[e].Target, theirs[e].Target);
                Assert.Equal(mine[e].IsBound, theirs[e].IsBound);
                Assert.Equal(
                    KindTable.RelationNameOf(in before.Table, mine[e].Relation),
                    KindTable.RelationNameOf(in after.Table, theirs[e].Relation));
                Assert.Equal(
                    SourceWalk.Label(in before.Src, in mine[e]).ToArray(),
                    SourceWalk.Label(in after.Src, in theirs[e]).ToArray());
            }
        }
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    public void The_current_version_is_smaller(string name)
    {
        Assert.True(Current(name).Length < Legacy(name).Length);
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    public void Converting_twice_changes_nothing_the_second_time(string name)
    {
        byte[] once = Current(name);
        SourceFile loaded = SourceRead.FromBytes(once);
        Assert.Equal(once, SourceWrite.ToBytes(in loaded));
    }

    [Fact]
    public void A_newer_major_is_refused_rather_than_guessed_at()
    {
        byte[] bytes = Current("ordinary-v1.ss");
        bytes[8] = (byte)(SourceWrite.FormatMajor + 1);

        Assert.Throws<FormatCorruptException>(() => SourceRead.FromBytes(bytes));
    }

    [Fact]
    public void A_padded_variable_width_integer_is_corruption_rather_than_a_smaller_number()
    {
        ByteWriter w = ByteWriter.Create(16);
        w.UVar(1);
        Assert.Equal(1, w.Length);

        // The same value spelled in two bytes: a continuation that adds nothing.
        byte[] padded = new byte[] { 0x81, 0x00 };
        ByteReader r = ByteReader.Over(padded);
        Assert.Throws<FormatCorruptException>(() => r.UVar());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(63L)]
    [InlineData(-64L)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Signed_variable_width_integers_round_trip(long value)
    {
        ByteWriter w = ByteWriter.Create(16);
        w.SVar(value);

        ByteReader r = ByteReader.Over(w.ToArray());
        Assert.Equal(value, r.SVar());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Minus_one_costs_one_byte()
    {
        ByteWriter w = ByteWriter.Create(16);
        w.SVar(-1);
        Assert.Equal(1, w.Length);
    }
}
