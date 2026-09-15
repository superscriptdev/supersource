using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_History
{
    static readonly SourceId Library = SourceId.Mint();
    static readonly QualifiedName ChapterName = new QualifiedName(Library, "chapter");
    static readonly QualifiedName ParagraphName = new QualifiedName(Library, "paragraph");
    static readonly QualifiedName AboutName = new QualifiedName(Library, "about");

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

        KindTable.DefineSchema(ref w.File.Table, w.Chapter, StructuralFact.OpensScope,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.File.Table, w.Paragraph, StructuralFact.None,
            PayloadFormat.Utf8, DisplayShape.Block, 0, new RelationId[] { w.About });

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

        Log.Do(ref w.File.History, ref w.File.Src, op, actor: 7);
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

        Log.Do(ref w.File.History, ref w.File.Src, op, actor: 7);
    }

    static string TextOf(in Source src, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, node));
    }

    static SourceFile SaveAndLoad(in World w)
    {
        return SourceRead.FromBytes(SourceWrite.ToBytes(in w.File));
    }

    [Fact]
    public void Undo_still_works_after_a_save_and_a_load()
    {
        World w = Build();
        NodeId first = Insert(ref w, w.Root, 0, "the first paragraph");
        Splice(ref w, first, 4, 5, "second");

        Assert.Equal("the second paragraph", TextOf(in w.File.Src, first));

        SourceFile reloaded = SaveAndLoad(in w);
        Assert.Equal("the second paragraph", TextOf(in reloaded.Src, first));

        Assert.True(Log.Undo(ref reloaded.History, ref reloaded.Src));
        Assert.Equal("the first paragraph", TextOf(in reloaded.Src, first));

        Assert.True(Log.Undo(ref reloaded.History, ref reloaded.Src));
        Assert.False(reloaded.Src.IsLive(first));

        // And forward again, because the log is walkable in both directions.
        Assert.True(Log.Redo(ref reloaded.History, ref reloaded.Src));
        Assert.True(Log.Redo(ref reloaded.History, ref reloaded.Src));
        Assert.Equal("the second paragraph", TextOf(in reloaded.Src, first));
    }

    [Fact]
    public void Every_entry_survives_the_save()
    {
        World w = Build();
        NodeId first = Insert(ref w, w.Root, 0, "alpha");
        NodeId second = Insert(ref w, w.Root, 1, "beta");
        Splice(ref w, second, 0, 4, "gamm");

        SourceFile reloaded = SaveAndLoad(in w);

        Assert.Equal(w.File.History.Count, reloaded.History.Count);
        for (int i = 0; i < w.File.History.Count; i++)
        {
            Entry mine = w.File.History.Entries[i];
            Entry back = reloaded.History.Entries[i];

            Assert.Equal(mine.Op.Kind, back.Op.Kind);
            Assert.Equal(mine.Op.Node, back.Op.Node);
            Assert.Equal(mine.Op.Parent, back.Op.Parent);
            Assert.Equal(mine.Op.Index, back.Op.Index);
            Assert.Equal(mine.Actor, back.Actor);
            Assert.Equal(mine.Order, back.Order);
            Assert.Equal(mine.Op.OldValue ?? Array.Empty<byte>(), back.Op.OldValue);
            Assert.Equal(mine.Op.Value ?? Array.Empty<byte>(), back.Op.Value);
        }

        Assert.Equal(w.Paragraph, reloaded.History.Entries[0].Op.NodeKind);
        Assert.True(reloaded.Src.IsLive(first));
    }

    [Fact]
    public void Loading_a_history_does_not_apply_it_again()
    {
        World w = Build();
        Insert(ref w, w.Root, 0, "alpha");
        Insert(ref w, w.Root, 1, "beta");

        long nodes = w.File.Src.NodeCount;
        int children = SourceWalk.ChildCount(in w.File.Src, w.Root);

        SourceFile reloaded = SaveAndLoad(in w);

        Assert.Equal(nodes, reloaded.Src.NodeCount);
        Assert.Equal(children, SourceWalk.ChildCount(in reloaded.Src, w.Root));

        // And a second save is a fixpoint rather than a doubling.
        byte[] once = SourceWrite.ToBytes(in reloaded);
        SourceFile again = SourceRead.FromBytes(once);
        Assert.Equal(once, SourceWrite.ToBytes(in again));
    }

    [Fact]
    public void Edits_to_generated_content_persist_and_remain_undoable()
    {
        World w = Build();
        NodeId authored = Insert(ref w, w.Root, 0, "authored");
        NodeId generated = Insert(ref w, w.Root, 1, "generated");

        w.File.Src.Nodes[generated.Index].Flags |= NodeFlags.Generated;

        Assert.Equal(2, w.File.History.Count);

        SourceFile reloaded = SaveAndLoad(in w);

        Assert.Equal(2, reloaded.History.Count);
        Assert.Equal(authored, reloaded.History.Entries[0].Op.Node);

        Assert.True(reloaded.Src.IsLive(generated));
        Assert.Equal(generated, reloaded.History.Entries[1].Op.Node);
        Assert.True(Log.Undo(ref reloaded.History, ref reloaded.Src));
        Assert.False(reloaded.Src.IsLive(generated));
        Assert.True(reloaded.Src.IsLive(authored));
        Assert.True(Log.Redo(ref reloaded.History, ref reloaded.Src));
        Assert.True(reloaded.Src.IsLive(generated));
        Assert.Equal("generated", TextOf(in reloaded.Src, generated));
        byte[] saved = SourceWrite.ToBytes(in reloaded);
        SourceFile again = SourceRead.FromBytes(saved);
        Assert.Equal(2, again.History.Count);
        Assert.Equal(saved, SourceWrite.ToBytes(in again));
    }

    [Fact]
    public void Undone_entries_are_not_written()
    {
        World w = Build();
        Insert(ref w, w.Root, 0, "alpha");
        NodeId second = Insert(ref w, w.Root, 1, "beta");

        Assert.True(Log.Undo(ref w.File.History, ref w.File.Src));
        Assert.False(w.File.Src.IsLive(second));
        Assert.Equal(2, w.File.History.Count);
        Assert.Equal(1, w.File.History.At);

        SourceFile reloaded = SaveAndLoad(in w);

        Assert.Equal(1, reloaded.History.Count);
        Assert.False(Log.CanRedo(in reloaded.History));
        Assert.True(Log.CanUndo(in reloaded.History));
    }

    [Fact]
    public void A_file_with_history_round_trips_byte_for_byte()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha");
        Splice(ref w, node, 0, 5, "omega");

        Operation connect = default;
        connect.Kind = OpKind.Connect;
        connect.Node = node;
        connect.Relation = w.About;
        connect.Target = w.Root;
        Log.Do(ref w.File.History, ref w.File.Src, connect, actor: 2);

        byte[] original = SourceWrite.ToBytes(in w.File);
        for (int i = 0; i < 3; i++)
        {
            SourceFile loaded = SourceRead.FromBytes(original);
            byte[] again = SourceWrite.ToBytes(in loaded);
            Assert.Equal(original, again);
            original = again;
        }
    }

    [Fact]
    public void A_source_with_no_history_gains_no_section()
    {
        World w = Build();

        SourceFile reloaded = SaveAndLoad(in w);

        Assert.Equal(0, reloaded.History.Count);
        for (int i = 0; i < reloaded.SectionOrderCount; i++)
        {
            Assert.NotEqual((uint)SectionId.History, reloaded.SectionOrder[i]);
        }
    }

    [Fact]
    public void A_relation_only_the_log_names_is_still_written()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha");

        Operation connect = default;
        connect.Kind = OpKind.Connect;
        connect.Node = node;
        connect.Relation = w.About;
        connect.Target = w.Root;
        Log.Do(ref w.File.History, ref w.File.Src, connect, actor: 1);

        Operation disconnect = default;
        disconnect.Kind = OpKind.Disconnect;
        disconnect.Node = node;
        disconnect.Index = 0;
        Log.Do(ref w.File.History, ref w.File.Src, disconnect, actor: 1);

        Assert.Equal(0, SourceWalk.RefCount(in w.File.Src, node));

        SourceFile reloaded = SaveAndLoad(in w);

        RelationId about = KindTable.LookupRelation(in reloaded.Table, AboutName);
        Assert.False(about.IsUnknown);
        Assert.Equal(about, reloaded.History.Entries[2].Op.Relation);

        // And undoing the disconnect puts back an edge that still means `about`.
        Assert.True(Log.Undo(ref reloaded.History, ref reloaded.Src));
        Assert.Equal(1, SourceWalk.RefCount(in reloaded.Src, node));
        Assert.Equal(about, SourceWalk.RefAt(in reloaded.Src, node, 0).Relation);
    }

    [Fact]
    public void A_history_from_a_build_we_do_not_share_is_preserved_rather_than_read()
    {
        World w = Build();
        NodeId node = Insert(ref w, w.Root, 0, "alpha");
        Splice(ref w, node, 0, 5, "omega");

        // Digest-less, the way a build that predates digests writes: tampering below would
        // otherwise invalidate the digest, and the rewrite would correctly replace it.
        w.File.SectionOrder = new uint[]
        {
            (uint)SectionId.Identity,
            (uint)SectionId.KindNames,
            (uint)SectionId.RelationNames,
            (uint)SectionId.Nodes,
            (uint)SectionId.History,
        };
        w.File.SectionOrderCount = 5;

        byte[] bytes = SourceWrite.ToBytes(in w.File);

        // Find the history section and claim a version this build has never heard of.
        SectionSpan[] spans = null;
        int count = SourceRead.Sections(bytes, ref spans);

        long at = -1;
        for (int i = 0; i < count; i++)
        {
            if (spans[i].Id == (uint)SectionId.History)
            {
                at = spans[i].At;
            }
        }
        Assert.True(at >= 0, "no history section was written");

        Assert.Equal(HistoryCodec.Version, bytes[at]);
        bytes[at] = 200;

        SourceFile loaded = SourceRead.FromBytes(bytes);

        // Nothing was read, and nothing was invented.
        Assert.Equal(0, loaded.History.Count);
        Assert.False(Log.CanUndo(in loaded.History));

        // Payloads survive save/reopen.
        Assert.Equal(bytes, SourceWrite.ToBytes(in loaded));

        // The graph itself is untouched by any of it.
        Assert.Equal("omega", TextOf(in loaded.Src, node));
    }
}
