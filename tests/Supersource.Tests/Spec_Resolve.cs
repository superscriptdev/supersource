using System;
using System.Text;
using Supersource.Kinds;
using Supersource.Resolve;
using Xunit;

namespace Supersource.Resolve.Tests;

public class Spec_Resolve
{
    static class Recipes
    {
        public static readonly SourceId Library = SourceId.Mint();

        public static readonly QualifiedName Book = new QualifiedName(Library, "book");
        public static readonly QualifiedName Recipe = new QualifiedName(Library, "recipe");
        public static readonly QualifiedName Ingredient = new QualifiedName(Library, "ingredient");
        public static readonly QualifiedName Step = new QualifiedName(Library, "step");
        public static readonly QualifiedName UseOf = new QualifiedName(Library, "use");

        public static readonly QualifiedName UsesName = new QualifiedName(Library, "uses");
        public static readonly QualifiedName NoteRelation = new QualifiedName(Library, "notes");
    }

    struct World
    {
        public Source Src;
        public KindTable Table;
        public TestKinds.Ids Boot;

        public KindId Book, Recipe, Ingredient, Step, Use;
        public RelationId Uses, Notes;

        public NodeId Root;
        public NodeId Salt;        // book-level ingredient
        public NodeId Cake;        // recipe (opens a scope)
        public NodeId Flour;       // cake-level ingredient
        public NodeId CakeSalt;    // shadows the book-level salt
        public NodeId CakeStep;    // step inside cake
        public NodeId Bread;       // sibling recipe
        public NodeId BreadStep;
    }

    static World Build()
    {
        World w = default;
        w.Table = TestKinds.CreateTable(out w.Boot);

        w.Book = KindTable.Intern(ref w.Table, Recipes.Book);
        w.Recipe = KindTable.Intern(ref w.Table, Recipes.Recipe);
        w.Ingredient = KindTable.Intern(ref w.Table, Recipes.Ingredient);
        w.Step = KindTable.Intern(ref w.Table, Recipes.Step);
        w.Use = KindTable.Intern(ref w.Table, Recipes.UseOf);
        w.Uses = KindTable.InternRelation(ref w.Table, Recipes.UsesName);
        w.Notes = KindTable.InternRelation(ref w.Table, Recipes.NoteRelation);

        KindTable.DefineSchema(ref w.Table, w.Book,
            StructuralFact.None, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.Table, w.Recipe,
            StructuralFact.None, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.Table, w.Ingredient,
            StructuralFact.None, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.Table, w.Step,
            StructuralFact.None, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref w.Table, w.Use,
            StructuralFact.None, PayloadFormat.None,
            new RelationId[] { w.Uses });

        w.Src = Source.Create(32);
        SourceBuild.MintIdentity(ref w.Src);

        w.Root = SourceBuild.AddRoot(ref w.Src, w.Book);
        Label(ref w.Src, w.Root, "pantry");

        w.Salt = SourceBuild.AddChild(ref w.Src, w.Root, w.Ingredient);
        Label(ref w.Src, w.Salt, "salt");

        w.Cake = SourceBuild.AddChild(ref w.Src, w.Root, w.Recipe);
        Label(ref w.Src, w.Cake, "cake");

        w.Flour = SourceBuild.AddChild(ref w.Src, w.Cake, w.Ingredient);
        Label(ref w.Src, w.Flour, "flour");

        w.CakeSalt = SourceBuild.AddChild(ref w.Src, w.Cake, w.Ingredient);
        Label(ref w.Src, w.CakeSalt, "salt");

        w.CakeStep = SourceBuild.AddChild(ref w.Src, w.Cake, w.Step);
        Label(ref w.Src, w.CakeStep, "mix");

        w.Bread = SourceBuild.AddChild(ref w.Src, w.Root, w.Recipe);
        Label(ref w.Src, w.Bread, "bread");

        w.BreadStep = SourceBuild.AddChild(ref w.Src, w.Bread, w.Step);
        Label(ref w.Src, w.BreadStep, "knead");

        return w;
    }

    static void Label(ref Source src, NodeId id, string text)
    {
        SourceBuild.SetPayload(ref src, id, Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void A_reference_says_whether_it_is_bound_unbound_or_dangling()
    {
        World w = Build();
        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);
        SourceBuild.AddUnboundRef(ref w.Src, use, w.Uses, Encoding.UTF8.GetBytes("sugar"));
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Salt);

        Assert.Equal(RefState.Bound, SourceWalk.StateOf(in w.Src, SourceWalk.RefAt(in w.Src, use, 0)));
        Assert.Equal(RefState.Unbound, SourceWalk.StateOf(in w.Src, SourceWalk.RefAt(in w.Src, use, 1)));

        // Deletion means gone: what pointed at the node now points at nothing live.
        SourceBuild.Delete(ref w.Src, w.Salt);
        Assert.Equal(RefState.Dangling, SourceWalk.StateOf(in w.Src, SourceWalk.RefAt(in w.Src, use, 2)));

        // An id the source never gave out is only in a damaged file.
        Ref forged = Ref.Bound(w.Uses, new NodeId(w.Src.NodeCount + 10));
        Assert.Equal(RefState.OutOfRange, SourceWalk.StateOf(in w.Src, in forged));
    }

    [Fact]
    public void A_relation_a_kind_does_not_declare_is_reported()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);    // declared
        SourceBuild.AddRef(ref w.Src, use, w.Notes, w.Cake);    // not declared

        Finding[] findings = null;
        int count = RelationCheck.Check(in w.Src, in w.Table, ref findings);

        int undeclared = 0;
        for (int i = 0; i < count; i++)
        {
            if (findings[i].Kind == FindingKind.UndeclaredRelation)
            {
                Assert.Equal(use, findings[i].Node);
                Assert.Equal(1, findings[i].RefIndex);
                undeclared++;
            }
        }
        Assert.Equal(1, undeclared);
    }

    [Fact]
    public void Unbound_references_are_reported_as_situations_not_as_errors()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddUnboundRef(ref w.Src, use, w.Uses, Encoding.UTF8.GetBytes("saffron"));

        Finding[] findings = null;
        int count = RelationCheck.Check(in w.Src, in w.Table, ref findings);

        bool sawUnbound = false;
        for (int i = 0; i < count; i++)
        {
            if (findings[i].Kind == FindingKind.Unbound && findings[i].Node == use)
            {
                Assert.Contains("saffron", findings[i].Detail);
                sawUnbound = true;
            }
        }
        Assert.True(sawUnbound);
    }

    [Fact]
    public void An_uninterpretable_kind_reports_once_and_suppresses_relation_noise()
    {
        World w = Build();

        KindId ghost = KindTable.Intern(ref w.Table, new QualifiedName(SourceId.Mint(), "ghost"));
        NodeId node = SourceBuild.AddChild(ref w.Src, w.CakeStep, ghost);
        SourceBuild.AddRef(ref w.Src, node, w.Uses, w.Flour);
        SourceBuild.AddRef(ref w.Src, node, w.Notes, w.Cake);

        Finding[] findings = null;
        int count = RelationCheck.Check(in w.Src, in w.Table, ref findings);

        int unresolved = 0;
        int undeclared = 0;
        for (int i = 0; i < count; i++)
        {
            if (findings[i].Node != node)
            {
                continue;
            }
            if (findings[i].Kind == FindingKind.UnresolvedKind)
            {
                unresolved++;
            }
            if (findings[i].Kind == FindingKind.UndeclaredRelation)
            {
                undeclared++;
            }
        }

        Assert.Equal(1, unresolved);
        Assert.Equal(0, undeclared);
    }

    [Fact]
    public void A_clean_document_reports_nothing()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);

        Finding[] findings = null;
        Assert.Equal(0, RelationCheck.Check(in w.Src, in w.Table, ref findings));
    }

    [Fact]
    public void Extension_relations_require_explicit_permission()
    {
        KindTable table = TestKinds.CreateTable(out TestKinds.Ids boot);
        RelationId producedBy = KindTable.InternRelation(ref table, new QualifiedName(SourceId.Mint(), "produced-by"));
        RelationId generatedFrom = KindTable.InternRelation(ref table, new QualifiedName(SourceId.Mint(), "generated-from"));

        SourceId library = SourceId.Mint();
        KindId thing = KindTable.Intern(ref table, new QualifiedName(library, "thing"));
        RelationId mine = KindTable.InternRelation(ref table, new QualifiedName(library, "mine"));

        // Declares nothing at all, which is the ordinary case for a kind that never expected
        // to be produced by anything.
        KindTable.DefineSchema(ref table, thing, StructuralFact.None,
            PayloadFormat.None, default);

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, thing);
        NodeId made = SourceBuild.AddChild(ref src, root, thing);

        SourceBuild.AddRef(ref src, made, producedBy, root);
        SourceBuild.AddRef(ref src, made, generatedFrom, root);

        Finding[] found = null;
        int count = RelationCheck.Check(in src, in table, ref found);

        Assert.Equal(2, count);
        Assert.All(found.AsSpan(0, count).ToArray(), f => Assert.Equal(FindingKind.UndeclaredRelation, f.Kind));
        RelationId[] allowed = { producedBy, generatedFrom };
        count = RelationCheck.Check(in src, in table, ref found, allowed);
        Assert.Equal(0, count);

        // Permission is limited to the supplied relations.
        SourceBuild.AddRef(ref src, made, mine, root);
        count = RelationCheck.Check(in src, in table, ref found, allowed);

        Assert.Contains(found.AsSpan(0, count).ToArray(),
            f => f.Kind == FindingKind.UndeclaredRelation && f.Node == made);
    }
}
