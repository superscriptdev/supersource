using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_Journal : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName ChapterName = new QualifiedName(Library, "chapter");
    static readonly QualifiedName ParagraphName = new QualifiedName(Library, "paragraph");
    static readonly QualifiedName AboutName = new QualifiedName(Library, "about");

    readonly string dir = Path.Combine(Path.GetTempPath(), "ss-journal-" + Guid.NewGuid().ToString("n"));

    public Spec_Journal()
    {
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }

    string Path_(string name)
    {
        return System.IO.Path.Combine(dir, name);
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
        w.File = SourceFile.Create(64);

        w.Chapter = KindTable.Intern(ref w.File.Table, ChapterName);
        w.Paragraph = KindTable.Intern(ref w.File.Table, ParagraphName);
        w.About = KindTable.InternRelation(ref w.File.Table, AboutName);

        KindTable.DefineSchema(ref w.File.Table, w.Chapter, StructuralFact.OpensScope,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.File.Table, w.Paragraph, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About });

        SourceBuild.MintIdentity(ref w.File.Src);
        w.Root = SourceBuild.AddRoot(ref w.File.Src, w.Chapter);
        SourceBuild.SetPayload(ref w.File.Src, w.Root, Encoding.UTF8.GetBytes("a chapter"));

        // A few paragraphs so the file is bigger than one edit to it.
        for (int i = 0; i < 40; i++)
        {
            NodeId p = SourceBuild.AddChild(ref w.File.Src, w.Root, w.Paragraph);
            SourceBuild.SetPayload(ref w.File.Src, p, Encoding.UTF8.GetBytes(
                "paragraph " + i + ", which goes on for a while so that the file is worth not rewriting"));
        }

        // One edge, so the relation is in the file's table. A journal names relations by
        // slot, and a relation the file never wrote has no slot to name.
        ReadOnlySpan<long> children = SourceWalk.Children(in w.File.Src, w.Root);
        SourceBuild.AddRef(ref w.File.Src, new NodeId(children[1]), w.About, new NodeId(children[0]));
        return w;
    }

    [Fact]
    public void Generated_history_survives_journal_folding_and_undo()
    {
        World w = Build();
        string path = Path_("generated.ss");
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));
        Log.Do(ref w.File.History, ref w.File.Src, new Operation
        {
            Kind = OpKind.Insert,
            Parent = w.Root,
            Index = 0,
            NodeKind = w.Paragraph,
            NodeFlags = NodeFlags.Generated,
            Value = Encoding.UTF8.GetBytes("generated")
        });
        NodeId made = w.File.History.Entries[w.File.History.At - 1].Op.Node;
        Assert.True(SourceWrite.Replace(ref w.File, path, out _));
        Assert.Equal(1, Journals(path));
        SourceFile journalled = SourceRead.FromFile(path);
        SourceFile folded = SourceRead.FromBytes(SourceWrite.ToBytes(in journalled));
        Assert.Equal(1, folded.History.Count);
        Assert.True(folded.Src.Nodes[made.Index].IsGenerated);
        Assert.True(Log.Undo(ref folded.History, ref folded.Src));
        Assert.False(folded.Src.IsLive(made));
        Assert.True(Log.Redo(ref folded.History, ref folded.Src));
        Assert.True(folded.Src.Nodes[made.Index].IsGenerated);
        Assert.Equal("generated", TextOf(in folded.Src, made));
    }

    static NodeId Insert(ref World w, NodeId parent, int index, string text)
    {
        Operation op = default;
        op.Kind = OpKind.Insert;
        op.Parent = parent;
        op.Index = index;
        op.NodeKind = w.Paragraph;
        op.Value = Encoding.UTF8.GetBytes(text);

        Log.Do(ref w.File.History, ref w.File.Src, op, actor: 3);
        return w.File.History.Entries[w.File.History.Count - 1].Op.Node;
    }

    static void Splice(ref World w, NodeId node, int at, int removing, string inserting)
    {
        Operation op = default;
        op.Kind = OpKind.Splice;
        op.Node = node;
        op.Index = at;
        op.Count = removing;
        op.Value = Encoding.UTF8.GetBytes(inserting);

        Log.Do(ref w.File.History, ref w.File.Src, op, actor: 3);
    }

    static void Connect(ref World w, NodeId from, NodeId to)
    {
        Operation op = default;
        op.Kind = OpKind.Connect;
        op.Node = from;
        op.Relation = w.About;
        op.Target = to;

        Log.Do(ref w.File.History, ref w.File.Src, op, actor: 3);
    }

    static string TextOf(in Source src, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, node));
    }

    static int Journals(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        {
            SectionSpan[] spans = null;
            int count = SourceRead.Sections(stream, ref spans);
            int journals = 0;
            for (int i = 0; i < count; i++)
            {
                if (spans[i].Id == (uint)SectionId.Journal)
                {
                    journals++;
                }
            }
            return journals;
        }
    }

    [Fact]
    public void A_save_after_an_edit_appends_instead_of_rewriting()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        SourceFile open = SourceRead.FromBytes(File.ReadAllBytes(path));
        World live = w;
        live.File = open;
        long before = new FileInfo(path).Length;

        NodeId note = Insert(ref live, live.Root, 0, "x");

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        Assert.Null(refusal);

        long after = new FileInfo(path).Length;
        Assert.Equal(1, Journals(path));

        // The whole file is ~4 KiB; the edit costs under a tenth of it.
        Assert.InRange(after - before, 1, before / 10);

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal("x", TextOf(in back.Src, note));
        Assert.Equal(live.File.Src.NodeCount, back.Src.NodeCount);
    }

    [Fact]
    public void Many_appends_replay_in_order()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));

        NodeId[] added = new NodeId[6];
        for (int i = 0; i < added.Length; i++)
        {
            added[i] = Insert(ref live, live.Root, i, "note " + i);
            Splice(ref live, added[i], 0, 0, "! ");
            if (i > 0)
            {
                Connect(ref live, added[i], added[i - 1]);
            }

            string refusal;
            Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
            Assert.Equal(i + 1, Journals(path));
        }

        Assert.Equal(added.Length, Journals(path));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        for (int i = 0; i < added.Length; i++)
        {
            Assert.Equal("! note " + i, TextOf(in back.Src, added[i]));
        }

        ReadOnlySpan<Ref> refs = SourceWalk.References(in back.Src, added[3]);
        Assert.Equal(added[2], refs[0].Target);

        // The same source either way, which is the only claim that matters.
        Assert.Equal(SourceWrite.ToBytes(in live.File), SourceWrite.ToBytes(in back));
    }

    [Fact]
    public void Writing_it_whole_folds_the_journals_away()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));
        for (int i = 0; i < 5; i++)
        {
            Insert(ref live, live.Root, 0, "note " + i);
            string refusal;
            Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        }

        long journalled = new FileInfo(path).Length;

        SourceFile reopened = SourceRead.FromBytes(File.ReadAllBytes(path));
        SourceWrite.ToFile(in reopened, path);

        Assert.Equal(0, Journals(path));
        Assert.True(new FileInfo(path).Length < journalled);

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal(reopened.Src.NodeCount, back.Src.NodeCount);
        Assert.Equal(SourceWrite.ToBytes(in reopened), SourceWrite.ToBytes(in back));
    }

    [Fact]
    public void An_edit_that_skipped_the_log_forces_a_whole_write()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));

        NodeId logged = Insert(ref live, live.Root, 0, "logged");

        // Straight into the source, with no operation to describe it.
        NodeId quiet = SourceBuild.AddChild(ref live.File.Src, live.Root, live.Paragraph);
        SourceBuild.SetPayload(ref live.File.Src, quiet, Encoding.UTF8.GetBytes("nobody wrote this down"));

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        Assert.Equal(0, Journals(path));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal("logged", TextOf(in back.Src, logged));
        Assert.Equal("nobody wrote this down", TextOf(in back.Src, quiet));
    }

    [Fact]
    public void An_undo_forces_a_whole_write()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));

        NodeId first = Insert(ref live, live.Root, 0, "first");
        Insert(ref live, live.Root, 1, "second");
        Assert.True(Log.Undo(ref live.File.History, ref live.File.Src));

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        Assert.Equal(0, Journals(path));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal("first", TextOf(in back.Src, first));
    }

    [Fact]
    public void A_kind_the_file_does_not_name_forces_a_whole_write()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));

        KindId footnote = KindTable.Intern(ref live.File.Table, new QualifiedName(Library, "footnote"));

        Operation op = default;
        op.Kind = OpKind.Insert;
        op.Parent = live.Root;
        op.Index = 0;
        op.NodeKind = footnote;
        op.Value = Encoding.UTF8.GetBytes("see below");
        Log.Do(ref live.File.History, ref live.File.Src, op);
        NodeId note = live.File.History.Entries[live.File.History.Count - 1].Op.Node;

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        Assert.Equal(0, Journals(path));

        SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
        Assert.Equal("see below", TextOf(in back.Src, note));
        Assert.Equal(footnote, KindTable.Lookup(in back.Table, new QualifiedName(Library, "footnote")));
    }

    [Fact]
    public void A_source_with_no_file_yet_is_written_whole()
    {
        World w = Build();
        string path = Path_("new.ss");

        string refusal;
        Assert.True(SourceWrite.Replace(ref w.File, path, out refusal));
        Assert.Equal(0, Journals(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_journalled_file_still_verifies()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));
        Insert(ref live, live.Root, 0, "a note worth keeping");

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));

        ContentHash recorded, actual;
        Assert.True(SourceRead.Verify(path, out recorded, out actual));
        Assert.Equal(recorded, actual);
    }

    [Fact]
    public void A_flipped_bit_inside_a_journal_is_caught()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));
        Insert(ref live, live.Root, 0, "a note worth keeping");

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));

        // Somewhere inside the journal's entries, which is the last section.
        byte[] bytes = File.ReadAllBytes(path);
        bytes[bytes.Length - 40] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        ContentHash recorded, actual;
        Assert.False(SourceRead.Verify(path, out recorded, out actual));
    }

    [Fact]
    public void An_append_moves_the_digest_the_next_saver_checks()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        SourceFile mine = SourceRead.FromBytes(File.ReadAllBytes(path));

        World theirs = w;
        theirs.File = SourceRead.FromBytes(File.ReadAllBytes(path));
        Insert(ref theirs, theirs.Root, 0, "mine first");

        string refusal;
        Assert.True(SourceWrite.Replace(ref theirs.File, path, out refusal));
        Assert.Equal(1, Journals(path));

        Assert.False(SourceWrite.Replace(ref mine, path, out refusal));
        Assert.Equal("the file has been written since it was read", refusal);
    }

    [Fact]
    public void A_mapped_source_can_be_appended_to_underneath_itself()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromFile(path);
        try
        {
            ReadOnlySpan<long> children = SourceWalk.Children(in live.File.Src, live.Root);
            NodeId first = new NodeId(children[0]);
            string was = TextOf(in live.File.Src, first);

            NodeId note = Insert(ref live, live.Root, 0, "written while mapped");

            string refusal;
            Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
            Assert.Equal(1, Journals(path));

            // The mapped payloads still read, because nothing before the append moved.
            Assert.Equal(was, TextOf(in live.File.Src, first));

            SourceFile back = SourceRead.FromBytes(File.ReadAllBytes(path));
            Assert.Equal("written while mapped", TextOf(in back.Src, note));
            Assert.Equal(was, TextOf(in back.Src, first));
        }
        finally
        {
            SourceFile.Close(ref live.File);
        }
    }

    [Fact]
    public void Saving_again_with_nothing_to_say_writes_nothing()
    {
        World w = Build();
        string path = Path_("chapter.ss");
        SourceWrite.ToFile(in w.File, path);

        World live = w;
        live.File = SourceRead.FromBytes(File.ReadAllBytes(path));
        Insert(ref live, live.Root, 0, "once");

        string refusal;
        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        long after = new FileInfo(path).Length;

        Assert.True(SourceWrite.Replace(ref live.File, path, out refusal));
        Assert.Equal(after, new FileInfo(path).Length);
        Assert.Equal(1, Journals(path));
    }
}
