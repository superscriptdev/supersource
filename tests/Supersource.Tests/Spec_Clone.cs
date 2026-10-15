using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

/// <summary>
/// Copying a loaded source in memory. A consumer that forks a document for draft editing should
/// not have to serialize it and read it back, nor re-intern its schemas by qualified name.
/// </summary>
public class Spec_Clone : IDisposable
{
    static class Lib
    {
        public static readonly SourceId Library = SourceId.Mint();
        public static readonly QualifiedName Note = new QualifiedName(Library, "note");
        public static readonly QualifiedName Section = new QualifiedName(Library, "section");
        public static readonly QualifiedName About = new QualifiedName(Library, "about");
    }

    readonly string dir;

    public Spec_Clone()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-clone-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    static SourceFile Built(out NodeId root, out NodeId first, out RelationId about)
    {
        SourceFile file = SourceFile.Create(64);

        KindId section = KindTable.Intern(ref file.Table, Lib.Section);
        KindId note = KindTable.Intern(ref file.Table, Lib.Note);
        about = KindTable.InternRelation(ref file.Table, Lib.About);
        KindTable.DefineRelation(ref file.Table, about, PayloadFormat.None, default);
        KindTable.DefineSchema(ref file.Table, section, StructuralFact.OpensScope,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new[] { about }, default);
        KindTable.DefineSchema(ref file.Table, note, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);

        SourceBuild.MintIdentity(ref file.Src);
        root = SourceBuild.AddRoot(ref file.Src, section);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("the west entrance"));

        first = NodeId.None;
        for (int i = 0; i < 6; i++)
        {
            Operation insert = default;
            insert.Kind = OpKind.Insert;
            insert.Parent = root;
            insert.Index = i;
            insert.NodeKind = note;
            insert.Value = Encoding.UTF8.GetBytes("note " + i);
            Log.Do(ref file.History, ref file.Src, insert);
            if (first.IsNone)
            {
                first = file.History.Entries[file.History.Count - 1].Op.Node;
            }
        }

        SourceBuild.AddRef(ref file.Src, root, about, first);
        return file;
    }

    static string TextOf(in SourceFile file, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, node));
    }

    [Fact]
    public void A_copy_writes_the_same_bytes_as_its_original()
    {
        SourceFile file = Built(out _, out _, out _);
        SourceFile copy = SourceFile.Clone(in file);

        Assert.Equal(SourceWrite.ToBytes(in file), SourceWrite.ToBytes(in copy));
    }

    [Fact]
    public void Editing_the_copy_leaves_the_original_alone()
    {
        SourceFile file = Built(out NodeId root, out NodeId first, out _);
        SourceFile copy = SourceFile.Clone(in file);

        SourceBuild.SetPayload(ref copy.Src, first, Encoding.UTF8.GetBytes("rewritten"));
        SourceBuild.AddChild(ref copy.Src, root, copy.Src.Nodes[first.Index].Kind);

        Assert.Equal("note 0", TextOf(in file, first));
        Assert.Equal("rewritten", TextOf(in copy, first));
        Assert.Equal(6, SourceWalk.ChildCount(in file.Src, root));
        Assert.Equal(7, SourceWalk.ChildCount(in copy.Src, root));
    }

    [Fact]
    public void Editing_the_original_leaves_the_copy_alone()
    {
        SourceFile file = Built(out NodeId root, out NodeId first, out _);
        SourceFile copy = SourceFile.Clone(in file);

        SourceBuild.SetPayload(ref file.Src, first, Encoding.UTF8.GetBytes("rewritten"));
        SourceBuild.Delete(ref file.Src, first);

        Assert.True(copy.Src.IsLive(first));
        Assert.Equal("note 0", TextOf(in copy, first));
        Assert.Equal(6, SourceWalk.ChildCount(in copy.Src, root));
    }

    [Fact]
    public void Handles_from_the_original_address_the_same_kinds_in_the_copy()
    {
        SourceFile file = Built(out NodeId root, out NodeId first, out RelationId about);
        SourceFile copy = SourceFile.Clone(in file);

        KindId section = KindTable.Lookup(in file.Table, Lib.Section);

        Assert.Equal(section, KindTable.Lookup(in copy.Table, Lib.Section));
        Assert.Equal(Lib.Section, KindTable.NameOf(in copy.Table, section));
        Assert.Equal(section, copy.Src.Nodes[root.Index].Kind);
        Assert.Equal(Lib.About, KindTable.RelationNameOf(in copy.Table, about));
        Assert.Equal(first, SourceWalk.Target(in copy.Src, root, about));

        KindSchema schema = KindTable.SchemaOf(in copy.Table, section);
        Assert.True(KindTable.HasSchema(in copy.Table, section));
        Assert.Equal(StructuralFact.OpensScope, schema.Facts);
        Assert.Equal(PayloadFormat.Utf8, schema.Payload);
    }

    [Fact]
    public void Interning_into_one_table_does_not_reach_the_other()
    {
        SourceFile file = Built(out _, out _, out _);
        SourceFile copy = SourceFile.Clone(in file);

        QualifiedName later = new QualifiedName(Lib.Library, "appendix");
        KindId added = KindTable.Intern(ref copy.Table, later);

        Assert.Equal(KindId.Unknown, KindTable.Lookup(in file.Table, later));
        Assert.Equal(added, KindTable.Lookup(in copy.Table, later));

        // The original can still grow, and must choose its slots without regard to the copy.
        KindId mine = KindTable.Intern(ref file.Table, new QualifiedName(Lib.Library, "footnote"));
        Assert.Equal(added.Value, mine.Value);
        Assert.Equal(KindId.Unknown, KindTable.Lookup(in copy.Table, new QualifiedName(Lib.Library, "footnote")));
    }

    [Fact]
    public void The_copy_undoes_as_far_as_the_original_without_moving_it()
    {
        SourceFile file = Built(out NodeId root, out _, out _);
        SourceFile copy = SourceFile.Clone(in file);

        Assert.Equal(file.History.Count, copy.History.Count);
        Assert.Equal(file.History.At, copy.History.At);

        for (int i = 0; i < 6; i++)
        {
            Assert.True(Log.Undo(ref copy.History, ref copy.Src));
        }

        Assert.False(Log.CanUndo(in copy.History));
        Assert.Equal(0, SourceWalk.ChildCount(in copy.Src, root));
        Assert.Equal(6, SourceWalk.ChildCount(in file.Src, root));
        Assert.True(Log.CanUndo(in file.History));
    }

    [Fact]
    public void An_undone_suffix_survives_the_copy_and_redoes()
    {
        SourceFile file = Built(out NodeId root, out _, out _);
        Log.Undo(ref file.History, ref file.Src);
        Log.Undo(ref file.History, ref file.Src);

        SourceFile copy = SourceFile.Clone(in file);

        Assert.Equal(4, SourceWalk.ChildCount(in copy.Src, root));
        Assert.True(Log.CanRedo(in copy.History));
        Assert.True(Log.Redo(ref copy.History, ref copy.Src));
        Assert.True(Log.Redo(ref copy.History, ref copy.Src));
        Assert.Equal(6, SourceWalk.ChildCount(in copy.Src, root));

        // The original kept its own position in the same history.
        Assert.Equal(4, SourceWalk.ChildCount(in file.Src, root));
    }

    [Fact]
    public void Captured_inverse_payloads_are_not_shared_between_the_copies()
    {
        SourceFile file = Built(out _, out NodeId first, out _);

        Operation modify = default;
        modify.Kind = OpKind.ModifyValue;
        modify.Node = first;
        modify.Value = Encoding.UTF8.GetBytes("changed");
        Log.Do(ref file.History, ref file.Src, modify);

        SourceFile copy = SourceFile.Clone(in file);

        int last = file.History.Count - 1;
        Assert.NotSame(file.History.Entries[last].Op.Value, copy.History.Entries[last].Op.Value);
        Assert.NotSame(file.History.Entries[last].Op.OldValue, copy.History.Entries[last].Op.OldValue);
        Assert.Equal(file.History.Entries[last].Op.Value, copy.History.Entries[last].Op.Value);

        // Undoing the copy restores the copy's own payload from its own captured bytes.
        Assert.True(Log.Undo(ref copy.History, ref copy.Src));
        Assert.Equal("note 0", TextOf(in copy, first));
        Assert.Equal("changed", TextOf(in file, first));
    }

    [Fact]
    public void Unknown_sections_are_carried_and_not_shared()
    {
        SourceFile file = Built(out _, out _, out _);
        file.UnknownSections = new UnknownSection[1];
        file.UnknownSections[0].Id = 0xC0FFEE;
        file.UnknownSections[0].Bytes = Encoding.UTF8.GetBytes("a later version wrote this");
        file.UnknownCount = 1;

        SourceFile copy = SourceFile.Clone(in file);

        Assert.Equal(1, copy.UnknownCount);
        Assert.Equal(0xC0FFEEu, copy.UnknownSections[0].Id);
        Assert.Equal(file.UnknownSections[0].Bytes, copy.UnknownSections[0].Bytes);
        Assert.NotSame(file.UnknownSections[0].Bytes, copy.UnknownSections[0].Bytes);
    }

    [Fact]
    public void A_copy_of_a_mapped_source_outlives_the_mapping()
    {
        string path = Path.Combine(dir, "mapped.ss");
        SourceFile built = Built(out _, out NodeId first, out _);
        SourceWrite.ToFile(in built, path);

        SourceFile mapped = SourceRead.FromFile(path);
        SourceFile copy = SourceFile.Clone(in mapped);
        SourceFile.Close(ref mapped);

        // Every payload the copy holds is its own; none points into the closed mapping.
        for (int i = 0; i < copy.Src.BlobSegments; i++)
        {
            Assert.False(copy.Src.Blob[i].IsMapped);
        }

        Assert.Equal("note 0", TextOf(in copy, first));
    }

    [Fact]
    public void A_copy_saves_a_complete_file_rather_than_appending_to_one_it_never_wrote()
    {
        string path = Path.Combine(dir, "saved.ss");
        SourceFile file = Built(out NodeId root, out _, out _);
        SourceWrite.ToFile(in file, path);

        SourceFile reopened = SourceRead.FromFile(path);
        SourceFile copy = SourceFile.Clone(in reopened);
        SourceFile.Close(ref reopened);

        Assert.Equal(0, copy.SavedAt);
        Assert.Equal(0ul, copy.SavedStamp);
        Assert.Null(copy.KindSlot);
        Assert.Equal(DigestKind.None, copy.DigestKind);

        SourceBuild.SetPayload(ref copy.Src, root, Encoding.UTF8.GetBytes("edited copy"));

        string other = Path.Combine(dir, "copy.ss");
        SourceWrite.ToFile(in copy, other);

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(other));
        Assert.Equal("edited copy", TextOf(in back, root));
        Assert.Equal(6, SourceWalk.ChildCount(in back.Src, root));
    }

    [Fact]
    public void A_copy_keeps_the_identity_it_copied()
    {
        SourceFile file = Built(out _, out _, out _);
        SourceFile copy = SourceFile.Clone(in file);

        Assert.Equal(file.Src.Identity, copy.Src.Identity);
    }
}
