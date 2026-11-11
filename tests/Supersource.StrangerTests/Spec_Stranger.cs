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
    public void Unfamiliar_schemas_payloads_and_relations_survive_without_a_vocabulary_library()
    {
        SourceFile file = SourceFile.Create(16);
        var library = SourceId.Mint();
        var name = new QualifiedName(library, "foreign-record");
        var relationName = new QualifiedName(library, "foreign-link");
        KindId kind = KindTable.Intern(ref file.Table, name);
        RelationId relation = KindTable.InternRelation(ref file.Table, relationName);
        KindTable.DefineSchema(ref file.Table, kind,
            StructuralFact.InteriorOpaque | StructuralFact.Annotation,
            PayloadFormat.Opaque, new[] { relation });
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        NodeId child = SourceBuild.AddChild(ref file.Src, root, kind);
        byte[] payload = { 0, 255, 19, 128 };
        SourceBuild.SetPayload(ref file.Src, child, payload);
        SourceBuild.AddRef(ref file.Src, child, relation, root);
        byte[] saved = SourceWrite.ToBytes(in file);
        SourceFile reopened = SourceRead.FromBytes(saved);
        KindId readKind = reopened.Src.Nodes[child.Index].Kind;
        KindSchema schema = KindTable.SchemaOf(in reopened.Table, readKind);
        Assert.Equal(name, KindTable.NameOf(in reopened.Table, readKind));
        Assert.Equal(PayloadFormat.Opaque, schema.Payload);
        Assert.Equal(StructuralFact.InteriorOpaque | StructuralFact.Annotation, schema.Facts);
        Assert.Equal(payload, SourceWalk.Payload(in reopened.Src, child).ToArray());
        Assert.Equal(child, SourceWalk.ChildAt(in reopened.Src, root, 0));
        var edge = SourceWalk.References(in reopened.Src, child)[0];
        Assert.Equal(relationName, KindTable.RelationNameOf(in reopened.Table, edge.Relation));
        Assert.Equal(root, edge.Target);
        Assert.Equal(saved, SourceWrite.ToBytes(in reopened));
        // The format knows nothing built on it.
        Assert.DoesNotContain(typeof(Source).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name.StartsWith("Superscript", StringComparison.Ordinal));
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
        // A file holds a state, not the edits that produced it: a deleted node keeps its id and
        // its kind and loses its content. So a fixture with nothing deleted re-saves byte for
        // byte, and any fixture re-saves to a fixpoint.
        byte[] original = Load(name);
        SourceFile file = SourceRead.FromBytes(original);
        byte[] saved = SourceWrite.ToBytes(in file);
        Assert.Equal(saved, SourceWrite.ToBytes(SourceRead.FromBytes(saved)));
        if (!HasDeleted(in file.Src)) Assert.Equal(original, saved);
    }

    static bool HasDeleted(in Source src)
    {
        for (long i = 0; i < src.NodeCount; i++)
        {
            if (!src.IsLive(new NodeId(i))) return true;
        }
        return false;
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
    public void A_stranger_can_answer_every_structural_question_about_every_node(string name)
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
            bool unusedOpaque = schema.InteriorOpaque;
            bool unusedOrder = schema.ChildOrderSignificant;
            bool unusedAnnotates = schema.Annotates;

            // And how to read its bytes: a format this build knows, schema or not.
            Assert.True(Enum.IsDefined(typeof(PayloadForm), schema.Payload.Form),
                "node " + id + " has a payload form this build does not know");

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

        Assert.True(described >= 1, "expected the file to describe what it holds");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Editing_what_a_stranger_understands_does_not_damage_what_it_does_not(string name)
    {
        SourceFile file = SourceRead.FromBytes(Load(name));

        int unknownSectionsBefore = file.UnknownCount;
        long nodesBefore = file.Src.NodeCount;

        // The most ordinary edit there is: append a node of a kind the stranger brings itself,
        // since the format has none of its own.
        NodeId root = file.Src.Root;
        Assert.True(file.Src.IsLive(root));
        NodeId added = SourceBuild.AddChild(ref file.Src, root, TestKinds.Install(ref file.Table).Leaf);
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
        // the ones this build cannot interpret. A deleted one keeps its id and kind; its content
        // is not the file's to keep.
        for (int i = 0; i < nodesBefore; i++)
        {
            Assert.Equal(file.Src.Nodes[i].Flags, reloaded.Src.Nodes[i].Flags);
            Assert.Equal(
                KindTable.NameOf(in file.Table, file.Src.Nodes[i].Kind),
                KindTable.NameOf(in reloaded.Table, reloaded.Src.Nodes[i].Kind));
            if (!file.Src.IsLive(new NodeId(i))) continue;
            Assert.Equal(
                SourceWalk.Payload(in file.Src, new NodeId(i)).ToArray(),
                SourceWalk.Payload(in reloaded.Src, new NodeId(i)).ToArray());
        }
    }
}
