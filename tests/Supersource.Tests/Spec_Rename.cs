using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Resolve;
using Xunit;

namespace Supersource.Tests;

public class Spec_Rename
{
    static readonly SourceId OldLibrary =
        new SourceId(0x1111111111111111UL, 0x2222222222222222UL);
    static readonly SourceId NewLibrary =
        new SourceId(0x3333333333333333UL, 0x4444444444444444UL);

    static QualifiedName Old(string name) { return new QualifiedName(OldLibrary, name); }
    static QualifiedName New(string name) { return new QualifiedName(NewLibrary, name); }

    struct World
    {
        public SourceFile File;
        public KindId Note, Tag;
        public RelationId Tagged;
        public NodeId Root, TagNode;
    }

    static World Build()
    {
        World w = default;
        w.File = SourceFile.Create(32);
        SourceBuild.MintIdentity(ref w.File.Src);

        w.Tagged = KindTable.InternRelation(ref w.File.Table, Old("tagged"));
        w.Note = KindTable.Intern(ref w.File.Table, Old("note"));
        w.Tag = KindTable.Intern(ref w.File.Table, Old("tag"));

        KindTable.DefineSchema(ref w.File.Table, w.Note,
            StructuralFact.None, PayloadFormat.Utf8, DisplayShape.Block, 0xAABBCCFF,
            new ReadOnlySpan<RelationId>(new RelationId[] { w.Tagged }));

        KindTable.DefineSchema(ref w.File.Table, w.Tag,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Leaf, 0xCC7799FF,
            default);

        w.Root = SourceBuild.AddRoot(ref w.File.Src, w.Note);
        SourceBuild.SetPayload(ref w.File.Src, w.Root, Encoding.UTF8.GetBytes("a note"));

        w.TagNode = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Tag);
        SourceBuild.SetPayload(ref w.File.Src, w.TagNode, Encoding.UTF8.GetBytes("key-term"));

        SourceBuild.AddRef(ref w.File.Src, w.Root, w.Tagged, w.TagNode);

        KindSchemaCodec.Write(ref w.File.Src, in w.File.Table, in w.File.Boot, w.Root);
        return w;
    }

    static SourceFile Reopen(in SourceFile file)
    {
        return SourceRead.FromBytes(SourceWrite.ToBytes(in file));
    }

    static int Findings(in SourceFile file, FindingKind kind)
    {
        Finding[] found = null;
        int count = RelationCheck.Check(in file.Src, in file.Table, ref found);

        int matching = 0;
        for (int i = 0; i < count; i++)
        {
            if (found[i].Kind == kind)
            {
                matching++;
            }
        }
        return matching;
    }

    [Fact]
    public void Renaming_a_library_without_migrating_fails_silently_rather_than_loudly()
    {
        World w = Build();
        SourceFile reopened = Reopen(in w.File);

        // A build whose libraries have moved on mints the NEW names.
        KindId minted = KindTable.Intern(ref reopened.Table, New("tag"));
        RelationId mintedRelation = KindTable.InternRelation(ref reopened.Table, New("tagged"));

        // Nothing is wrong with the source. Nothing at all.
        Assert.Equal(0, Findings(in reopened, FindingKind.UnresolvedKind));
        Assert.Equal(0, Findings(in reopened, FindingKind.UndeclaredRelation));
        Assert.Equal(SourceWrite.ToBytes(in w.File), SourceWrite.ToBytes(in reopened));

        // And yet the tag this build knows how to draw appears nowhere in it.
        int tags = 0;
        for (int i = 0; i < reopened.Src.NodeCount; i++)
        {
            if (reopened.Src.Nodes[i].Kind == minted)
            {
                tags++;
            }
        }
        Assert.Equal(0, tags);

        // Nor does the edge, though one is plainly there under its old name.
        ReadOnlySpan<Ref> refs = SourceWalk.References(in reopened.Src, w.Root);
        Assert.Single(refs.ToArray());
        Assert.NotEqual(mintedRelation, refs[0].Relation);
    }

    [Fact]
    public void A_renamed_kind_keeps_its_nodes_its_ids_and_its_schema()
    {
        World w = Build();

        RenameResult result = KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Kind(Old("note"), New("note")),
            Rename.Kind(Old("tag"), New("tag")),
            Rename.Relation(Old("tagged"), New("tagged")),
        });

        Assert.Equal(3, result.Renamed);
        Assert.Equal(3, result.SchemasRewritten);

        SourceFile back = Reopen(in w.File);

        // The new names are what the source now declares...
        KindId note = KindTable.Lookup(in back.Table, New("note"));
        Assert.False(note.IsUnknown);
        Assert.True(KindTable.HasSchema(in back.Table, note));

        // ...the old ones are gone...
        Assert.True(KindTable.Lookup(in back.Table, Old("note")).IsUnknown);

        // ...the nodes kept their ids and their payloads...
        Assert.Equal(note, back.Src.Nodes[w.Root.Index].Kind);
        Assert.Equal("a note",
            Encoding.UTF8.GetString(SourceWalk.Payload(in back.Src, w.Root)));

        // ...and nothing reports as uninterpretable or undeclared.
        Assert.Equal(0, Findings(in back, FindingKind.UnresolvedKind));
        Assert.Equal(0, Findings(in back, FindingKind.UndeclaredRelation));
    }

    [Fact]
    public void A_renamed_kind_keeps_the_facts_its_schema_stated()
    {
        World w = Build();
        KindSchema before = KindTable.SchemaOf(in w.File.Table, w.Tag);

        KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Kind(Old("tag"), New("tag")),
        });

        SourceFile back = Reopen(in w.File);
        KindSchema after = KindTable.SchemaOf(
            in back.Table, KindTable.Lookup(in back.Table, New("tag")));

        Assert.True(after.IsDeclaration);
        Assert.Equal(before.Facts, after.Facts);
        Assert.Equal(before.Payload, after.Payload);
        Assert.Equal(before.Shape, after.Shape);
        Assert.Equal(before.DisplayColor, after.DisplayColor);
    }

    [Fact]
    public void A_renamed_relation_keeps_the_edges_that_used_it()
    {
        World w = Build();

        KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Relation(Old("tagged"), New("tagged")),
        });

        SourceFile back = Reopen(in w.File);

        RelationId tagged = KindTable.LookupRelation(in back.Table, New("tagged"));
        Assert.False(tagged.IsUnknown);

        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, w.Root);
        Assert.Single(refs.ToArray());
        Assert.Equal(tagged, refs[0].Relation);
        Assert.Equal(w.TagNode, refs[0].Target);
    }

    [Fact]
    public void When_both_names_are_present_the_handles_merge_and_nodes_move()
    {
        World w = Build();

        // Something already used the new name — a paste from a migrated source, say.
        KindId already = KindTable.Intern(ref w.File.Table, New("tag"));
        KindTable.DefineSchema(ref w.File.Table, already,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Leaf, 0xCC7799FF,
            default);
        NodeId newer = SourceBuild.AddChild(ref w.File.Src, w.Root, already);

        RenameResult result = KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Kind(Old("tag"), New("tag")),
        });

        Assert.Equal(1, result.NodesMoved);

        // Both tag nodes now carry one handle, and it is the surviving one.
        Assert.Equal(already, w.File.Src.Nodes[w.TagNode.Index].Kind);
        Assert.Equal(already, w.File.Src.Nodes[newer.Index].Kind);
    }

    [Fact]
    public void A_name_the_document_never_used_is_skipped_rather_than_reported()
    {
        World w = Build();

        RenameResult result = KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Kind(Old("nothing-called-this"), New("nothing-called-this")),
        });

        Assert.Equal(0, result.Renamed);
        Assert.Equal(0, result.SchemasRewritten);
    }

    [Fact]
    public void Tombstones_are_renamed_too()
    {
        World w = Build();

        NodeId doomed = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Tag);
        SourceBuild.Delete(ref w.File.Src, doomed);
        Assert.False(w.File.Src.IsLive(doomed));

        KindId already = KindTable.Intern(ref w.File.Table, New("tag"));
        KindRename.Apply(ref w.File, new Rename[]
        {
            Rename.Kind(Old("tag"), New("tag")),
        });

        Assert.Equal(already, w.File.Src.Nodes[doomed.Index].Kind);
    }

    [Fact]
    public void Running_a_rename_twice_changes_nothing_the_second_time()
    {
        World w = Build();

        Rename[] renames = { Rename.Kind(Old("note"), New("note")) };

        KindRename.Apply(ref w.File, renames);
        byte[] once = SourceWrite.ToBytes(in w.File);

        RenameResult second = KindRename.Apply(ref w.File, renames);
        byte[] twice = SourceWrite.ToBytes(in w.File);

        Assert.Equal(0, second.Renamed);
        Assert.Equal(once, twice);
    }
}
