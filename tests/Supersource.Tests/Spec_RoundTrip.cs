using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_RoundTrip
{

    static class Alien
    {
        public static readonly SourceId Library =
            new SourceId(0xA11E_1234_5678_9ABCUL, 0xDEF0_1122_3344_5566UL);

        public static readonly QualifiedName Widget = new QualifiedName(Library, "widget");
        public static readonly QualifiedName Gizmo = new QualifiedName(Library, "gizmo");
        public static readonly QualifiedName Wobbles = new QualifiedName(Library, "wobbles-toward");
    }

    const uint UnknownSectionId = 0xC0FFEE;

    static readonly byte[] UnknownSectionBytes =
        Encoding.UTF8.GetBytes("a future version wrote this and we must not eat it");

    [Fact]
    public void A_document_full_of_things_this_build_cannot_read_round_trips_byte_for_byte()
    {
        byte[] original = BuildAlienFile();

        SourceFile loaded = SourceRead.FromBytes(original);
        byte[] rewritten = SourceWrite.ToBytes(in loaded);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void Round_tripping_repeatedly_is_a_fixpoint()
    {
        byte[] bytes = BuildAlienFile();

        for (int i = 0; i < 5; i++)
        {
            SourceFile loaded = SourceRead.FromBytes(bytes);
            byte[] next = SourceWrite.ToBytes(in loaded);
            Assert.Equal(bytes, next);
            bytes = next;
        }
    }

    [Fact]
    public void The_unreadable_parts_are_actually_present_after_loading()
    {
        SourceFile loaded = SourceRead.FromBytes(BuildAlienFile());

        // The kind name survives without a schema.
        KindId widget = KindTable.Lookup(in loaded.Table, Alien.Widget);
        Assert.False(widget.IsUnknown);
        Assert.False(KindTable.HasSchema(in loaded.Table, widget));

        // Structure under an uninterpretable node survived.
        NodeId root = loaded.Src.Root;
        NodeId node = SourceWalk.ChildAt(in loaded.Src, root, 0);
        Assert.Equal(widget, loaded.Src.Nodes[node.Index].Kind);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, SourceWalk.Payload(in loaded.Src, node).ToArray());
        Assert.Equal(2, SourceWalk.ChildCount(in loaded.Src, node));

        // The reference and its relation survived.
        RelationId wobbles = KindTable.LookupRelation(in loaded.Table, Alien.Wobbles);
        Assert.False(wobbles.IsUnknown);
        Assert.Equal(
            SourceWalk.ChildAt(in loaded.Src, node, 1),
            SourceWalk.Target(in loaded.Src, node, wobbles));

        // And the section from the future is intact.
        Assert.Equal(1, loaded.UnknownCount);
        Assert.Equal(UnknownSectionId, loaded.UnknownSections[0].Id);
        Assert.Equal(UnknownSectionBytes, loaded.UnknownSections[0].Bytes);
    }

    [Fact]
    public void A_document_with_schemas_round_trips_and_stays_interpretable()
    {
        SourceFile file = SourceFile.Create(32);
        SourceBuild.MintIdentity(ref file.Src);

        SourceId library = SourceId.Mint();
        QualifiedName sectionName = new QualifiedName(library, "section");
        QualifiedName citesName = new QualifiedName(library, "cites");

        KindId section = KindTable.Intern(ref file.Table, sectionName);
        RelationId cites = KindTable.InternRelation(ref file.Table, citesName);
        KindTable.DefineSchema(
            ref file.Table, section,
            StructuralFact.Declaration | StructuralFact.OpensScope,
            PayloadFormat.Utf8,
            DisplayShape.Block,
            0x11223344,
            new RelationId[] { cites });

        NodeId root = SourceBuild.AddRoot(ref file.Src, section);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("intro"));
        NodeId child = SourceBuild.AddChild(ref file.Src, root, section);
        SourceBuild.AddRef(ref file.Src, child, cites, root);
        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);

        byte[] bytes = SourceWrite.ToBytes(in file);
        SourceFile loaded = SourceRead.FromBytes(bytes);

        KindId loadedSection = KindTable.Lookup(in loaded.Table, sectionName);
        Assert.True(KindTable.HasSchema(in loaded.Table, loadedSection));

        KindSchema schema = KindTable.SchemaOf(in loaded.Table, loadedSection);
        Assert.True(schema.IsDeclaration);
        Assert.True(schema.OpensScope);
        Assert.Equal(PayloadFormat.Utf8, schema.Payload);
        Assert.Equal(0x11223344u, schema.DisplayColor);

        Assert.Equal("intro", Encoding.UTF8.GetString(SourceWalk.Payload(in loaded.Src, loaded.Src.Root)));
        Assert.Equal(bytes, SourceWrite.ToBytes(in loaded));
    }

    [Fact]
    public void Tombstones_survive_the_round_trip_so_later_ids_keep_their_meaning()
    {
        SourceFile file = SourceFile.Create(16);
        SourceBuild.MintIdentity(ref file.Src);

        NodeId root = SourceBuild.AddRoot(ref file.Src, file.Boot.SchemaSet);
        NodeId dead = SourceBuild.AddChild(ref file.Src, root, file.Boot.Hole);
        NodeId alive = SourceBuild.AddChild(ref file.Src, root, file.Boot.Hole);
        SourceBuild.SetPayload(ref file.Src, alive, Encoding.UTF8.GetBytes("still here"));
        SourceBuild.Delete(ref file.Src, dead);

        SourceFile loaded = SourceRead.FromBytes(SourceWrite.ToBytes(in file));

        Assert.Equal(file.Src.NodeCount, loaded.Src.NodeCount);
        Assert.False(loaded.Src.IsLive(dead));
        Assert.True(loaded.Src.WasAllocated(dead));
        Assert.True(loaded.Src.IsLive(alive));
        Assert.Equal("still here", Encoding.UTF8.GetString(SourceWalk.Payload(in loaded.Src, alive)));
    }

    [Fact]
    public void Unbound_references_keep_their_labels()
    {
        SourceFile file = SourceFile.Create(16);
        SourceBuild.MintIdentity(ref file.Src);

        RelationId decl = KindTable.InternRelation(ref file.Table, new QualifiedName(SourceId.Mint(), "decl"));
        NodeId root = SourceBuild.AddRoot(ref file.Src, file.Boot.SchemaSet);
        NodeId use = SourceBuild.AddChild(ref file.Src, root, file.Boot.Hole);
        SourceBuild.AddUnboundRef(ref file.Src, use, decl, Encoding.UTF8.GetBytes("nowhere"));

        SourceFile loaded = SourceRead.FromBytes(SourceWrite.ToBytes(in file));

        Ref edge = SourceWalk.RefAt(in loaded.Src, use, 0);
        Assert.True(edge.IsUnbound);
        Assert.Equal("nowhere", Encoding.UTF8.GetString(SourceWalk.Label(in loaded.Src, in edge)));
    }

    [Fact]
    public void The_document_identity_and_root_survive()
    {
        SourceFile file = SourceFile.Create(16);
        SourceBuild.MintIdentity(ref file.Src);
        SourceId minted = file.Src.Identity;

        SourceBuild.AddNode(ref file.Src, file.Boot.Hole);   // so the root is not slot 0
        NodeId root = SourceBuild.AddRoot(ref file.Src, file.Boot.SchemaSet);

        SourceFile loaded = SourceRead.FromBytes(SourceWrite.ToBytes(in file));

        Assert.Equal(minted, loaded.Src.Identity);
        Assert.Equal(root, loaded.Src.Root);
    }

    [Fact]
    public void An_empty_document_round_trips()
    {
        SourceFile file = SourceFile.Create(4);
        byte[] bytes = SourceWrite.ToBytes(in file);
        SourceFile loaded = SourceRead.FromBytes(bytes);

        Assert.Equal(0, loaded.Src.NodeCount);
        Assert.Equal(bytes, SourceWrite.ToBytes(in loaded));
    }

    [Fact]
    public void Bad_magic_is_refused()
    {
        byte[] junk = Encoding.UTF8.GetBytes("this is a text file, not a source at all");
        Assert.Throws<FormatCorruptException>(() => SourceRead.FromBytes(junk));
    }

    [Fact]
    public void A_truncated_file_is_refused_rather_than_read_past_the_end()
    {
        byte[] bytes = BuildAlienFile();
        byte[] cut = new byte[bytes.Length / 2];
        Array.Copy(bytes, cut, cut.Length);

        Assert.Throws<FormatCorruptException>(() => SourceRead.FromBytes(cut));
    }

    [Fact]
    public void A_newer_major_version_is_refused_instead_of_guessed_at()
    {
        byte[] bytes = BuildAlienFile();
        bytes[SourceWrite.Magic.Length] = (byte)(SourceWrite.FormatMajor + 1);

        FormatCorruptException error = Assert.Throws<FormatCorruptException>(
            () => SourceRead.FromBytes(bytes));
        Assert.Contains("major", error.Message);
    }

    [Fact]
    public void A_newer_minor_version_is_read_normally()
    {
        byte[] bytes = BuildAlienFile();
        bytes[SourceWrite.Magic.Length + 2] = (byte)(SourceWrite.FormatMinor + 7);

        SourceFile loaded = SourceRead.FromBytes(bytes);
        Assert.Equal(1, loaded.UnknownCount);
    }

    static byte[] BuildAlienFile()
    {
        SourceFile file = SourceFile.Create(16);
        file.Src.Identity = new SourceId(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

        KindId widget = KindTable.Intern(ref file.Table, Alien.Widget);
        KindId gizmo = KindTable.Intern(ref file.Table, Alien.Gizmo);
        RelationId wobbles = KindTable.InternRelation(ref file.Table, Alien.Wobbles);

        NodeId root = SourceBuild.AddRoot(ref file.Src, gizmo);
        NodeId node = SourceBuild.AddChild(ref file.Src, root, widget);
        SourceBuild.SetPayload(ref file.Src, node, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        NodeId a = SourceBuild.AddChild(ref file.Src, node, gizmo);
        SourceBuild.SetPayload(ref file.Src, a, new byte[] { 1, 2, 3, 4, 5 });
        NodeId b = SourceBuild.AddChild(ref file.Src, node, widget);

        SourceBuild.AddRef(ref file.Src, node, wobbles, b);
        SourceBuild.AddUnboundRef(ref file.Src, a, wobbles, Encoding.UTF8.GetBytes("something-missing"));

        // Omit schemas intentionally. Use a file without a checksum so injecting an unknown
        // section does not invalidate a digest.
        file.SectionOrder = new uint[]
        {
            (uint)SectionId.Identity,
            (uint)SectionId.KindNames,
            (uint)SectionId.RelationNames,
            (uint)SectionId.Nodes,
        };
        file.SectionOrderCount = 4;

        byte[] known = SourceWrite.ToBytes(in file);
        return SpliceUnknownSection(known);
    }

    static byte[] SpliceUnknownSection(byte[] bytes)
    {
        int countAt = SourceWrite.Magic.Length + 4;
        uint sectionCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            new ReadOnlySpan<byte>(bytes, countAt, 4));

        ByteWriter w = ByteWriter.Create(bytes.Length + 64);
        w.Bytes(bytes);
        w.U32(UnknownSectionId);
        w.U64((ulong)UnknownSectionBytes.Length);
        w.Bytes(UnknownSectionBytes);

        byte[] result = w.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            new Span<byte>(result, countAt, 4), sectionCount + 1);
        return result;
    }

    [Fact]
    public void The_section_table_is_walkable_without_reading_a_section()
    {
        byte[] bytes = BuildAlienFile();

        SectionSpan[] spans = null;
        int count = SourceRead.Sections(bytes, ref spans);

        SourceFile loaded = SourceRead.FromBytes(bytes);
        Assert.Equal(loaded.SectionOrderCount, count);

        long covered = 0;
        bool sawUnknown = false;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(loaded.SectionOrder[i], spans[i].Id);

            // The span really names those bytes and not an offset off by a header.
            Assert.True(spans[i].At + spans[i].Length <= bytes.Length);
            covered += spans[i].Length;

            if (spans[i].Id == UnknownSectionId)
            {
                sawUnknown = true;
                Assert.Equal(
                    UnknownSectionBytes,
                    new ReadOnlySpan<byte>(bytes, (int)spans[i].At, (int)spans[i].Length).ToArray());
            }
        }

        Assert.True(sawUnknown, "the future-version section was not in the table");
        Assert.True(covered < bytes.Length, "sections cannot account for the framing too");
    }

    [Fact]
    public void Walking_the_section_table_of_something_else_says_so()
    {
        SectionSpan[] spans = null;
        Assert.Throws<FormatCorruptException>(
            () => SourceRead.Sections(Encoding.UTF8.GetBytes("this is not a source at all"), ref spans));
    }
}
