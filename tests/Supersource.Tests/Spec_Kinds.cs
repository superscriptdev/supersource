using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Kinds.Tests;

public class Spec_Kinds
{

    static class StateMachines
    {
        public static readonly SourceId Library = SourceId.Mint();

        public static readonly QualifiedName Machine = new QualifiedName(Library, "machine");
        public static readonly QualifiedName State = new QualifiedName(Library, "state");
        public static readonly QualifiedName Transition = new QualifiedName(Library, "transition");
        public static readonly QualifiedName Blob = new QualifiedName(Library, "captured-trace");

        public static readonly QualifiedName TargetsRelation = new QualifiedName(Library, "targets");
        public static readonly QualifiedName DocumentsRelation = new QualifiedName(Library, "sources");
    }

    struct Authored
    {
        public Source Src;
        public KindTable Table;
        public TestKinds.Ids Boot;

        public KindId Machine;
        public KindId State;
        public KindId Transition;
        public KindId Blob;
        public RelationId Targets;

        public NodeId Root;
        public NodeId Idle;
        public NodeId Running;
        public NodeId IdleToRunning;
        public NodeId Trace;
    }

    static Authored Author()
    {
        Authored a = default;
        a.Table = TestKinds.CreateTable(out a.Boot);

        a.Machine = KindTable.Intern(ref a.Table, StateMachines.Machine);
        a.State = KindTable.Intern(ref a.Table, StateMachines.State);
        a.Transition = KindTable.Intern(ref a.Table, StateMachines.Transition);
        a.Blob = KindTable.Intern(ref a.Table, StateMachines.Blob);

        a.Targets = KindTable.InternRelation(ref a.Table, StateMachines.TargetsRelation);
        RelationId sources = KindTable.InternRelation(ref a.Table, StateMachines.DocumentsRelation);

        // A machine opens a scope and contains states; states are declarations you can
        // bind a name to; a transition points at the state it targets; a captured trace is
        // opaque bytes.
        KindTable.DefineSchema(
            ref a.Table, a.Machine,
            StructuralFact.ChildOrderSignificant,
            PayloadFormat.Text,
            default);

        KindTable.DefineSchema(
            ref a.Table, a.State,
            StructuralFact.Annotation,
            PayloadFormat.Text,
            new RelationId[] { sources });

        KindTable.DefineSchema(
            ref a.Table, a.Transition,
            StructuralFact.None,
            PayloadFormat.Text,
            new RelationId[] { a.Targets });

        KindTable.DefineSchema(
            ref a.Table, a.Blob,
            StructuralFact.InteriorOpaque | StructuralFact.ChildOrderSignificant,
            PayloadFormat.Opaque,
            default);

        a.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref a.Src);

        a.Root = SourceBuild.AddRoot(ref a.Src, a.Machine);
        SourceBuild.SetPayload(ref a.Src, a.Root, Encoding.UTF8.GetBytes("door"));

        a.Idle = SourceBuild.AddChild(ref a.Src, a.Root, a.State);
        SourceBuild.SetPayload(ref a.Src, a.Idle, Encoding.UTF8.GetBytes("closed"));

        a.Running = SourceBuild.AddChild(ref a.Src, a.Root, a.State);
        SourceBuild.SetPayload(ref a.Src, a.Running, Encoding.UTF8.GetBytes("open"));

        a.IdleToRunning = SourceBuild.AddChild(ref a.Src, a.Idle, a.Transition);
        SourceBuild.AddRef(ref a.Src, a.IdleToRunning, a.Targets, a.Running);

        a.Trace = SourceBuild.AddChild(ref a.Src, a.Root, a.Blob);
        SourceBuild.SetPayload(ref a.Src, a.Trace, new byte[] { 0x7F, 0x00, 0xC3, 0x11 });

        return a;
    }

    /// <summary>What a reader with none of the author's libraries gets back from the bytes.</summary>
    static SourceFile Saved(in Authored a)
    {
        SourceFile file = SourceFile.Create();
        file.Src = a.Src;
        file.Table = a.Table;
        return SourceRead.FromBytes(SourceWrite.ToBytes(in file));
    }

    [Fact]
    public void A_stranger_reads_a_kind_library_it_has_never_linked()
    {
        Authored authored = Author();
        SourceFile back = Saved(in authored);
        KindTable reader = back.Table;

        KindId machine = KindTable.Lookup(in reader, StateMachines.Machine);
        KindId state = KindTable.Lookup(in reader, StateMachines.State);
        KindId transition = KindTable.Lookup(in reader, StateMachines.Transition);
        KindId blob = KindTable.Lookup(in reader, StateMachines.Blob);

        Assert.True(KindTable.HasSchema(in reader, machine));
        Assert.True(KindTable.HasSchema(in reader, state));
        Assert.True(KindTable.HasSchema(in reader, transition));
        Assert.True(KindTable.HasSchema(in reader, blob));

        // Facts and formats, without the library that defined them.
        Assert.True(KindTable.SchemaOf(in reader, machine).ChildOrderSignificant);
        Assert.False(KindTable.SchemaOf(in reader, machine).Annotates);
        Assert.True(KindTable.SchemaOf(in reader, state).Annotates);
        Assert.True(KindTable.SchemaOf(in reader, blob).InteriorOpaque);
        Assert.True(KindTable.SchemaOf(in reader, blob).ChildOrderSignificant);
        Assert.Equal(PayloadFormat.Text, KindTable.SchemaOf(in reader, state).Payload);
        Assert.Equal(PayloadFormat.Opaque, KindTable.SchemaOf(in reader, blob).Payload);

        // And the source itself reads correctly through the reader's handles.
        Assert.Equal(machine, back.Src.Nodes[authored.Root.Index].Kind);
        Assert.Equal("closed", Encoding.UTF8.GetString(SourceWalk.Payload(in back.Src, authored.Idle)));
        RelationId targets = KindTable.LookupRelation(in reader, StateMachines.TargetsRelation);
        Assert.Equal(authored.Running, SourceWalk.Target(in back.Src, authored.IdleToRunning, targets));
    }

    [Fact]
    public void A_schema_carries_the_relations_its_kind_uses()
    {
        SourceFile back = Saved(Author());
        KindTable reader = back.Table;

        KindId transition = KindTable.Lookup(in reader, StateMachines.Transition);
        RelationId targets = KindTable.LookupRelation(in reader, StateMachines.TargetsRelation);
        ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in reader, transition);
        Assert.Equal(1, used.Length);
        Assert.Equal(targets, used[0]);

        // A relation no edge has is still named, because a schema lists it.
        KindId state = KindTable.Lookup(in reader, StateMachines.State);
        RelationId sources = KindTable.LookupRelation(in reader, StateMachines.DocumentsRelation);
        Assert.False(sources.IsUnknown);
        Assert.Equal(sources, KindTable.UsedRelations(in reader, state)[0]);
    }

    [Fact]
    public void The_schemas_are_a_section_and_the_graph_holds_none_of_them()
    {
        Authored authored = Author();
        SourceFile file = SourceFile.Create();
        file.Src = authored.Src;
        file.Table = authored.Table;
        byte[] bytes = SourceWrite.ToBytes(in file);

        SectionSpan[] spans = null;
        int count = SourceRead.Sections(bytes, ref spans);
        bool hasSchemas = false;
        for (int i = 0; i < count; i++)
        {
            hasSchemas |= spans[i].Id == (uint)SectionId.Schemas;
        }
        Assert.True(hasSchemas);

        // Every node in the file is one the author made.
        SourceFile back = SourceRead.FromBytes(bytes);
        Assert.Equal(authored.Src.NodeCount, back.Src.NodeCount);
        Assert.Equal(SourceWalk.ChildCount(in authored.Src, authored.Root), SourceWalk.ChildCount(in back.Src, authored.Root));
    }

    [Fact]
    public void A_kind_library_writes_its_kinds_with_no_node_to_carry_them()
    {
        SourceFile library = SourceFile.Create();
        SourceBuild.MintIdentity(ref library.Src);
        KindId shelf = KindTable.Intern(ref library.Table, new QualifiedName(library.Src.Identity, "shelf"));
        KindTable.DefineSchema(ref library.Table, shelf, StructuralFact.ChildOrderSignificant, PayloadFormat.Text, default);
        KindId book = KindTable.Intern(ref library.Table, new QualifiedName(library.Src.Identity, "book"));
        KindTable.DefineSchema(ref library.Table, book, StructuralFact.None, PayloadFormat.Text, default);
        SourceBuild.AddRoot(ref library.Src, shelf);

        SourceFile back = SourceRead.FromBytes(SourceWrite.ToBytes(in library));

        // A book is declared here and used nowhere, and it is still written.
        KindId read = KindTable.Lookup(in back.Table, new QualifiedName(library.Src.Identity, "book"));
        Assert.True(KindTable.HasSchema(in back.Table, read));
        Assert.Equal(1, back.Src.NodeCount);
    }

    [Fact]
    public void A_kind_only_a_deleted_node_used_is_not_written()
    {
        Authored a = Author();
        SourceBuild.Delete(ref a.Src, a.Trace);

        SourceFile back = Saved(in a);
        Assert.True(KindTable.Lookup(in back.Table, StateMachines.Blob).IsUnknown);
    }

    [Fact]
    public void Elements_and_their_fields_survive_a_save()
    {
        Authored a = Author();
        KindId mesh = KindTable.Intern(ref a.Table, new QualifiedName(StateMachines.Library, "vertices"));
        PayloadFormat vertices = PayloadFormat.Many(
            PayloadElement.Of(20,
                PayloadField.Of("position", PayloadScalar.F32, 0, 3),
                PayloadField.Of("colour", PayloadScalar.U8, 12, 4),
                PayloadField.Of("visible", PayloadScalar.Bool, 16)),
            PayloadElement.Of(4, PayloadField.Of("count", PayloadScalar.U32, 0)));
        KindTable.DefineSchema(ref a.Table, mesh, StructuralFact.None, vertices, default);
        NodeId node = SourceBuild.AddChild(ref a.Src, a.Root, mesh);
        SourceBuild.SetPayload(ref a.Src, node, new byte[4 + 20 * 2]);

        SourceFile back = Saved(in a);
        PayloadFormat read = KindTable.SchemaOf(in back.Table,
            KindTable.Lookup(in back.Table, new QualifiedName(StateMachines.Library, "vertices"))).Payload;

        Assert.Equal(vertices, read);
        Assert.Equal(2, read.ElementCount(SourceWalk.Payload(in back.Src, node).Length));
    }

    [Fact]
    public void A_payload_its_kind_fixes_the_length_of_is_written_without_one_and_reads_back()
    {
        Authored a = Author();
        KindId number = KindTable.Intern(ref a.Table, new QualifiedName(StateMachines.Library, "number"));
        KindTable.DefineSchema(ref a.Table, number, StructuralFact.None, PayloadFormat.One(PayloadScalar.I64), default);
        NodeId right = SourceBuild.AddChild(ref a.Src, a.Root, number);
        SourceBuild.SetPayload(ref a.Src, right, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        // One that disagrees with its kind is written with its length, and kept as it is.
        NodeId wrong = SourceBuild.AddChild(ref a.Src, a.Root, number);
        SourceBuild.SetPayload(ref a.Src, wrong, new byte[] { 9, 9, 9 });

        SourceFile back = Saved(in a);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, SourceWalk.Payload(in back.Src, right).ToArray());
        Assert.Equal(new byte[] { 9, 9, 9 }, SourceWalk.Payload(in back.Src, wrong).ToArray());
    }

    [Fact]
    public void A_kind_with_no_embedded_schema_is_storable_but_reports_no_facts()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot);

        QualifiedName ghost = new QualifiedName(SourceId.Mint(), "kind-from-a-library-nobody-shipped");
        KindId kind = KindTable.Intern(ref table, ghost);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Leaf);
        NodeId node = SourceBuild.AddChild(ref src, root, kind);
        SourceBuild.SetPayload(ref src, node, new byte[] { 1, 2, 3 });

        Assert.False(KindTable.HasSchema(in table, kind));

        // The all-zero schema does not claim anything. Pairing with HasSchema is how a
        // caller tells "declared false" from "not declared".
        KindSchema schema = KindTable.SchemaOf(in table, kind);
        Assert.False(schema.Annotates);
        Assert.False(schema.InteriorOpaque);
        Assert.Equal(PayloadFormat.None, schema.Payload);

        // And it stores and walks like anything else.
        Assert.Equal(kind, src.Nodes[node.Index].Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, SourceWalk.Payload(in src, node).ToArray());
        Assert.Equal(ghost, KindTable.NameOf(in table, kind));
    }

    [Fact]
    public void A_kind_with_no_schema_is_written_without_one()
    {
        Authored a = Author();
        QualifiedName undescribed = new QualifiedName(SourceId.Mint(), "undescribed");
        KindId kind = KindTable.Intern(ref a.Table, undescribed);
        NodeId node = SourceBuild.AddChild(ref a.Src, a.Root, kind);
        SourceBuild.SetPayload(ref a.Src, node, new byte[] { 4, 5 });

        SourceFile back = Saved(in a);
        KindId read = KindTable.Lookup(in back.Table, undescribed);
        Assert.False(read.IsUnknown);
        Assert.False(KindTable.HasSchema(in back.Table, read));
        Assert.Equal(new byte[] { 4, 5 }, SourceWalk.Payload(in back.Src, node).ToArray());
    }

    [Fact]
    public void Same_local_name_in_different_libraries_is_a_different_kind()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot);

        QualifiedName mine = new QualifiedName(SourceId.Mint(), "node");
        QualifiedName theirs = new QualifiedName(SourceId.Mint(), "node");

        KindId a = KindTable.Intern(ref table, mine);
        KindId b = KindTable.Intern(ref table, theirs);

        Assert.NotEqual(a, b);
        Assert.Equal(a, KindTable.Intern(ref table, mine));   // interning is stable
    }

    [Fact]
    public void The_hole_kind_has_a_fixed_identity_and_carries_a_label()
    {
        KindTable a = TestKinds.CreateTable(out TestKinds.Ids one);
        KindTable b = TestKinds.CreateTable(out TestKinds.Ids two);

        Assert.Equal(one.Leaf, two.Leaf);
        Assert.Equal(TestKinds.Library, KindTable.NameOf(in a, one.Leaf).Library);
        Assert.Equal(PayloadFormat.Text, KindTable.SchemaOf(in b, two.Leaf).Payload);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, one.Leaf);
        NodeId hole = SourceBuild.AddChild(ref src, root, one.Leaf);
        SourceBuild.SetPayload(ref src, hole, Encoding.UTF8.GetBytes("foo"));
        Assert.Equal("foo", Encoding.UTF8.GetString(SourceWalk.Payload(in src, hole)));
    }

    [Fact]
    public void Interning_many_names_stays_correct_across_rehash()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot, 8);

        SourceId library = SourceId.Mint();
        QualifiedName[] names = new QualifiedName[500];
        KindId[] ids = new KindId[500];

        for (int i = 0; i < names.Length; i++)
        {
            names[i] = new QualifiedName(library, "kind" + i);
            ids[i] = KindTable.Intern(ref table, names[i]);
        }

        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(ids[i], KindTable.Lookup(in table, names[i]));
            Assert.Equal(names[i], KindTable.NameOf(in table, ids[i]));
        }

        for (int i = 0; i < names.Length; i++)
        {
            for (int j = i + 1; j < names.Length; j++)
            {
                if (ids[i] == ids[j])
                {
                    Assert.Fail("interning collapsed " + names[i] + " and " + names[j]);
                }
            }
        }
    }

    [Fact]
    public void Lookup_of_an_unknown_name_never_allocates_a_handle()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot);
        int before = table.KindCount;

        KindId missing = KindTable.Lookup(in table, new QualifiedName(SourceId.Mint(), "absent"));

        Assert.True(missing.IsUnknown);
        Assert.Equal(before, table.KindCount);
    }

    [Fact]
    public void Remapping_preserves_structure_even_where_a_name_is_missing()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Leaf);
        NodeId child = SourceBuild.AddChild(ref src, root, boot.Leaf);

        // A handle from nowhere — as if the file named a kind the table never interned.
        src.Nodes[child.Index].Kind = new KindId(9999);

        TestKinds.Ids readerBoot;
        KindTable reader = TestKinds.CreateTable(out readerBoot);
        KindRemap.Apply(ref src, in table, ref reader);

        Assert.True(src.Nodes[child.Index].Kind.IsUnknown);
        Assert.Equal(1, SourceWalk.ChildCount(in src, root));
        Assert.Equal(child, SourceWalk.ChildAt(in src, root, 0));
    }

    [Fact]
    public void Remapping_leaves_a_deleted_id_empty()
    {
        TestKinds.Ids boot;
        KindTable table = TestKinds.CreateTable(out boot);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Leaf);
        NodeId dead = SourceBuild.AddChild(ref src, root, boot.Leaf);
        SourceBuild.Delete(ref src, dead);

        TestKinds.Ids readerBoot;
        KindTable reader = TestKinds.CreateTable(out readerBoot);
        KindTable.Intern(ref reader, new QualifiedName(SourceId.Mint(), "shift-the-numbering"));
        KindRemap.Apply(ref src, in table, ref reader);

        Assert.False(src.IsLive(dead));
        Assert.True(src.Nodes[dead.Index].Kind.IsUnknown);
    }
}
