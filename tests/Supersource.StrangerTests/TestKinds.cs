using Supersource.Kinds;

namespace Supersource;

/// <summary>
/// A kind for tests that need nodes and do not care what they are: `leaf`, with a text payload.
/// The format has no kinds of its own to start a table with, so the tests bring this one.
/// </summary>
static class TestKinds
{
    /// <summary>ASCII "Supersource_test".</summary>
    public static readonly SourceId Library = new SourceId(0x5375706572736F75UL, 0x7263655F74657374UL);

    public static readonly QualifiedName LeafName = new QualifiedName(Library, "leaf");

    public struct Ids
    {
        public KindId Leaf;
    }

    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.Leaf = KindTable.Intern(ref table, LeafName);
        KindTable.DefineSchema(ref table, ids.Leaf, StructuralFact.None, PayloadFormat.Text, default);
        return ids;
    }

    public static KindTable CreateTable(out Ids ids, int capacity = 64)
    {
        KindTable table = KindTable.Create(capacity);
        ids = Install(ref table);
        return table;
    }
}
