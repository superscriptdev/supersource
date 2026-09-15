using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>
/// Core-only compatibility tests. Keep this project independent of vocabulary and domain assemblies
/// so fixtures exercise unfamiliar content.
/// </summary>
public class Spec_Stranger
{
    [Fact]
    public void Unfamiliar_schema_hints_payloads_and_relations_survive_without_a_vocabulary_library()
    {
        SourceFile file = SourceFile.Create(16);
        var library = SourceId.Mint();
        var name = new QualifiedName(library, "foreign-record");
        var relationName = new QualifiedName(library, "foreign-link");
        KindId kind = KindTable.Intern(ref file.Table, name);
        RelationId relation = KindTable.InternRelation(ref file.Table, relationName);
        KindTable.DefineSchema(ref file.Table, kind,
            StructuralFact.InteriorOpaque | StructuralFact.Annotation,
            PayloadFormat.Opaque, DisplayShape.Leaf, 0x12345678, new[] { relation });
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        NodeId child = SourceBuild.AddChild(ref file.Src, root, kind);
        byte[] payload = { 0, 255, 19, 128 };
        SourceBuild.SetPayload(ref file.Src, child, payload);
        SourceBuild.AddRef(ref file.Src, child, relation, root);
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);
        byte[] saved = SourceWrite.ToBytes(in file);
        SourceFile reopened = SourceRead.FromBytes(saved);
        KindId readKind = reopened.Src.Nodes[child.Index].Kind;
        KindSchema schema = KindTable.SchemaOf(in reopened.Table, readKind);
        Assert.Equal(name, KindTable.NameOf(in reopened.Table, readKind));
        Assert.Equal(PayloadFormat.Opaque, schema.Payload);
        Assert.Equal(DisplayShape.Leaf, schema.Shape);
        Assert.Equal(0x12345678u, schema.DisplayColor);
        Assert.Equal(StructuralFact.InteriorOpaque | StructuralFact.Annotation, schema.Facts);
        Assert.Equal(payload, SourceWalk.Payload(in reopened.Src, child).ToArray());
        Assert.Equal(child, SourceWalk.ChildAt(in reopened.Src, root, 0));
        var edge = SourceWalk.References(in reopened.Src, child)[0];
        Assert.Equal(relationName, KindTable.RelationNameOf(in reopened.Table, edge.Relation));
        Assert.Equal(root, edge.Target);
        Assert.Equal(saved, SourceWrite.ToBytes(in reopened));
        Assert.DoesNotContain(typeof(Source).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "Supersource.Vocabularies");
    }

    static string[] Fixtures()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ss") : new string[0];
    }

    public static TheoryData<string> AllFixtures()
    {
        TheoryData<string> data = new TheoryData<string>();
        string[] files = Fixtures();
        for (int i = 0; i < files.Length; i++)
        {
            data.Add(Path.GetFileName(files[i]));
        }

        if (files.Length == 0)
        {
            // xUnit refuses an empty theory, and a silently-empty stranger test is worse
            // than a failing one: it would pass forever while checking nothing.
            data.Add("<no fixtures found>");
        }
        return data;
    }

    static byte[] Load(string name)
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
    }

    [Fact]
    public void There_are_fixtures_to_check()
    {
        Assert.NotEmpty(Fixtures());
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void A_stranger_re_saves_every_fixture_unchanged(string name)
    {
        byte[] original = Load(name);
        SourceFile file = SourceRead.FromBytes(original);
        Assert.Equal(original, SourceWrite.ToBytes(in file));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void A_stranger_walks_every_node_without_reaching_for_anything(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        int visited = 0;
        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId id = new NodeId(i);
            if (!file.Src.IsLive(id))
            {
                continue;
            }

            visited++;

            // Containment, both directions.
            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, id);
            for (int c = 0; c < children.Length; c++)
            {
                Assert.Equal(id, SourceWalk.Parent(in file.Src, new NodeId(children[c])));
            }

            // Edges out, bound or not.
            ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, id);
            for (int r = 0; r < refs.Length; r++)
            {
                if (refs[r].IsUnbound)
                {
                    SourceWalk.Label(in file.Src, in refs[r]);
                }
            }

            // Bytes.
            SourceWalk.Payload(in file.Src, id);
        }

        Assert.True(visited > 0, "fixture " + name + " has no live nodes");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void A_stranger_can_render_every_node_from_display_defaults(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId id = new NodeId(i);
            if (!file.Src.IsLive(id))
            {
                continue;
            }

            KindId kind = file.Src.Nodes[i].Kind;
            KindSchema schema = KindTable.SchemaOf(in file.Table, kind);

            // Every structural question has an answer, schema or not.
            bool unusedDecl = schema.IsDeclaration;
            bool unusedScope = schema.OpensScope;
            bool unusedOpaque = schema.InteriorOpaque;
            bool unusedOrder = schema.ChildOrderSignificant;

            // A shape to draw, always. Default is a legitimate answer; a missing one is not.
            Assert.True(Enum.IsDefined(typeof(DisplayShape), schema.Shape),
                "node " + id + " has an unrenderable shape");

            // And something to call it, even if only that it cannot be named.
            QualifiedName kindName = KindTable.NameOf(in file.Table, kind);
            string label = kindName.IsNone ? "<unknown>" : kindName.Name;
            Assert.NotNull(label);
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void A_stranger_can_measure_how_opaque_a_document_is(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        int live = 0;
        int uninterpretable = 0;

        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            if (!file.Src.IsLive(new NodeId(i)))
            {
                continue;
            }
            live++;
            if (!KindTable.HasSchema(in file.Table, file.Src.Nodes[i].Kind))
            {
                uninterpretable++;
            }
        }

        Assert.True(live > 0);
        Assert.True(uninterpretable <= live);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Whatever_schemas_a_fixture_embeds_are_readable_without_a_fetch(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        // The bootstrap is always known, by definition.
        Assert.True(KindTable.HasSchema(in file.Table, file.Boot.SchemaSet));
        Assert.True(KindTable.HasSchema(in file.Table, file.Boot.Hole));

        // Anything else with a schema resolved from bytes in this file alone.
        int described = 0;
        for (int i = 0; i < file.Table.KindCount; i++)
        {
            if (file.Table.SchemaDefined[i])
            {
                described++;
                Assert.Equal(new KindId(i), file.Table.Schemas[i].Kind);
            }
        }

        Assert.True(described >= 4, "expected at least the bootstrap schemas");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Editing_what_a_stranger_understands_does_not_damage_what_it_does_not(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        int unknownSectionsBefore = file.UnknownCount;
        long nodesBefore = file.Src.NodeCount;

        // The most ordinary edit there is: append a hole, which is the one kind every
        // source embeds and every build understands.
        NodeId root = file.Src.Root;
        Assert.True(file.Src.IsLive(root));
        NodeId added = SourceBuild.AddChild(ref file.Src, root, file.Boot.Hole);
        SourceBuild.SetPayload(ref file.Src, added, Encoding.UTF8.GetBytes("a stranger was here"));

        SourceFile reloaded = SourceRead.FromBytes(SourceWrite.ToBytes(in file));

        Assert.Equal(nodesBefore + 1, reloaded.Src.NodeCount);
        Assert.Equal(unknownSectionsBefore, reloaded.UnknownCount);
        for (int i = 0; i < unknownSectionsBefore; i++)
        {
            Assert.Equal(file.UnknownSections[i].Id, reloaded.UnknownSections[i].Id);
            Assert.Equal(file.UnknownSections[i].Bytes, reloaded.UnknownSections[i].Bytes);
        }

        // Every node that was there is still there, with its kind name intact — including
        // the ones this build cannot interpret.
        for (int i = 0; i < nodesBefore; i++)
        {
            Assert.Equal(file.Src.Nodes[i].Flags, reloaded.Src.Nodes[i].Flags);
            Assert.Equal(
                KindTable.NameOf(in file.Table, file.Src.Nodes[i].Kind),
                KindTable.NameOf(in reloaded.Table, reloaded.Src.Nodes[i].Kind));
            Assert.Equal(
                SourceWalk.Payload(in file.Src, new NodeId(i)).ToArray(),
                SourceWalk.Payload(in reloaded.Src, new NodeId(i)).ToArray());
        }
    }
}
