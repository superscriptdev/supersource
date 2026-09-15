using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

public class Spec_Compact
{
    static readonly SourceId Library = SourceId.Mint();

    static Source Typed(out NodeId root, out NodeId deleted, out RelationId about)
    {
        Source src = Source.Create(64);
        SourceBuild.MintIdentity(ref src);

        KindId kind = new KindId(1);
        about = new RelationId(0);
        root = SourceBuild.AddRoot(ref src, kind);

        Log log = Log.Create();
        NodeId first = NodeId.None;

        for (int p = 0; p < 8; p++)
        {
            Operation insert = default;
            insert.Kind = OpKind.Insert;
            insert.Parent = root;
            insert.Index = p;
            insert.NodeKind = kind;
            Log.Do(ref log, ref src, insert);

            NodeId node = log.Entries[log.Count - 1].Op.Node;
            if (first.IsNone)
            {
                first = node;
            }

            int at = 0;
            for (int word = 0; word < 40; word++)
            {
                byte[] text = Encoding.UTF8.GetBytes("word" + word + " ");
                Operation splice = default;
                splice.Kind = OpKind.Splice;
                splice.Node = node;
                splice.Index = at;
                splice.Count = 0;
                splice.Value = text;
                Log.Do(ref log, ref src, splice);
                at += text.Length;
            }

            for (int r = 0; r < 6; r++)
            {
                SourceBuild.AddRef(ref src, node, about, root);
            }

            SourceBuild.AddUnboundRef(ref src, node, about, Encoding.UTF8.GetBytes("looking for " + p));
        }

        // A deleted node, because a rebuild that quietly drops one would renumber everything
        // after it.
        deleted = SourceWalk.ChildAt(in src, root, 3);
        SourceBuild.Delete(ref src, deleted);

        return src;
    }

    static string TextOf(in Source src, NodeId node)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, node));
    }

    [Fact]
    public void A_session_abandons_most_of_its_blob_and_compaction_takes_it_back()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);

        SourceCompact.Wasted(in src, out int blob, out int children, out int refs);
        Assert.True(blob > BlobUsed(in src) / 4, "expected real waste to reclaim, found " + blob);
        Assert.True(children > 0);
        Assert.True(refs > 0);

        Source tight = SourceCompact.Compact(in src);

        Assert.True(BlobUsed(in tight) < BlobUsed(in src));
        Assert.True(tight.ChildArenaUsed < src.ChildArenaUsed);
        Assert.True(tight.RefArenaUsed < src.RefArenaUsed);

        SourceCompact.Wasted(in tight, out blob, out children, out refs);
        Assert.Equal(0, blob);
        Assert.Equal(0, children);
        Assert.Equal(0, refs);
    }

    [Fact]
    public void Compacting_twice_reclaims_nothing_the_second_time()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);

        Source once = SourceCompact.Compact(in src);
        Source twice = SourceCompact.Compact(in once);

        Assert.Equal(BlobUsed(in once), BlobUsed(in twice));
        Assert.Equal(once.ChildArenaUsed, twice.ChildArenaUsed);
        Assert.Equal(once.RefArenaUsed, twice.RefArenaUsed);
    }

    [Fact]
    public void No_id_moves_and_no_deleted_node_is_dropped()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);
        Source tight = SourceCompact.Compact(in src);

        Assert.Equal(src.NodeCount, tight.NodeCount);
        Assert.Equal(src.DeletedNodeCount(), tight.DeletedNodeCount());
        Assert.Equal(src.Root, tight.Root);
        Assert.Equal(src.Identity, tight.Identity);

        Assert.False(tight.IsLive(deleted));
        Assert.True(tight.WasAllocated(deleted));

        for (int i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);

            Assert.Equal(src.Nodes[i].Flags, tight.Nodes[i].Flags);
            Assert.Equal(src.Nodes[i].Kind, tight.Nodes[i].Kind);
            Assert.Equal(src.Nodes[i].Parent, tight.Nodes[i].Parent);

            Assert.Equal(
                SourceWalk.Payload(in src, node).ToArray(),
                SourceWalk.Payload(in tight, node).ToArray());
            Assert.Equal(
                SourceWalk.Children(in src, node).ToArray(),
                SourceWalk.Children(in tight, node).ToArray());

            ReadOnlySpan<Ref> mine = SourceWalk.References(in src, node);
            ReadOnlySpan<Ref> theirs = SourceWalk.References(in tight, node);
            Assert.Equal(mine.Length, theirs.Length);

            for (int r = 0; r < mine.Length; r++)
            {
                Assert.Equal(mine[r].Relation, theirs[r].Relation);
                Assert.Equal(mine[r].Target, theirs[r].Target);
                Assert.Equal(mine[r].IsBound, theirs[r].IsBound);

                // A label lives in the blob and has to move with its edge, which is the
                // copy easiest to forget.
                Assert.Equal(
                    SourceWalk.Label(in src, in mine[r]).ToArray(),
                    SourceWalk.Label(in tight, in theirs[r]).ToArray());
            }
        }
    }

    [Fact]
    public void A_deleted_node_keeps_what_it_held()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);
        string before = TextOf(in src, deleted);
        Assert.NotEqual(string.Empty, before);

        Source tight = SourceCompact.Compact(in src);
        Assert.Equal(before, TextOf(in tight, deleted));
    }

    [Fact]
    public void A_compacted_source_is_the_same_source()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);

        Backrefs index = default;
        Backrefs.Build(in src, ref index);

        Source tight = SourceCompact.Compact(in src);

        Assert.Equal(src.Revision, tight.Revision);
        Assert.True(Backrefs.IsCurrent(in index, in tight));

        for (int i = 0; i < src.NodeCount; i++)
        {
            Assert.Equal(src.NodeRevision[i], tight.NodeRevision[i]);
            Assert.Equal(
                Backrefs.CountTo(in index, new NodeId(i)),
                CountEdgesTo(in tight, new NodeId(i)));
        }
    }

    static int CountEdgesTo(in Source src, NodeId target)
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
                if (refs[r].Target == target)
                {
                    found++;
                }
            }
        }
        return found;
    }

    [Fact]
    public void Compaction_does_not_change_a_single_byte_of_the_file()
    {
        Source src = Typed(out NodeId root, out NodeId deleted, out RelationId about);

        SourceFile loose = SourceFile.Create(4);
        loose.Src = src;

        SourceFile tight = SourceFile.Create(4);
        tight.Src = SourceCompact.Compact(in src);

        Assert.Equal(SourceWrite.ToBytes(in loose), SourceWrite.ToBytes(in tight));
    }

    [Fact]
    public void Compacting_nothing_is_nothing()
    {
        Source src = Source.Create(8);
        Source tight = SourceCompact.Compact(in src);

        Assert.Equal(0, tight.NodeCount);
        Assert.Equal(0, BlobUsed(in tight));
        Assert.True(tight.Root.IsNone);
    }

    static int BlobUsed(in Source src)
    {
        int used = 0;
        for (int i = 0; i < src.BlobSegments; i++)
        {
            used += src.Blob[i].IsMapped ? 0 : src.Blob[i].Used;
        }
        return used;
    }
}
