
namespace Supersource.Kinds;
/// <summary>
/// Well-known kinds and relation used to decode embedded schemas. Their fixed identities let
/// readers recognize schemas before loading any schema definitions.
/// </summary>
public static class KindBootstrap
{
    /// <summary>Fixed bootstrap identity, encoded as ASCII "Superscript_boot".</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F626F6F74UL);

    // Kinds.
    public static readonly QualifiedName SchemaSetName = new QualifiedName(Library, "schema-set");
    public static readonly QualifiedName KindSchemaName = new QualifiedName(Library, "kind-schema");
    // Keep role-decl and uses-role as their existing wire names; changing them would create
    // different qualified identities.
    public static readonly QualifiedName RelationDeclName = new QualifiedName(Library, "role-decl");

    /// <summary>
    /// A placeholder node kind with an optional label. Distinct from an unbound reference, which is
    /// edge state.
    /// </summary>
    public static readonly QualifiedName HoleName = new QualifiedName(Library, "hole");

    /// <summary>Kind-schema → each relation declaration it uses.</summary>
    public static readonly QualifiedName UsesRelationName = new QualifiedName(Library, "uses-role");

    /// <summary>Interned handles for the bootstrap, valid for one table.</summary>
    public struct Ids
    {
        public KindId SchemaSet;
        public KindId KindSchema;
        public KindId RelationDecl;
        public KindId Hole;
        public RelationId UsesRelation;
    }

    /// <summary>Installs the bootstrap handles and schemas. Required before decoding schema nodes.</summary>
    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.UsesRelation = KindTable.InternRelation(ref table, UsesRelationName);

        ids.SchemaSet = KindTable.Intern(ref table, SchemaSetName);
        ids.KindSchema = KindTable.Intern(ref table, KindSchemaName);
        ids.RelationDecl = KindTable.Intern(ref table, RelationDeclName);
        ids.Hole = KindTable.Intern(ref table, HoleName);

        // Schema-set order is not significant; declarations are matched by name.
        KindTable.DefineSchema(
            ref table, ids.SchemaSet,
            StructuralFact.None,
            PayloadFormat.None,
            DisplayShape.Block,
            0,
            default);

        // A kind-schema carries its facts as payload and points at the relations it uses.
        KindTable.DefineSchema(
            ref table, ids.KindSchema,
            StructuralFact.None,
            PayloadFormat.Record,
            DisplayShape.Block,
            0,
            new RelationId[] { ids.UsesRelation });

        KindTable.DefineSchema(
            ref table, ids.RelationDecl,
            StructuralFact.None,
            PayloadFormat.Record,
            DisplayShape.Leaf,
            0,
            default);

        // A hole carries an optional label and no defined interior.
        KindTable.DefineSchema(
            ref table, ids.Hole,
            StructuralFact.None,
            PayloadFormat.Utf8,
            DisplayShape.Leaf,
            0,
            default);

        return ids;
    }

    /// <summary>A table with the bootstrap already installed. The normal way to start one.</summary>
    public static KindTable CreateTable(out Ids ids, int capacity = 64)
    {
        KindTable table = KindTable.Create(capacity);
        ids = Install(ref table);
        return table;
    }
}
