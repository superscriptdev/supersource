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
        public KindBootstrap.Ids Boot;

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
        w.Table = KindBootstrap.CreateTable(out w.Boot);

        w.Book = KindTable.Intern(ref w.Table, Recipes.Book);
        w.Recipe = KindTable.Intern(ref w.Table, Recipes.Recipe);
        w.Ingredient = KindTable.Intern(ref w.Table, Recipes.Ingredient);
        w.Step = KindTable.Intern(ref w.Table, Recipes.Step);
        w.Use = KindTable.Intern(ref w.Table, Recipes.UseOf);
        w.Uses = KindTable.InternRelation(ref w.Table, Recipes.UsesName);
        w.Notes = KindTable.InternRelation(ref w.Table, Recipes.NoteRelation);

        // Two facts drive everything below: who opens a scope, and who is a declaration.
        KindTable.DefineSchema(ref w.Table, w.Book,
            StructuralFact.OpensScope, PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Recipe,
            StructuralFact.OpensScope | StructuralFact.Declaration,
            PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Ingredient,
            StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Leaf, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Step,
            StructuralFact.None, PayloadFormat.Utf8, DisplayShape.Block, 0, default);
        KindTable.DefineSchema(ref w.Table, w.Use,
            StructuralFact.None, PayloadFormat.None, DisplayShape.Inline, 0,
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

    static string[] Names(in World w, NodeId[] ids, int count)
    {
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = Encoding.UTF8.GetString(SourceWalk.Payload(in w.Src, ids[i]));
        }
        return names;
    }

    [Fact]
    public void Scope_is_computed_from_two_structural_facts_over_an_unknown_library()
    {
        World w = Build();

        NodeId[] visible = null;
        int count = Scope.Visible(in w.Src, in w.Table, w.CakeStep, ref visible);
        string[] names = Names(in w, visible, count);

        // Innermost scope first; within a scope, source order. The cake contributes
        // flour and its own salt; the book then contributes salt, cake and bread in the
        // order they are written.
        Assert.Equal(new[] { "flour", "salt", "salt", "cake", "bread" }, names);

        // The cake's salt shadows the pantry's — both visible, innermost first, and they
        // are different nodes.
        Assert.Equal(w.CakeSalt, visible[1]);
        Assert.Equal(w.Salt, visible[2]);
        Assert.NotEqual(w.CakeSalt, w.Salt);
    }

    [Fact]
    public void A_nested_scope_does_not_leak_its_declarations()
    {
        World w = Build();

        NodeId[] visible = null;
        int count = Scope.Visible(in w.Src, in w.Table, w.BreadStep, ref visible);
        string[] names = Names(in w, visible, count);

        Assert.DoesNotContain("flour", names);   // belongs to the cake
        Assert.Contains("salt", names);          // the pantry's
        Assert.Contains("bread", names);
    }

    [Fact]
    public void A_scope_opener_can_also_be_a_declaration()
    {
        World w = Build();

        NodeId[] fromOutside = null;
        int outside = Scope.Visible(in w.Src, in w.Table, w.Salt, ref fromOutside);
        Assert.Contains("cake", Names(in w, fromOutside, outside));
        Assert.DoesNotContain("flour", Names(in w, fromOutside, outside));

        Assert.Equal(w.Cake, Scope.EnclosingScope(in w.Src, in w.Table, w.CakeStep));

        // A scope opener is its own enclosing scope, matching Visible, which starts the
        // walk at the node itself. The scope OUTSIDE it is a different question, asked by
        // starting from the parent.
        Assert.Equal(w.Cake, Scope.EnclosingScope(in w.Src, in w.Table, w.Cake));
        Assert.Equal(
            w.Root,
            Scope.EnclosingScope(in w.Src, in w.Table, SourceWalk.Parent(in w.Src, w.Cake)));
    }

    [Fact]
    public void Binding_a_name_in_scope_produces_a_reference_to_the_innermost_match()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        Binder.Bind(ref w.Src, in w.Table, use, w.Uses, "salt");

        Ref edge = SourceWalk.RefAt(in w.Src, use, 0);
        Assert.True(edge.IsBound);
        Assert.Equal(w.CakeSalt, edge.Target);   // not the pantry's
    }

    [Fact]
    public void Binding_a_name_that_is_not_in_scope_produces_a_labelled_hole()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        Binder.Bind(ref w.Src, in w.Table, use, w.Uses, "saffron");

        Ref edge = SourceWalk.RefAt(in w.Src, use, 0);
        Assert.True(edge.IsUnbound);
        Assert.Equal("saffron", Encoding.UTF8.GetString(SourceWalk.Label(in w.Src, in edge)));
    }

    [Fact]
    public void A_name_from_a_sibling_scope_does_not_bind()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.BreadStep, w.Use);
        Binder.Bind(ref w.Src, in w.Table, use, w.Uses, "flour");

        Assert.True(SourceWalk.RefAt(in w.Src, use, 0).IsUnbound);
    }

    [Fact]
    public void Candidates_returns_every_match_innermost_first_without_choosing()
    {
        World w = Build();

        NodeId[] candidates = null;
        int count = Binder.Candidates(in w.Src, in w.Table, w.CakeStep,
            Encoding.UTF8.GetBytes("salt"), ref candidates);

        Assert.Equal(2, count);
        Assert.Equal(w.CakeSalt, candidates[0]);
        Assert.Equal(w.Salt, candidates[1]);
    }

    [Fact]
    public void Linking_turns_a_reference_into_a_deleted_node_into_a_labelled_hole()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);

        SourceBuild.Delete(ref w.Src, w.Flour);
        Assert.Equal(Resolution.Deleted, Linker.Resolve(in w.Src, SourceWalk.RefAt(in w.Src, use, 0)));

        int repaired = Linker.Link(ref w.Src, in w.Table);
        Assert.Equal(1, repaired);

        Ref edge = SourceWalk.RefAt(in w.Src, use, 0);
        Assert.True(edge.IsUnbound);
        // The tombstone kept its payload, so the hole can say WHAT went missing.
        Assert.Equal("flour", Encoding.UTF8.GetString(SourceWalk.Label(in w.Src, in edge)));
    }

    [Fact]
    public void Linking_is_idempotent()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);
        SourceBuild.Delete(ref w.Src, w.Flour);

        Assert.Equal(1, Linker.Link(ref w.Src, in w.Table));
        Assert.Equal(0, Linker.Link(ref w.Src, in w.Table));
        Assert.Equal(0, Linker.Link(ref w.Src, in w.Table));
    }

    [Fact]
    public void Linking_leaves_good_references_alone()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        SourceBuild.AddRef(ref w.Src, use, w.Uses, w.Flour);

        Assert.Equal(0, Linker.Link(ref w.Src, in w.Table));
        Assert.Equal(w.Flour, SourceWalk.RefAt(in w.Src, use, 0).Target);
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
    public void Holes_are_reported_as_situations_not_as_errors()
    {
        World w = Build();

        NodeId use = SourceBuild.AddChild(ref w.Src, w.CakeStep, w.Use);
        Binder.Bind(ref w.Src, in w.Table, use, w.Uses, "saffron");

        Finding[] findings = null;
        int count = RelationCheck.Check(in w.Src, in w.Table, ref findings);

        bool sawHole = false;
        for (int i = 0; i < count; i++)
        {
            if (findings[i].Kind == FindingKind.Hole && findings[i].Node == use)
            {
                Assert.Contains("saffron", findings[i].Detail);
                sawHole = true;
            }
        }
        Assert.True(sawHole);
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
        KindTable table = KindBootstrap.CreateTable(out KindBootstrap.Ids boot);
        RelationId producedBy = KindTable.InternRelation(ref table, new QualifiedName(SourceId.Mint(), "produced-by"));
        RelationId generatedFrom = KindTable.InternRelation(ref table, new QualifiedName(SourceId.Mint(), "generated-from"));

        SourceId library = SourceId.Mint();
        KindId thing = KindTable.Intern(ref table, new QualifiedName(library, "thing"));
        RelationId mine = KindTable.InternRelation(ref table, new QualifiedName(library, "mine"));

        // Declares nothing at all, which is the ordinary case for a kind that never expected
        // to be produced by anything.
        KindTable.DefineSchema(ref table, thing, StructuralFact.None,
            PayloadFormat.None, DisplayShape.Leaf, 0, default);

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
