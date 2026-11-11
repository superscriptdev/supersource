using System;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Rules;
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
    [InlineData("vec-v4.ss")]
    [InlineData("externs-v4.ss")]
    public void Rewriting_an_older_file_keeps_its_graph(string name)
    {
        SourceFile before = SourceRead.FromBytes(Legacy(name));
        SourceFile after = SourceRead.FromBytes(Current(name));

        Assert.Equal(before.Src.Identity, after.Src.Identity);
        Assert.Equal(before.Src.Root, after.Src.Root);
        Assert.Equal(before.Src.NodeCount, after.Src.NodeCount);
        Assert.Equal(before.UnknownCount, after.UnknownCount);

        for (int i = 0; i < before.UnknownCount; i++)
        {
            Assert.Equal(before.UnknownSections[i].Id, after.UnknownSections[i].Id);
            Assert.Equal(before.UnknownSections[i].Bytes, after.UnknownSections[i].Bytes);
        }

        for (int i = 0; i < before.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            // A deleted id stays deleted and holds nothing: a file holds a state.
            Assert.Equal(before.Src.IsLive(node), after.Src.IsLive(node));
            if (!before.Src.IsLive(node))
            {
                continue;
            }

            Assert.Equal(before.Src.Nodes[i].Parent, after.Src.Nodes[i].Parent);
            Assert.Equal(
                SourceWalk.Payload(in before.Src, node).ToArray(),
                SourceWalk.Payload(in after.Src, node).ToArray());

            // Kinds and relations are interned per load, so the durable name is what has to
            // match — comparing handles would compare two tables and prove nothing.
            QualifiedName kind = KindTable.NameOf(in before.Table, before.Src.Nodes[i].Kind);
            Assert.Equal(kind, KindTable.NameOf(in after.Table, after.Src.Nodes[i].Kind));
            Assert.Equal(
                KindTable.HasSchema(in before.Table, before.Src.Nodes[i].Kind),
                KindTable.HasSchema(in after.Table, after.Src.Nodes[i].Kind));
            Assert.Equal(
                KindTable.SchemaOf(in before.Table, before.Src.Nodes[i].Kind).Payload,
                KindTable.SchemaOf(in after.Table, after.Src.Nodes[i].Kind).Payload);

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

    [Fact]
    public void A_version_4_file_keeps_its_schemas_as_a_section_and_none_as_nodes()
    {
        SourceFile old = SourceRead.FromBytes(Legacy("vec-v4.ss"));

        // Its schema nodes were read into the table and then removed from the graph.
        for (int i = 0; i < old.Src.NodeCount; i++)
        {
            if (!old.Src.IsLive(new NodeId(i)))
            {
                continue;
            }
            QualifiedName kind = KindTable.NameOf(in old.Table, old.Src.Nodes[i].Kind);
            Assert.NotEqual("schema-set", kind.Name);
            Assert.NotEqual("kind-schema", kind.Name);
            Assert.True(KindTable.HasSchema(in old.Table, old.Src.Nodes[i].Kind), kind.Name);
        }

        // The old formats arrive as the new ones: a name is text.
        KindId function = KindTable.Lookup(in old.Table, KindTable.NameOf(in old.Table, old.Src.Nodes[4].Kind));
        Assert.Equal(PayloadFormat.Text, KindTable.SchemaOf(in old.Table, function).Payload);
    }

    [Fact]
    public void A_version_4_external_reference_becomes_a_node_id_and_a_shared_dependency()
    {
        SourceFile old = SourceRead.FromBytes(Legacy("externs-v4.ss"));
        ExternalKinds.Ids ids = ExternalKinds.Install(ref old.Table);

        int references = 0, dependencies = 0;
        NodeId shared = NodeId.None;
        for (int i = 0; i < old.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!old.Src.IsLive(node))
            {
                continue;
            }
            if (old.Src.Nodes[i].Kind == ids.Dependency)
            {
                dependencies++;
                Assert.Equal(old.Src.Root, SourceWalk.Parent(in old.Src, node));
            }
            if (old.Src.Nodes[i].Kind != ids.ExternalRef)
            {
                continue;
            }

            references++;
            Assert.Equal(8, SourceWalk.Payload(in old.Src, node).Length);
            Assert.True(ExternalKinds.TryDecode(in old.Src, node, out External external));
            Assert.Equal("frame-stdlib.ss", external.Locator);
            Assert.False(external.Source.IsNone);

            NodeId dependency = ExternalKinds.DependencyOf(in old.Src, node);
            if (shared.IsNone) shared = dependency;
            Assert.Equal(shared, dependency);
        }

        // Every reference into the one library it uses shares its address.
        Assert.True(references > 1);
        Assert.Equal(1, dependencies);

        // And every payload reads as its schema says, the new dependency's included.
        RuleViolation[] wrong = null;
        Assert.Equal(0, RuleSet.Check(StockRule.PayloadMatchesTheSchema, in old.Src, in old.Table, ref wrong, 0));
        Assert.Equal(0, RuleSet.Check(StockRule.EveryKindIsInterpretable, in old.Src, in old.Table, ref wrong, 0));
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    [InlineData("vec-v4.ss")]
    [InlineData("externs-v4.ss")]
    public void The_current_version_is_smaller(string name)
    {
        Assert.True(Current(name).Length < Legacy(name).Length);
    }

    [Theory]
    [InlineData("ordinary-v1.ss")]
    [InlineData("hostile-v1.ss")]
    [InlineData("ordinary-v2.ss")]
    [InlineData("hostile-v2.ss")]
    [InlineData("vec-v4.ss")]
    [InlineData("externs-v4.ss")]
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
