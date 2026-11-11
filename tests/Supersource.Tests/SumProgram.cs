using System.Text;

namespace Supersource.Tests;
/// <summary>
/// Program fixture with test-local kinds and relations. The node map below is used by tests that
/// address specific slots.
///
/// <code>
/// fn sum(xs):
///     let total = 0
///     for x in xs:
///         total = total + x
///     return total
/// </code>
/// <code>
///  1  function    "sum"      children = [2, 3]
///  2  parameter   "xs"
///  3  body                   children = [4, 6, 14]
///  4  local       "total"    children = [5]
///  5  literal     0
///  6  for-loop               children = [7, 8, 9]
///  7  local       "x"                              ← the loop variable
///  8  use                    refs = {decl -> 2}    ← xs
///  9  body                   children = [10]
/// 10  assign                 children = [11]  refs = {target -> 4}
/// 11  add                    children = [12, 13]
/// 12  use                    refs = {decl -> 4}    ← total
/// 13  use                    refs = {decl -> 7}    ← x
/// 14  return                 children = [15]
/// 15  use                    refs = {decl -> 4}    ← total
/// </code>
/// </summary>
public static class SumProgram
{
    // Test-local kind handles.
    public static readonly KindId Module = new KindId(0);
    public static readonly KindId Function = new KindId(1);
    public static readonly KindId Parameter = new KindId(2);
    public static readonly KindId Body = new KindId(3);
    public static readonly KindId Local = new KindId(4);
    public static readonly KindId Literal = new KindId(5);
    public static readonly KindId ForLoop = new KindId(6);
    public static readonly KindId Use = new KindId(7);
    public static readonly KindId Assign = new KindId(8);
    public static readonly KindId Add = new KindId(9);
    public static readonly KindId Return = new KindId(10);

    // Relations.
    public static readonly RelationId Decl = new RelationId(0);
    public static readonly RelationId Target = new RelationId(1);

    public struct Built
    {
        public Source Src;

        public NodeId Root;
        public NodeId Function;    // 1
        public NodeId Xs;          // 2
        public NodeId FnBody;      // 3
        public NodeId Total;       // 4
        public NodeId Zero;        // 5
        public NodeId Loop;        // 6
        public NodeId X;           // 7
        public NodeId UseXs;       // 8
        public NodeId LoopBody;    // 9
        public NodeId Assign;      // 10
        public NodeId Add;         // 11
        public NodeId UseTotal;    // 12
        public NodeId UseX;        // 13
        public NodeId Return;      // 14
        public NodeId UseTotal2;   // 15
    }

    public static Built Build()
    {
        Built b = default;
        b.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref b.Src);

        b.Root = SourceBuild.AddRoot(ref b.Src, Module);

        b.Function = SourceBuild.AddChild(ref b.Src, b.Root, SumProgram.Function);
        Label(ref b.Src, b.Function, "sum");

        b.Xs = SourceBuild.AddChild(ref b.Src, b.Function, Parameter);
        Label(ref b.Src, b.Xs, "xs");

        b.FnBody = SourceBuild.AddChild(ref b.Src, b.Function, Body);

        b.Total = SourceBuild.AddChild(ref b.Src, b.FnBody, Local);
        Label(ref b.Src, b.Total, "total");

        b.Zero = SourceBuild.AddChild(ref b.Src, b.Total, Literal);
        SourceBuild.SetPayload(ref b.Src, b.Zero, new byte[] { 0 });

        b.Loop = SourceBuild.AddChild(ref b.Src, b.FnBody, ForLoop);

        b.X = SourceBuild.AddChild(ref b.Src, b.Loop, Local);
        Label(ref b.Src, b.X, "x");

        b.UseXs = SourceBuild.AddChild(ref b.Src, b.Loop, Use);
        SourceBuild.AddRef(ref b.Src, b.UseXs, Decl, b.Xs);

        b.LoopBody = SourceBuild.AddChild(ref b.Src, b.Loop, Body);

        b.Assign = SourceBuild.AddChild(ref b.Src, b.LoopBody, SumProgram.Assign);
        SourceBuild.AddRef(ref b.Src, b.Assign, Target, b.Total);

        b.Add = SourceBuild.AddChild(ref b.Src, b.Assign, SumProgram.Add);

        b.UseTotal = SourceBuild.AddChild(ref b.Src, b.Add, Use);
        SourceBuild.AddRef(ref b.Src, b.UseTotal, Decl, b.Total);

        b.UseX = SourceBuild.AddChild(ref b.Src, b.Add, Use);
        SourceBuild.AddRef(ref b.Src, b.UseX, Decl, b.X);

        b.Return = SourceBuild.AddChild(ref b.Src, b.FnBody, SumProgram.Return);

        b.UseTotal2 = SourceBuild.AddChild(ref b.Src, b.Return, Use);
        SourceBuild.AddRef(ref b.Src, b.UseTotal2, Decl, b.Total);

        return b;
    }

    public static void Label(ref Source src, NodeId id, string text)
    {
        SourceBuild.SetPayload(ref src, id, Encoding.UTF8.GetBytes(text));
    }

    public static string LabelOf(in Source src, NodeId id)
    {
        return Encoding.UTF8.GetString(SourceWalk.Payload(in src, id));
    }
}
