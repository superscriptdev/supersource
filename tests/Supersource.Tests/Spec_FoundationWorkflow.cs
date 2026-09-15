using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

public class Spec_FoundationWorkflow
{
    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void Rejected_move_preserves_structure_and_history(int index)
    {
        var src = Source.Create(16); var kind = new KindId(1);
        var root = SourceBuild.AddRoot(ref src, kind);
        var child = SourceBuild.AddChild(ref src, root, kind);
        var destination = SourceBuild.AddChild(ref src, root, kind);
        var log = Log.Create(); var stamp = src.Stamp; var revision = src.Revision;
        Assert.Throws<ArgumentOutOfRangeException>(() => Log.Do(ref log, ref src,
            new Operation { Kind = OpKind.Move, Node = child, Parent = destination, Index = index }));
        Assert.Equal(root, SourceWalk.Parent(in src, child));
        Assert.Equal(2, SourceWalk.ChildCount(in src, root));
        Assert.Equal(child, SourceWalk.ChildAt(in src, root, 0));
        Assert.Equal(0, SourceWalk.ChildCount(in src, destination));
        Assert.Equal(stamp, src.Stamp); Assert.Equal(revision, src.Revision);
        Assert.Equal(0, log.Count); Assert.Equal(0, log.At);
    }

    [Fact]
    public void Attaching_a_detached_ancestor_below_its_descendant_is_rejected_without_mutation()
    {
        var src = Source.Create(16); var kind = new KindId(1);
        var root = SourceBuild.AddRoot(ref src, kind);
        var child = SourceBuild.AddChild(ref src, root, kind);
        var stamp = src.Stamp;
        Assert.Throws<InvalidOperationException>(() => SourceBuild.InsertChild(ref src, child, 0, root));
        Assert.True(SourceWalk.Parent(in src, root).IsNone);
        Assert.Equal(root, SourceWalk.Parent(in src, child)); Assert.Equal(stamp, src.Stamp);
    }

    [Fact]
    public void Reference_identity_survives_incomplete_edit_reopen_repair_delete_and_undo()
    {
        var file = SourceFile.Create();
        var library = new SourceId(17, 23);
        var kind = KindTable.Intern(ref file.Table, new QualifiedName(library, "node"));
        var relation = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "refers-to"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.OpensScope, PayloadFormat.Utf8, DisplayShape.Block, 0, new[] { relation });
        SourceBuild.MintIdentity(ref file.Src);
        var identity = file.Src.Identity;
        var root = SourceBuild.AddRoot(ref file.Src, kind);
        var declaration = SourceBuild.AddChild(ref file.Src, root, kind);
        SourceBuild.SetPayload(ref file.Src, declaration, Encoding.UTF8.GetBytes("original"));
        var use = SourceBuild.AddChild(ref file.Src, root, kind);
        SourceBuild.AddRef(ref file.Src, use, relation, declaration);
        var folder = SourceBuild.AddChild(ref file.Src, root, kind);
        Log.Do(ref file.History, ref file.Src, new Operation { Kind = OpKind.Rename, Node = declaration, Value = Encoding.UTF8.GetBytes("renamed") });
        Log.Do(ref file.History, ref file.Src, new Operation { Kind = OpKind.Move, Node = declaration, Parent = folder, Index = 0 });
        Log.Do(ref file.History, ref file.Src, new Operation { Kind = OpKind.Disconnect, Node = use, Index = 0 });
        Log.Do(ref file.History, ref file.Src, new Operation { Kind = OpKind.Connect, Node = use, Relation = relation, Target = NodeId.None, Value = Encoding.UTF8.GetBytes("unfinished") });
        var bytes = SourceWrite.ToBytes(in file);
        var reopened = SourceRead.FromBytes(bytes);
        try
        {
            Assert.Equal(identity, reopened.Src.Identity); Assert.Equal(bytes, SourceWrite.ToBytes(in reopened));
            relation = KindTable.InternRelation(ref reopened.Table, new QualifiedName(library, "refers-to"));
            Assert.True(SourceWalk.Target(in reopened.Src, use, relation).IsNone);
            Assert.Equal(folder, SourceWalk.Parent(in reopened.Src, declaration));
            Log.Do(ref reopened.History, ref reopened.Src, new Operation { Kind = OpKind.Disconnect, Node = use, Index = 0 });
            Log.Do(ref reopened.History, ref reopened.Src, new Operation { Kind = OpKind.Connect, Node = use, Relation = relation, Target = declaration });
            Assert.Equal(declaration, SourceWalk.Target(in reopened.Src, use, relation));
            Log.Do(ref reopened.History, ref reopened.Src, new Operation { Kind = OpKind.Delete, Node = declaration });
            Assert.False(reopened.Src.IsLive(declaration)); Assert.Equal(declaration, SourceWalk.Target(in reopened.Src, use, relation));
            Assert.True(Log.Undo(ref reopened.History, ref reopened.Src)); Assert.True(reopened.Src.IsLive(declaration));
            Assert.Equal(declaration, SourceWalk.Target(in reopened.Src, use, relation));
            while (Log.CanUndo(in reopened.History)) Assert.True(Log.Undo(ref reopened.History, ref reopened.Src));
            Assert.Equal(root, SourceWalk.Parent(in reopened.Src, declaration));
            Assert.Equal("original", Encoding.UTF8.GetString(SourceWalk.Payload(in reopened.Src, declaration)));
            Assert.Equal(declaration, SourceWalk.Target(in reopened.Src, use, relation));
        }
        finally { SourceFile.Close(ref reopened); SourceFile.Close(ref file); }
    }
}
