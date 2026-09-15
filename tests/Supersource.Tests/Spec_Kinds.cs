using System;
using System.Text;
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
        public KindBootstrap.Ids Boot;

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
        a.Table = KindBootstrap.CreateTable(out a.Boot);

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
            StructuralFact.OpensScope,
            PayloadFormat.Utf8,
            DisplayShape.Block,
            0xFF3366FF,
            default);

        KindTable.DefineSchema(
            ref a.Table, a.State,
            StructuralFact.Declaration,
            PayloadFormat.Utf8,
            DisplayShape.Block,
            0x22AA88FF,
            new RelationId[] { sources });

        KindTable.DefineSchema(
            ref a.Table, a.Transition,
            StructuralFact.None,
            PayloadFormat.Utf8,
            DisplayShape.Inline,
            0,
            new RelationId[] { a.Targets });

        KindTable.DefineSchema(
            ref a.Table, a.Blob,
            StructuralFact.InteriorOpaque | StructuralFact.ChildOrderSignificant,
            PayloadFormat.Opaque,
            DisplayShape.Leaf,
            0x808080FF,
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

        KindSchemaCodec.Write(ref a.Src, in a.Table, in a.Boot, a.Root);
        return a;
    }

    [Fact]
    public void A_stranger_reads_a_kind_library_it_has_never_linked()
    {
        Authored authored = Author();
        Source src = authored.Src;

        // A different build: bootstrap only, and a deliberately different intern order so
        // no handle can accidentally line up with the author's numbering.
        KindBootstrap.Ids boot;
        KindTable reader = KindBootstrap.CreateTable(out boot);
        KindTable.Intern(ref reader, new QualifiedName(SourceId.Mint(), "something-else"));
        KindTable.InternRelation(ref reader, new QualifiedName(SourceId.Mint(), "unrelated"));

        KindRemap.Apply(ref src, in authored.Table, ref reader);
        int defined = KindSchemaCodec.Read(in src, ref reader, in boot);

        // Four state-machine kinds plus the four bootstrap kinds, which Write embeds too
        // so that a source is legible without knowing the bootstrap by heart.
        Assert.Equal(8, defined);

        KindId machine = KindTable.Lookup(in reader, StateMachines.Machine);
        KindId state = KindTable.Lookup(in reader, StateMachines.State);
        KindId transition = KindTable.Lookup(in reader, StateMachines.Transition);
        KindId blob = KindTable.Lookup(in reader, StateMachines.Blob);

        Assert.False(machine.IsUnknown);
        Assert.NotEqual(authored.Machine.Value, machine.Value);   // handles really did differ

        // Check schema facts without a domain library.
        Assert.True(KindTable.SchemaOf(in reader, machine).OpensScope);
        Assert.False(KindTable.SchemaOf(in reader, machine).IsDeclaration);

        Assert.True(KindTable.SchemaOf(in reader, state).IsDeclaration);
        Assert.False(KindTable.SchemaOf(in reader, state).OpensScope);

        Assert.True(KindTable.SchemaOf(in reader, blob).InteriorOpaque);
        Assert.True(KindTable.SchemaOf(in reader, blob).ChildOrderSignificant);

        Assert.Equal(PayloadFormat.Utf8, KindTable.SchemaOf(in reader, state).Payload);
        Assert.Equal(PayloadFormat.Opaque, KindTable.SchemaOf(in reader, blob).Payload);

        Assert.Equal(DisplayShape.Inline, KindTable.SchemaOf(in reader, transition).Shape);
        Assert.Equal(0x22AA88FFu, KindTable.SchemaOf(in reader, state).DisplayColor);

        // And the source itself still reads correctly through the new handles.
        Assert.Equal(machine, src.Nodes[authored.Root.Index].Kind);
        Assert.Equal("closed", Encoding.UTF8.GetString(SourceWalk.Payload(in src, authored.Idle)));

        RelationId targets = KindTable.LookupRelation(in reader, StateMachines.TargetsRelation);
        Assert.False(targets.IsUnknown);
        Assert.Equal(authored.Running, SourceWalk.Target(in src, authored.IdleToRunning, targets));
    }

    [Fact]
    public void A_schema_carries_the_relations_its_kind_uses()
    {
        Authored authored = Author();
        Source src = authored.Src;

        KindBootstrap.Ids boot;
        KindTable reader = KindBootstrap.CreateTable(out boot);
        KindRemap.Apply(ref src, in authored.Table, ref reader);
        KindSchemaCodec.Read(in src, ref reader, in boot);

        KindId transition = KindTable.Lookup(in reader, StateMachines.Transition);
        RelationId targets = KindTable.LookupRelation(in reader, StateMachines.TargetsRelation);

        ReadOnlySpan<RelationId> used = KindTable.UsedRelations(in reader, transition);
        Assert.Equal(1, used.Length);
        Assert.Equal(targets, used[0]);

        KindId state = KindTable.Lookup(in reader, StateMachines.State);
        RelationId sources = KindTable.LookupRelation(in reader, StateMachines.DocumentsRelation);
        ReadOnlySpan<RelationId> stateRelations = KindTable.UsedRelations(in reader, state);
        Assert.Equal(1, stateRelations.Length);
        Assert.Equal(sources, stateRelations[0]);
    }

    [Fact]
    public void A_kind_with_no_embedded_schema_is_storable_but_reports_no_facts()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        QualifiedName ghost = new QualifiedName(SourceId.Mint(), "kind-from-a-library-nobody-shipped");
        KindId kind = KindTable.Intern(ref table, ghost);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.SchemaSet);
        NodeId node = SourceBuild.AddChild(ref src, root, kind);
        SourceBuild.SetPayload(ref src, node, new byte[] { 1, 2, 3 });

        Assert.False(KindTable.HasSchema(in table, kind));

        // The all-zero schema does not claim anything. Pairing with HasSchema is how a
        // caller tells "declared false" from "not declared".
        KindSchema schema = KindTable.SchemaOf(in table, kind);
        Assert.False(schema.IsDeclaration);
        Assert.False(schema.InteriorOpaque);
        Assert.Equal(PayloadFormat.None, schema.Payload);

        // And it stores and walks like anything else.
        Assert.Equal(kind, src.Nodes[node.Index].Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, SourceWalk.Payload(in src, node).ToArray());
        Assert.Equal(ghost, KindTable.NameOf(in table, kind));
    }

    [Fact]
    public void Writing_skips_kinds_that_have_no_schema()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        QualifiedName undescribed = new QualifiedName(SourceId.Mint(), "undescribed");
        KindId kind = KindTable.Intern(ref table, undescribed);

        Source src = Source.Create(16);
        NodeId root = SourceBuild.AddRoot(ref src, boot.SchemaSet);
        // A node really does use the kind, so it is not skipped for being unreferenced.
        SourceBuild.AddChild(ref src, root, kind);
        KindSchemaCodec.Write(ref src, in table, in boot, root);

        KindBootstrap.Ids readerBoot;
        KindTable reader = KindBootstrap.CreateTable(out readerBoot);
        KindRemap.Apply(ref src, in table, ref reader);
        int defined = KindSchemaCodec.Read(in src, ref reader, in readerBoot);

        Assert.Equal(4, defined);   // the four bootstrap kinds, and not the undescribed one

        // The remap interned it, so the reader can name it — and still has no facts for it.
        KindId readerKind = KindTable.Lookup(in reader, undescribed);
        Assert.False(readerKind.IsUnknown);
        Assert.False(KindTable.HasSchema(in reader, readerKind));
    }

    [Fact]
    public void Same_local_name_in_different_libraries_is_a_different_kind()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        QualifiedName mine = new QualifiedName(SourceId.Mint(), "node");
        QualifiedName theirs = new QualifiedName(SourceId.Mint(), "node");

        KindId a = KindTable.Intern(ref table, mine);
        KindId b = KindTable.Intern(ref table, theirs);

        Assert.NotEqual(a, b);
        Assert.Equal(a, KindTable.Intern(ref table, mine));   // interning is stable
    }

    [Fact]
    public void The_bootstrap_library_identity_is_fixed()
    {
        KindBootstrap.Ids one;
        KindTable a = KindBootstrap.CreateTable(out one);
        KindBootstrap.Ids two;
        KindTable b = KindBootstrap.CreateTable(out two);

        Assert.Equal(one.SchemaSet, two.SchemaSet);
        Assert.Equal(one.Hole, two.Hole);
        Assert.Equal(KindBootstrap.Library, KindTable.NameOf(in a, one.KindSchema).Library);
        Assert.Equal(KindBootstrap.Library, KindTable.NameOf(in b, two.Hole).Library);
    }

    [Fact]
    public void The_hole_kind_is_in_the_bootstrap_and_carries_a_label()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        Assert.True(KindTable.HasSchema(in table, boot.Hole));
        Assert.Equal(PayloadFormat.Utf8, KindTable.SchemaOf(in table, boot.Hole).Payload);
        Assert.Equal(DisplayShape.Leaf, KindTable.SchemaOf(in table, boot.Hole).Shape);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.SchemaSet);
        NodeId hole = SourceBuild.AddChild(ref src, root, boot.Hole);
        SourceBuild.SetPayload(ref src, hole, Encoding.UTF8.GetBytes("foo"));

        Assert.Equal("foo", Encoding.UTF8.GetString(SourceWalk.Payload(in src, hole)));
    }

    [Fact]
    public void Schema_payloads_are_records_rather_than_opaque()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        Assert.Equal(PayloadFormat.Record, KindTable.SchemaOf(in table, boot.KindSchema).Payload);
        Assert.Equal(PayloadFormat.Record, KindTable.SchemaOf(in table, boot.RelationDecl).Payload);

        // And nothing in the bootstrap claims to be opaque — opacity is a fallback for
        // content nobody has described, not a property of the format's own machinery.
        Assert.NotEqual(PayloadFormat.Opaque, KindTable.SchemaOf(in table, boot.Hole).Payload);
        Assert.NotEqual(PayloadFormat.Opaque, KindTable.SchemaOf(in table, boot.SchemaSet).Payload);
        Assert.False(KindTable.SchemaOf(in table, boot.KindSchema).InteriorOpaque);
    }

    [Fact]
    public void Interning_many_names_stays_correct_across_rehash()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot, 8);

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
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);
        int before = table.KindCount;

        KindId missing = KindTable.Lookup(in table, new QualifiedName(SourceId.Mint(), "absent"));

        Assert.True(missing.IsUnknown);
        Assert.Equal(before, table.KindCount);
    }

    [Fact]
    public void Remapping_preserves_structure_even_where_a_name_is_missing()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.SchemaSet);
        NodeId child = SourceBuild.AddChild(ref src, root, boot.Hole);

        // A handle from nowhere — as if the file named a kind the table never interned.
        src.Nodes[child.Index].Kind = new KindId(9999);

        KindBootstrap.Ids readerBoot;
        KindTable reader = KindBootstrap.CreateTable(out readerBoot);
        KindRemap.Apply(ref src, in table, ref reader);

        Assert.True(src.Nodes[child.Index].Kind.IsUnknown);
        Assert.Equal(1, SourceWalk.ChildCount(in src, root));
        Assert.Equal(child, SourceWalk.ChildAt(in src, root, 0));
    }

    [Fact]
    public void Remapping_translates_tombstoned_nodes()
    {
        KindBootstrap.Ids boot;
        KindTable table = KindBootstrap.CreateTable(out boot);

        Source src = Source.Create(8);
        NodeId root = SourceBuild.AddRoot(ref src, boot.SchemaSet);
        NodeId dead = SourceBuild.AddChild(ref src, root, boot.Hole);
        SourceBuild.Delete(ref src, dead);

        KindBootstrap.Ids readerBoot;
        KindTable reader = KindBootstrap.CreateTable(out readerBoot);
        KindTable.Intern(ref reader, new QualifiedName(SourceId.Mint(), "shift-the-numbering"));
        KindRemap.Apply(ref src, in table, ref reader);

        Assert.False(src.IsLive(dead));
        Assert.Equal(KindTable.Lookup(in reader, KindBootstrap.HoleName), src.Nodes[dead.Index].Kind);
    }

    [Fact]
    public void A_declaration_whose_facts_moved_is_brought_forward()
    {
        // Embed the schema before saving.
        Authored a = Author();
        string before = SchemaNodes(in a);
        Assert.Equal(0, KindSchemaCodec.Refresh(ref a.Src, in a.Table, in a.Boot));

        // The library changes its mind: a machine carries no payload after all.
        KindTable.DefineSchema(
            ref a.Table, a.Machine,
            StructuralFact.OpensScope, PayloadFormat.None, DisplayShape.Block, 0xFF3366FF,
            default);

        Assert.Equal(1, KindSchemaCodec.Refresh(ref a.Src, in a.Table, in a.Boot));
        Assert.Equal(0, KindSchemaCodec.Refresh(ref a.Src, in a.Table, in a.Boot));

        // Schema updates must preserve declaration IDs.
        Assert.Equal(before, SchemaNodes(in a));

        KindTable fresh = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        KindSchemaCodec.Read(in a.Src, ref fresh, in boot);
        Assert.Equal(
            PayloadFormat.None,
            KindTable.SchemaOf(in fresh, KindTable.Lookup(in fresh, StateMachines.Machine)).Payload);
        Assert.Equal(
            PayloadFormat.Utf8,
            KindTable.SchemaOf(in fresh, KindTable.Lookup(in fresh, StateMachines.State)).Payload);
    }

    [Fact]
    public void A_declaration_this_build_cannot_check_is_left_alone()
    {
        Authored a = Author();

        KindTable stranger = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        Assert.Equal(0, KindSchemaCodec.Refresh(ref a.Src, in stranger, in boot));
    }

    static string SchemaNodes(in Authored a)
    {
        System.Text.StringBuilder said = new System.Text.StringBuilder();
        for (int i = 0; i < a.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (a.Src.IsLive(node) && a.Src.Nodes[i].Kind == a.Boot.KindSchema)
            {
                said.Append(i).Append(' ');
            }
        }
        return said.ToString();
    }

    [Fact]
    public void A_declaration_nothing_uses_is_dropped()
    {
        Authored a = Author();

        // Hand-encode an unused schema declaration, independently of the current writer’s
        // filtering.
        KindId ghost = KindTable.Intern(
            ref a.Table, new QualifiedName(StateMachines.Library, "ghost"));
        KindTable.DefineSchema(ref a.Table, ghost, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);

        NodeId set = SchemaSet(in a);
        NodeId spare = SourceBuild.AddChild(ref a.Src, set, a.Boot.KindSchema);
        SourceBuild.SetPayload(ref a.Src, spare,
            KindRecord(StateMachines.Library, "ghost", PayloadFormat.Utf8, DisplayShape.Leaf));

        Assert.Equal(1, KindSchemaCodec.Prune(ref a.Src, in a.Table, in a.Boot));
        Assert.Equal(0, KindSchemaCodec.Prune(ref a.Src, in a.Table, in a.Boot));

        KindTable fresh = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        KindSchemaCodec.Read(in a.Src, ref fresh, in boot);

        Assert.True(KindTable.Lookup(in fresh, new QualifiedName(StateMachines.Library, "ghost")).IsUnknown);
        Assert.False(KindTable.Lookup(in fresh, StateMachines.Machine).IsUnknown);
    }

    [Fact]
    public void A_kind_only_a_tombstone_uses_keeps_its_declaration()
    {
        Authored a = Author();
        SourceBuild.Delete(ref a.Src, a.Trace);

        Assert.Equal(0, KindSchemaCodec.Prune(ref a.Src, in a.Table, in a.Boot));

        KindTable fresh = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        KindSchemaCodec.Read(in a.Src, ref fresh, in boot);
        Assert.False(KindTable.Lookup(in fresh, StateMachines.Blob).IsUnknown);
    }

    static NodeId SchemaSet(in Authored a)
    {
        for (int i = 0; i < a.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (a.Src.IsLive(node) && a.Src.Nodes[i].Kind == a.Boot.SchemaSet)
            {
                return node;
            }
        }
        return NodeId.None;
    }

    static byte[] KindRecord(
        SourceId library, string name, PayloadFormat payload, DisplayShape shape)
    {
        byte[] text = Encoding.UTF8.GetBytes(name);
        byte[] record = new byte[16 + 2 + 1 + 1 + 4 + 2 + text.Length];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(record, 0, 8), library.Hi);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(record, 8, 8), library.Lo);

        record[18] = (byte)payload;
        record[19] = (byte)shape;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            new Span<byte>(record, 24, 2), (ushort)text.Length);
        Array.Copy(text, 0, record, 26, text.Length);
        return record;
    }
}
