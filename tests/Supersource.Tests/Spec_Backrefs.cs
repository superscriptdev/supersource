using System;
using System.Text;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Tests;

public class Spec_Backrefs
{
    static readonly SourceId Library = SourceId.Mint();

    struct World
    {
        public Source Src;
        public KindId Kind;
        public RelationId About, Calls;
        public NodeId Root;
    }

    static World Build(int fanOut)
    {
        World w = default;
        KindTable table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        w.Kind = KindTable.Intern(ref table, new QualifiedName(Library, "thing"));
        w.About = KindTable.InternRelation(ref table, new QualifiedName(Library, "about"));
        w.Calls = KindTable.InternRelation(ref table, new QualifiedName(Library, "calls"));

        w.Src = Source.Create(64);
        SourceBuild.MintIdentity(ref w.Src);
        w.Root = SourceBuild.AddRoot(ref w.Src, w.Kind);

        NodeId subject = SourceBuild.AddChild(ref w.Src, w.Root, w.Kind);
        for (int i = 0; i < fanOut; i++)
        {
            NodeId node = SourceBuild.AddChild(ref w.Src, w.Root, w.Kind);
            SourceBuild.SetPayload(ref w.Src, node, Encoding.UTF8.GetBytes("n" + i));
            SourceBuild.AddRef(ref w.Src, node, w.About, subject);
        }
        return w;
    }

    /// <summary>Independent full scan used as an oracle for Backrefs.</summary>
    static int ScanFor(in Source src, NodeId target, ref Backref[] into)
    {
        int found = 0;
        for (int i = 0; i < src.NodeCount; i++)
        {
            if (!src.IsLive(new NodeId(i)))
            {
                continue;
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in src, new NodeId(i));
            for (int r = 0; r < refs.Length; r++)
            {
                if (refs[r].Target != target)
                {
                    continue;
                }

                if (found == into.Length)
                {
                    Array.Resize(ref into, into.Length * 2);
                }
                into[found].From = new NodeId(i);
                into[found].RefIndex = r;
                found++;
            }
        }
        return found;
    }

    static void AssertMatchesTheScan(in Source src)
    {
        Backrefs index = default;
        Backrefs.Build(in src, ref index);

        Backref[] scanned = new Backref[8];
        int edges = 0;

        for (int i = 0; i < src.NodeCount; i++)
        {
            NodeId target = new NodeId(i);
            int count = ScanFor(in src, target, ref scanned);

            ReadOnlySpan<Backref> indexed = Backrefs.To(in index, target);
            Assert.Equal(count, indexed.Length);
            Assert.Equal(count, Backrefs.CountTo(in index, target));

            for (int e = 0; e < count; e++)
            {
                Assert.Equal(scanned[e].From, indexed[e].From);
                Assert.Equal(scanned[e].RefIndex, indexed[e].RefIndex);
            }
            edges += count;
        }

        Assert.Equal(edges, index.EntryCount);
    }

    [Fact]
    public void It_answers_exactly_what_the_scan_answers()
    {
        World w = Build(6);
        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void Two_edges_from_one_node_to_one_target_are_two_entries()
    {
        World w = Build(1);
        NodeId subject = SourceWalk.ChildAt(in w.Src, w.Root, 0);
        NodeId caller = SourceWalk.ChildAt(in w.Src, w.Root, 1);

        SourceBuild.AddRef(ref w.Src, caller, w.Calls, subject);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        ReadOnlySpan<Backref> at = Backrefs.To(in index, subject);
        Assert.Equal(2, at.Length);
        Assert.Equal(caller, at[0].From);
        Assert.Equal(caller, at[1].From);
        Assert.NotEqual(at[0].RefIndex, at[1].RefIndex);

        NodeId[] nodes = null;
        Assert.Equal(1, SourceWalk.Referrers(in w.Src, subject, ref nodes));

        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void Edges_pointing_at_a_tombstone_are_indexed()
    {
        World w = Build(3);
        NodeId subject = SourceWalk.ChildAt(in w.Src, w.Root, 0);

        SourceBuild.Delete(ref w.Src, subject);
        Assert.False(w.Src.IsLive(subject));

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        Assert.Equal(3, Backrefs.CountTo(in index, subject));
        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void Edges_held_by_a_tombstone_are_not_indexed()
    {
        World w = Build(3);
        NodeId subject = SourceWalk.ChildAt(in w.Src, w.Root, 0);
        NodeId holder = SourceWalk.ChildAt(in w.Src, w.Root, 1);

        SourceBuild.Delete(ref w.Src, holder);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        Assert.Equal(2, Backrefs.CountTo(in index, subject));
        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void Holes_are_not_in_the_index()
    {
        World w = Build(2);
        NodeId node = SourceWalk.ChildAt(in w.Src, w.Root, 1);
        SourceBuild.AddUnboundRef(ref w.Src, node, w.About, Encoding.UTF8.GetBytes("missing"));

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        Assert.Equal(2, index.EntryCount);
        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void A_source_with_no_edges_at_all_indexes_to_nothing()
    {
        Source src = Source.Create(8);
        SourceBuild.MintIdentity(ref src);
        SourceBuild.AddRoot(ref src, new KindId(1));

        Backrefs index = default;
        Backrefs.Build(in src, ref index);

        Assert.Equal(0, index.EntryCount);
        Assert.Equal(0, Backrefs.CountTo(in index, src.Root));
        AssertMatchesTheScan(in src);
    }

    [Fact]
    public void An_index_knows_when_its_source_moved_on()
    {
        World w = Build(2);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);
        Assert.True(Backrefs.IsCurrent(in index, in w.Src));

        SourceBuild.AddRef(ref w.Src, w.Root, w.About, w.Root);
        Assert.False(Backrefs.IsCurrent(in index, in w.Src));

        Backrefs.Build(in w.Src, ref index);
        Assert.True(Backrefs.IsCurrent(in index, in w.Src));
    }

    [Fact]
    public void Asking_about_a_slot_the_index_does_not_cover_is_empty()
    {
        World w = Build(2);
        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        Assert.Equal(0, Backrefs.To(in index, new NodeId(9999)).Length);
        Assert.Equal(0, Backrefs.To(in index, NodeId.None).Length);
        Assert.Equal(0, Backrefs.CountTo(in index, new NodeId(9999)));

        Backrefs never = default;
        Assert.Equal(0, Backrefs.To(in never, new NodeId(0)).Length);
        Assert.False(Backrefs.IsCurrent(in never, in w.Src));
    }

    [Fact]
    public void Rebuilding_reuses_its_arrays()
    {
        World w = Build(8);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        int[] start = index.Start;
        Backref[] entries = index.Entries;

        SourceBuild.SetPayload(ref w.Src, w.Root, Encoding.UTF8.GetBytes("edited"));
        Backrefs.Build(in w.Src, ref index);

        Assert.Same(start, index.Start);
        Assert.Same(entries, index.Entries);
        AssertMatchesTheScan(in w.Src);
    }

    [Fact]
    public void Runs_are_ordered_by_the_node_that_holds_the_edge()
    {
        World w = Build(12);
        NodeId subject = SourceWalk.ChildAt(in w.Src, w.Root, 0);

        Backrefs index = default;
        Backrefs.Build(in w.Src, ref index);

        ReadOnlySpan<Backref> at = Backrefs.To(in index, subject);
        Assert.Equal(12, at.Length);
        for (int i = 1; i < at.Length; i++)
        {
            Assert.True(at[i - 1].From.Index < at[i].From.Index);
        }
    }

    [Fact]
    public void An_index_built_from_another_source_is_not_current()
    {
        Format.SourceFile a = Format.SourceFile.Create(16);
        SourceBuild.MintIdentity(ref a.Src);
        KindId kind = KindTable.Intern(ref a.Table, new QualifiedName(Library, "thing"));
        NodeId rootA = SourceBuild.AddRoot(ref a.Src, kind);
        NodeId childA = SourceBuild.AddChild(ref a.Src, rootA, kind);
        SourceBuild.AddRef(ref a.Src, childA, new RelationId(0), rootA);

        Format.SourceFile b = Format.SourceFile.Create(16);
        SourceBuild.MintIdentity(ref b.Src);
        KindTable.Intern(ref b.Table, new QualifiedName(Library, "thing"));
        NodeId rootB = SourceBuild.AddRoot(ref b.Src, kind);
        SourceBuild.AddChild(ref b.Src, rootB, kind);   // same shape, no edge

        Source loadedA = Format.SourceRead.FromBytes(Format.SourceWrite.ToBytes(in a)).Src;
        Source loadedB = Format.SourceRead.FromBytes(Format.SourceWrite.ToBytes(in b)).Src;

        // The collision this is about: both come off disk at revision zero, same size.
        Assert.Equal(loadedA.Revision, loadedB.Revision);
        Assert.Equal(loadedA.NodeCount, loadedB.NodeCount);

        Backrefs index = default;
        Backrefs.Build(in loadedA, ref index);

        Assert.True(Backrefs.IsCurrent(in index, in loadedA));
        Assert.False(Backrefs.IsCurrent(in index, in loadedB));
    }

    [Fact]
    public void Two_states_of_one_source_that_diverged_are_not_interchangeable()
    {
        World w = Build(2);
        Source ours = SourceCompact.Compact(in w.Src);
        Source theirs = SourceCompact.Compact(in w.Src);

        NodeId one = SourceWalk.ChildAt(in w.Src, w.Root, 0);
        SourceBuild.AddRef(ref ours, one, w.About, w.Root);
        SourceBuild.AddRef(ref theirs, w.Root, w.About, one);

        Assert.Equal(ours.Revision, theirs.Revision);

        Backrefs index = default;
        Backrefs.Build(in ours, ref index);

        Assert.True(Backrefs.IsCurrent(in index, in ours));
        Assert.False(Backrefs.IsCurrent(in index, in theirs));
    }
}
