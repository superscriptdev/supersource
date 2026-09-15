using System;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>Checks embedded schemas in fixtures without loading the libraries that authored them.</summary>
public class Spec_FixturesFresh
{
    [Theory]
    [MemberData(nameof(Spec_Stranger.AllFixtures), MemberType = typeof(Spec_Stranger))]
    public void A_fixture_embeds_a_schema_for_every_kind_it_uses(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        SourceFile file = SourceRead.FromBytes(File.ReadAllBytes(path));

        // hostile.ss deliberately contains kinds without schemas.
        if (name.StartsWith("hostile"))
        {
            return;
        }

        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!file.Src.IsLive(node))
            {
                continue;
            }

            KindId kind = file.Src.Nodes[i].Kind;
            Assert.True(
                KindTable.HasSchema(in file.Table, kind),
                name + " uses " + KindTable.NameOf(in file.Table, kind) +
                " with no embedded schema — regenerate with `ss fixtures <supersource>/tests/fixtures`");
        }
    }
}
