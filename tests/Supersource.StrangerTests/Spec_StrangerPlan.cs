using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>Reads a plan using qualified names and published layouts, without its authoring library.</summary>
public class Spec_StrangerPlan
{

    static readonly SourceId Plan =
        new SourceId(0x5375706572736372UL, 0x6970745F706C616EUL);

    static readonly SourceId Relate =
        new SourceId(0x5375706572736372UL, 0x6970745F72656C6EUL);

    static byte[] Authored(out long first, out long second)
    {
        SourceFile file = SourceFile.Create(16);
        SourceBuild.MintIdentity(ref file.Src);

        RelationId blocks = KindTable.InternRelation(
            ref file.Table, new QualifiedName(Plan, "blocks"));
        RelationId tagged = KindTable.InternRelation(
            ref file.Table, new QualifiedName(Relate, "tagged"));

        KindId task = KindTable.Intern(ref file.Table, new QualifiedName(Plan, "task"));
        KindId tag = KindTable.Intern(ref file.Table, new QualifiedName(Relate, "tag"));

        KindTable.DefineSchema(ref file.Table, task,
            StructuralFact.Declaration | StructuralFact.ChildOrderSignificant,
            PayloadFormat.Utf8, DisplayShape.Block, 0,
            new ReadOnlySpan<RelationId>(new RelationId[] { blocks, tagged }));
        KindTable.DefineSchema(ref file.Table, tag,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);

        NodeId root = SourceBuild.AddRoot(ref file.Src, task);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("Everything"));

        NodeId a = SourceBuild.AddChild(ref file.Src, root, task);
        SourceBuild.SetPayload(ref file.Src, a, Encoding.UTF8.GetBytes("Write the library"));

        NodeId b = SourceBuild.AddChild(ref file.Src, root, task);
        SourceBuild.SetPayload(ref file.Src, b, Encoding.UTF8.GetBytes("Move the list into it"));

        NodeId done = SourceBuild.AddChild(ref file.Src, root, tag);
        SourceBuild.SetPayload(ref file.Src, done, Encoding.UTF8.GetBytes("done"));

        SourceBuild.AddRef(ref file.Src, a, blocks, b);
        SourceBuild.AddRef(ref file.Src, a, tagged, done);

        // Embed the schema explicitly; name tables alone do not carry structural facts.
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);

        first = a.Index;
        second = b.Index;
        return SourceWrite.ToBytes(in file);
    }

    [Fact]
    public void A_stranger_reads_the_state_and_the_dependencies()
    {
        long first;
        long second;
        SourceFile file = SourceRead.FromBytes(Authored(out first, out second));

        // Found by *name*, because a handle is interned per load and means nothing here.
        RelationId blocks = KindTable.LookupRelation(
            in file.Table, new QualifiedName(Plan, "blocks"));
        RelationId tagged = KindTable.LookupRelation(
            in file.Table, new QualifiedName(Relate, "tagged"));
        Assert.False(blocks.IsUnknown);

        NodeId a = new NodeId(first);
        NodeId b = new NodeId(second);

        Assert.Equal("Write the library",
            Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, a)));

        // Its state: follow the tag edge and read the declaration's name.
        NodeId state = SourceWalk.Target(in file.Src, a, tagged);
        Assert.Equal("done", Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, state)));

        // What is waiting on the second task, by scanning the edges that exist.
        int waiting = 0;
        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!file.Src.IsLive(node))
            {
                continue;
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, node);
            for (int r = 0; r < refs.Length; r++)
            {
                if (refs[r].Relation == blocks && refs[r].IsBound && refs[r].Target == b)
                {
                    waiting++;
                }
            }
        }
        Assert.Equal(1, waiting);
    }

    [Fact]
    public void A_stranger_round_trips_a_plan_it_does_not_understand()
    {
        long first;
        long second;
        byte[] bytes = Authored(out first, out second);

        SourceFile back = SourceRead.FromBytes(bytes);
        Assert.Equal(bytes, SourceWrite.ToBytes(in back));
    }

    [Fact]
    public void Nothing_in_a_plan_is_uninterpretable()
    {
        long first;
        long second;
        SourceFile file = SourceRead.FromBytes(Authored(out first, out second));

        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!file.Src.IsLive(node))
            {
                continue;
            }

            KindId kind = file.Src.Nodes[i].Kind;
            Assert.True(KindTable.HasSchema(in file.Table, kind),
                "n" + i + " is " + KindTable.NameOf(in file.Table, kind).Name + " with no schema");
            Assert.False(string.IsNullOrEmpty(KindTable.NameOf(in file.Table, kind).Name));
        }
    }
}
