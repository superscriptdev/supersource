using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

/// <summary>
/// A file holds a state, not the edits that produced it. What a session can undo is the
/// session's; what reaches the disk is the graph, plus at most the operations appended since the
/// last full write.
/// </summary>
public class Spec_SavedState : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName ChapterName = new QualifiedName(Library, "chapter");
    static readonly QualifiedName ParagraphName = new QualifiedName(Library, "paragraph");
    static readonly QualifiedName AboutName = new QualifiedName(Library, "about");

    readonly string dir = Path.Combine(Path.GetTempPath(), "ss-state-" + Guid.NewGuid().ToString("n"));

    public Spec_SavedState()
    {
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }

    struct World
    {
        public SourceFile File;
        public KindId Chapter, Paragraph;
        public RelationId About;
        public NodeId Root;
    }

    static World Build()
    {
        World w = default;
        w.File = SourceFile.Create(32);

        w.Chapter = KindTable.Intern(ref w.File.Table, ChapterName);
        w.Paragraph = KindTable.Intern(ref w.File.Table, ParagraphName);
        w.About = KindTable.InternRelation(ref w.File.Table, AboutName);

        KindTable.DefineSchema(ref w.File.Table, w.Chapter, StructuralFact.None,
            PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.File.Table, w.Paragraph, StructuralFact.None,
            PayloadFormat.Text, new RelationId[] { w.About });

        SourceBuild.MintIdentity(ref w.File.Src);
        w.Root = SourceBuild.AddRoot(ref w.File.Src, w.Chapter);
        SourceBuild.SetPayload(ref w.File.Src, w.Root, Encoding.UTF8.GetBytes("one"));
        return w;
    }

    static NodeId Insert(ref World w, NodeId parent, int index, string text)
    {
        Operation op = default;
        op.Kind = OpKind.Insert;
        op.Parent = parent;
        op.Index = index;
        op.NodeKind = w.Paragraph;
        op.Value = Encoding.UTF8.GetBytes(text);

        Log.Do(ref w.File.Log, ref w.File.Src, op);
        return w.File.Log.Entries[w.File.Log.Count - 1].Op.Node;
    }

    static void Splice(ref World w, NodeId node, int at, int removing, string inserting)
    {
        Operation op = default;
        op.Kind = OpKind.Splice;
        op.Node = node;
        op.Index = at;
        op.Count = removing;
        op.Value = Encoding.UTF8.GetBytes(inserting);

        Log.Do(ref w.File.Log, ref w.File.Src, op);
    }

    static void Do(ref World w, Operation op)
    {
        Log.Do(ref w.File.Log, ref w.File.Src, op);
    }

    static string TextOf(in Source src, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, node));
    }

    static bool Holds(byte[] haystack, string text)
    {
        return haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
    }

    static SectionSpan[] Spans(byte[] bytes, out int count)
    {
        SectionSpan[] spans = null;
        count = SourceRead.Sections(bytes, ref spans);
        return spans;
    }

    static int CountOf(byte[] bytes, SectionId id)
    {
        SectionSpan[] spans = Spans(bytes, out int count);
        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (spans[i].Id == (uint)id)
            {
                found++;
            }
        }
        return found;
    }

    /// <summary>
    /// Bytes as a writer from before the history section was retired produced them: the same file
    /// with a section 5 appended and the section count raised to match.
    /// </summary>
    static byte[] WithRetiredHistory(byte[] bytes, byte[] content)
    {
        byte[] grown = new byte[bytes.Length + 12 + content.Length];
        bytes.CopyTo(grown, 0);

        Span<byte> header = grown.AsSpan(bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)SectionId.RetiredHistory);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(4), (ulong)content.Length);
        content.CopyTo(grown, bytes.Length + 12);

        int countAt = SourceWrite.Magic.Length + 2 + 2;
        uint sections = BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(countAt));
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(countAt), sections + 1);
        return grown;
    }

    [Fact]
    public void A_reopened_file_has_the_edits_applied_and_nothing_to_undo()
    {
        World w = Build();
        NodeId first = Insert(ref w, w.Root, 0, "the first paragraph");
        Splice(ref w, first, 4, 5, "second");

        SourceFile reloaded = SourceRead.FromBytes(SourceWrite.ToBytes(in w.File));

        Assert.Equal("the second paragraph", TextOf(in reloaded.Src, first));
        Assert.Equal(0, reloaded.Log.Count);
        Assert.False(Log.CanUndo(in reloaded.Log));
    }

    [Fact]
    public void A_save_writes_no_history_section()
    {
        World w = Build();
        Insert(ref w, w.Root, 0, "alpha");

        byte[] bytes = SourceWrite.ToBytes(in w.File);

        Assert.Equal(0, CountOf(bytes, SectionId.RetiredHistory));
    }

    [Fact]
    public void A_save_leaves_the_session_able_to_undo_past_it()
    {
        World w = Build();
        string path = Path.Combine(dir, "chapter.ss");
        NodeId node = Insert(ref w, w.Root, 0, "draft");
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        Splice(ref w, node, 0, 5, "final");
        Assert.True(Log.Undo(ref w.File.Log, ref w.File.Src));
        Assert.True(Log.Undo(ref w.File.Log, ref w.File.Src));
        Assert.False(w.File.Src.IsLive(node));

        Assert.True(SourceWrite.Replace(ref w.File, path, out _));
        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.False(back.Src.IsLive(node));
    }

    [Fact]
    public void Text_an_edit_removed_does_not_reach_a_fully_written_file()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha secret");
        Splice(ref w, node, 5, 7, "");

        byte[] bytes = SourceWrite.ToBytes(in w.File);

        SourceFile back = SourceRead.FromBytes(bytes);
        Assert.Equal("alpha", TextOf(in back.Src, node));
        Assert.False(Holds(bytes, "secret"));
    }

    [Fact]
    public void A_deleted_subtree_is_written_as_tombstones_that_keep_their_ids()
    {
        World w = Build();
        NodeId doomed = Insert(ref w, w.Root, 0, "doomed paragraph");
        NodeId inside = Insert(ref w, doomed, 0, "doomed child");
        NodeId survivor = Insert(ref w, w.Root, 1, "survivor");

        Operation connect = default;
        connect.Kind = OpKind.Connect;
        connect.Node = survivor;
        connect.Relation = w.About;
        connect.Target = inside;
        Do(ref w, connect);

        Operation delete = default;
        delete.Kind = OpKind.Delete;
        delete.Node = doomed;
        Do(ref w, delete);

        byte[] bytes = SourceWrite.ToBytes(in w.File);
        Assert.False(Holds(bytes, "doomed"));

        SourceFile back = SourceRead.FromBytes(bytes);
        Assert.True(SourceValidate.Check(in back, out _));
        Assert.False(back.Src.IsLive(doomed));
        Assert.False(back.Src.IsLive(inside));
        Assert.True(back.Src.Nodes[inside.Index].Kind.IsUnknown);

        // The reference is dangling, not unbound: it still names what it lost.
        Assert.Equal(inside, SourceWalk.RefAt(in back.Src, survivor, 0).Target);

        // And the slot stays taken.
        NodeId SourceAdd(ref SourceFile file)
        {
            Operation op = default;
            op.Kind = OpKind.Insert;
            op.Parent = file.Src.Root;
            op.NodeKind = w.Paragraph;
            Log.Do(ref file.Log, ref file.Src, op);
            return file.Log.Entries[file.Log.Count - 1].Op.Node;
        }
        NodeId fresh = SourceAdd(ref back);
        Assert.True(fresh.Index > inside.Index);

        // Writing it again is a fixpoint.
        SourceFile again = SourceRead.FromBytes(bytes);
        Assert.Equal(bytes, SourceWrite.ToBytes(in again));
    }

    [Fact]
    public void A_session_keeps_a_deleted_subtree_for_its_own_undo()
    {
        World w = Build();
        NodeId doomed = Insert(ref w, w.Root, 0, "kept for undo");
        Insert(ref w, doomed, 0, "and its child");

        Operation delete = default;
        delete.Kind = OpKind.Delete;
        delete.Node = doomed;
        Do(ref w, delete);

        string path = Path.Combine(dir, "undo.ss");
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));
        Assert.False(Holds(File.ReadAllBytes(path), "kept for undo"));

        Assert.True(Log.Undo(ref w.File.Log, ref w.File.Src));
        Assert.Equal("kept for undo", TextOf(in w.File.Src, doomed));
        Assert.Equal(1, SourceWalk.ChildCount(in w.File.Src, doomed));
    }

    [Fact]
    public void A_journal_records_operations_forward_and_keeps_nothing_they_replaced()
    {
        World w = Build();
        string path = Path.Combine(dir, "journal.ss");
        NodeId node = Insert(ref w, w.Root, 0, "draft text");

        Operation connect = default;
        connect.Kind = OpKind.Connect;
        connect.Node = node;
        connect.Relation = w.About;
        connect.Target = NodeId.None;
        connect.Value = Encoding.UTF8.GetBytes("a label to lose");
        Do(ref w, connect);
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        Splice(ref w, node, 0, 5, "final");
        Operation disconnect = default;
        disconnect.Kind = OpKind.Disconnect;
        disconnect.Node = node;
        disconnect.Index = 0;
        Do(ref w, disconnect);
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        byte[] bytes = File.ReadAllBytes(path);
        SectionSpan[] spans = Spans(bytes, out int count);
        int journals = 0;
        for (int i = 0; i < count; i++)
        {
            if (spans[i].Id != (uint)SectionId.Journal)
            {
                continue;
            }
            journals++;
            byte[] journal = bytes.AsSpan((int)spans[i].At, (int)spans[i].Length).ToArray();
            Assert.True(Holds(journal, "final"));
            Assert.False(Holds(journal, "draft"));
            Assert.False(Holds(journal, "a label to lose"));
        }
        Assert.Equal(1, journals);

        SourceFile reopened = SourceRead.FromBytes(bytes);
        Assert.Equal("final text", TextOf(in reopened.Src, node));
        Assert.Equal(0, SourceWalk.RefCount(in reopened.Src, node));
        Assert.Equal(0, reopened.Log.Count);

        // The next full write is what removes the saved graph's older bytes.
        Assert.False(Holds(SourceWrite.ToBytes(in reopened), "draft"));
    }

    [Fact]
    public void A_retired_history_section_is_read_past_and_dropped_on_save()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha");
        byte[] old = WithRetiredHistory(SourceWrite.ToBytes(in w.File),
            Encoding.UTF8.GetBytes("every edit anybody ever made"));

        SourceFile loaded = SourceRead.FromBytes(old);
        Assert.Equal("alpha", TextOf(in loaded.Src, node));
        Assert.Equal(0, loaded.Log.Count);
        Assert.Equal(0, loaded.UnknownCount);

        byte[] saved = SourceWrite.ToBytes(in loaded);
        Assert.Equal(0, CountOf(saved, SectionId.RetiredHistory));
        Assert.False(Holds(saved, "every edit anybody ever made"));
    }

    [Fact]
    public void A_file_still_carrying_retired_history_is_written_in_full_on_its_next_save()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha");
        string path = Path.Combine(dir, "old.ss");
        File.WriteAllBytes(path, WithRetiredHistory(SourceWrite.ToBytes(in w.File), new byte[] { 1, 2, 3 }));

        SourceFile loaded = SourceRead.FromBytes(File.ReadAllBytes(path));
        Operation op = default;
        op.Kind = OpKind.ModifyValue;
        op.Node = node;
        op.Value = Encoding.UTF8.GetBytes("beta");
        Log.Do(ref loaded.Log, ref loaded.Src, op);

        Assert.True(SourceWrite.Replace(ref loaded, path, out _));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(0, CountOf(bytes, SectionId.RetiredHistory));
        Assert.Equal(0, CountOf(bytes, SectionId.Journal));
        SourceFile back = SourceRead.FromBytes(bytes);
        Assert.Equal("beta", TextOf(in back.Src, node));
    }

    [Fact]
    public void A_deletion_taken_back_as_a_new_edit_survives_a_journal()
    {
        World w = Build();
        string path = Path.Combine(dir, "restore.ss");
        NodeId section = Insert(ref w, w.Root, 0, "a section");
        NodeId inside = Insert(ref w, section, 0, "its paragraph");
        NodeId elsewhere = Insert(ref w, w.Root, 1, "elsewhere");
        Operation link = default;
        link.Kind = OpKind.Connect;
        link.Node = inside;
        link.Relation = w.About;
        link.Target = elsewhere;
        Do(ref w, link);
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        Operation delete = default;
        delete.Kind = OpKind.Delete;
        delete.Node = section;
        Do(ref w, delete);
        Operation removal = w.File.Log.Entries[w.File.Log.Count - 1].Op;

        // Not an undo: the inverse applied as an edit of its own, as a collaborator's client or a
        // revert would, so it reaches the journal carrying a capture the journal cannot store.
        Do(ref w, OpApply.Invert(in removal));
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.True(back.Src.IsLive(section));
        Assert.Equal("its paragraph", TextOf(in back.Src, inside));
        Assert.Equal(section, SourceWalk.Parent(in back.Src, inside));
        Assert.Equal(elsewhere, SourceWalk.RefAt(in back.Src, inside, 0).Target);
    }

    [Fact]
    public void A_version_1_journal_is_still_read()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "before");
        byte[] bytes = SourceWrite.ToBytes(in w.File);

        // One entry in the old layout: an actor, an order, wire value 3 (Rename), node flags, the
        // old parent and index, and the replaced payload, none of which replay reads any more.
        MemoryStream content = new MemoryStream();
        ByteWriter j = ByteWriter.Over(content);
        j.U8(1);
        j.UVar(1);
        j.SVar(7);
        j.UVar(0);
        j.U8(3);
        j.U8(0);
        j.SVar(node.Index);
        j.SVar(-1);
        j.SVar(0);
        j.SVar(-1);
        j.SVar(0);
        j.SVar(0);
        j.SVar(-1);
        j.SVar(-1);
        j.SVar(-1);
        byte[] after = Encoding.UTF8.GetBytes("after");
        j.UVar((ulong)after.Length);
        j.Bytes(after);
        byte[] removed = Encoding.UTF8.GetBytes("before");
        j.UVar((ulong)removed.Length);
        j.Bytes(removed);
        j.Bytes(new byte[JournalCodec.DigestBytes]);
        j.Flush();

        byte[] old = WithSection(bytes, SectionId.Journal, content.ToArray());
        SourceFile loaded = SourceRead.FromBytes(old);
        Assert.Equal("after", TextOf(in loaded.Src, node));
    }

    /// <summary>A file with one more section appended and the section count raised to match.</summary>
    static byte[] WithSection(byte[] bytes, SectionId id, byte[] content)
    {
        byte[] grown = new byte[bytes.Length + 12 + content.Length];
        bytes.CopyTo(grown, 0);

        Span<byte> header = grown.AsSpan(bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)id);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(4), (ulong)content.Length);
        content.CopyTo(grown, bytes.Length + 12);

        int countAt = SourceWrite.Magic.Length + 2 + 2;
        uint sections = BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(countAt));
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(countAt), sections + 1);
        return grown;
    }
}
