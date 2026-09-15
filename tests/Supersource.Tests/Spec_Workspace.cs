using System.Text;
using Supersource.Kinds;
using Supersource.Resolve;
using Xunit;

namespace Supersource.Tests;

public class Spec_Workspace
{
    struct Library
    {
        public Source Src;
        public SourceId Identity;
        public ContentHash Hash;
        public NodeId Exported;
    }

    static KindTable Table(out KindBootstrap.Ids boot, out ExternalKinds.Ids external)
    {
        KindTable table = KindBootstrap.CreateTable(out boot);
        external = ExternalKinds.Install(ref table);
        return table;
    }

    static Library MakeLibrary(in KindBootstrap.Ids boot, string label, string version)
    {
        Library lib = default;
        lib.Src = Source.Create(16);
        SourceBuild.MintIdentity(ref lib.Src);
        lib.Identity = lib.Src.Identity;

        NodeId root = SourceBuild.AddRoot(ref lib.Src, boot.Hole);
        SourceBuild.SetPayload(ref lib.Src, root, Encoding.UTF8.GetBytes(label));

        lib.Exported = SourceBuild.AddChild(ref lib.Src, root, boot.Hole);
        SourceBuild.SetPayload(ref lib.Src, lib.Exported, Encoding.UTF8.GetBytes("exported"));

        // Stands in for the hash of the bytes it was published as. The pin is checked
        // against this, so a different version is simply a different hash.
        lib.Hash = ContentHash.Of(Encoding.UTF8.GetBytes(label + "@" + version));
        return lib;
    }

    static External Pointing(in Library lib, bool pinned = true)
    {
        External e = default;
        e.Source = lib.Identity;
        e.Node = lib.Exported.Index;
        e.Hash = pinned ? lib.Hash : default;
        e.Pin = pinned ? "1.0.0" : null;
        e.Locator = "libs/geometry.ss";
        return e;
    }

    [Fact]
    public void An_edge_reaching_another_document_answers_with_the_node_it_names()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);

        NodeId user = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId hop = ExternalKinds.Declare(ref src, in ids, root, Pointing(in lib));

        RelationId uses = KindTable.InternRelation(
            ref table, new QualifiedName(default, "has-type"));
        SourceBuild.AddRef(ref src, user, uses, hop);

        Workspace.Set set = Workspace.Create();
        int mine = Workspace.Add(ref set, src.Identity, default, in src);
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        CrossRef found = Workspace.Follow(in set, mine, in src, ids.ExternalRef, user, uses);

        Assert.Equal(CrossResolution.Bound, found.How);
        Assert.Equal(lib.Exported, found.Node);
        Assert.Equal(hop, found.From);
        Assert.Equal("exported",
            Encoding.UTF8.GetString(
                SourceWalk.Payload(in set.Sources[found.Source].Src, found.Node)));
    }

    [Fact]
    public void An_edge_that_stays_home_answers_the_same_way()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);

        NodeId user = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId here = SourceBuild.AddChild(ref src, root, boot.Hole);
        SourceBuild.SetPayload(ref src, here, Encoding.UTF8.GetBytes("here"));

        RelationId uses = KindTable.InternRelation(
            ref table, new QualifiedName(default, "has-type"));
        SourceBuild.AddRef(ref src, user, uses, here);

        Workspace.Set set = Workspace.Create();
        int mine = Workspace.Add(ref set, src.Identity, default, in src);

        CrossRef found = Workspace.Follow(in set, mine, in src, ids.ExternalRef, user, uses);

        Assert.Equal(CrossResolution.Bound, found.How);
        Assert.Equal(mine, found.Source);
        Assert.Equal(here, found.Node);
    }

    [Fact]
    public void An_edge_reaching_a_document_nobody_has_is_a_hole()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);
        NodeId user = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId hop = ExternalKinds.Declare(ref src, in ids, root, Pointing(in lib));

        RelationId uses = KindTable.InternRelation(
            ref table, new QualifiedName(default, "has-type"));
        SourceBuild.AddRef(ref src, user, uses, hop);

        Workspace.Set set = Workspace.Create();
        int mine = Workspace.Add(ref set, src.Identity, default, in src);

        CrossRef found = Workspace.Follow(in set, mine, in src, ids.ExternalRef, user, uses);

        Assert.Equal(CrossResolution.DocumentMissing, found.How);
        Assert.Equal(hop, found.From);
    }

    [Fact]
    public void A_reader_follows_an_edge_out_of_its_own_source_and_keeps_reading()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);
        NodeId user = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId hop = ExternalKinds.Declare(ref src, in ids, root, Pointing(in lib));

        RelationId uses = KindTable.InternRelation(
            ref table, new QualifiedName(default, "has-type"));
        SourceBuild.AddRef(ref src, user, uses, hop);

        Workspace.Set set = Workspace.Create();
        int mine = Workspace.Add(ref set, src.Identity, default, in src);
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        Reach reach = Reach.Of(in set, mine, ids.ExternalRef);
        Reach.At found = Reach.To(in reach, in src, Reach.Here(in reach, user), uses);

        Assert.True(found.IsSome);
        Assert.Equal("exported",
            Encoding.UTF8.GetString(
                SourceWalk.Payload(in Reach.In(in reach, in src, in found), found.Node)));
    }

    [Fact]
    public void A_reader_with_one_source_reads_it_the_way_it_always_did()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);
        NodeId user = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId here = SourceBuild.AddChild(ref src, root, boot.Hole);
        SourceBuild.SetPayload(ref src, here, Encoding.UTF8.GetBytes("here"));

        RelationId uses = KindTable.InternRelation(
            ref table, new QualifiedName(default, "has-type"));
        SourceBuild.AddRef(ref src, user, uses, here);

        Reach reach = Reach.Alone(in src);
        Reach.At found = Reach.To(in reach, in src, Reach.Here(in reach, user), uses);

        Assert.True(found.IsSome);
        Assert.Equal(here, found.Node);
        Assert.Equal("here",
            Encoding.UTF8.GetString(
                SourceWalk.Payload(in Reach.In(in reach, in src, in found), found.Node)));
    }

    [Fact]
    public void A_reference_into_another_document_binds_through_its_minted_identity()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        CrossRef resolved = Workspace.Resolve(in set, Pointing(in lib));

        Assert.Equal(CrossResolution.Bound, resolved.How);
        Assert.True(resolved.Pinned);
        Assert.Equal(lib.Exported, resolved.Node);
        Assert.Equal("exported",
            Encoding.UTF8.GetString(
                SourceWalk.Payload(in set.Sources[resolved.Source].Src, resolved.Node)));
    }

    [Fact]
    public void Moving_or_renaming_the_file_does_not_break_the_reference()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        External moved = Pointing(in lib);
        moved.Locator = "somewhere/else/entirely/renamed.ss";

        Assert.Equal(CrossResolution.Bound, Workspace.Resolve(in set, in moved).How);
    }

    [Fact]
    public void A_document_that_is_not_here_is_a_hole_rather_than_a_failure()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        // An empty workspace: offline, or the library simply was not vendored.
        Workspace.Set set = Workspace.Create();

        CrossRef resolved = Workspace.Resolve(in set, Pointing(in lib));
        Assert.Equal(CrossResolution.DocumentMissing, resolved.How);
    }

    [Fact]
    public void The_wrong_version_is_a_hole_not_a_substitution()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library asked = MakeLibrary(in boot, "geometry", "1.0.0");
        Library present = MakeLibrary(in boot, "geometry", "2.0.0");

        // Same identity — it is the same library — but a different published version.
        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, asked.Identity, present.Hash, in present.Src);

        CrossRef resolved = Workspace.Resolve(in set, Pointing(in asked));
        Assert.Equal(CrossResolution.VersionMismatch, resolved.How);
    }

    [Fact]
    public void A_target_deleted_upstream_is_a_hole()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");
        External pointing = Pointing(in lib);

        SourceBuild.Delete(ref lib.Src, lib.Exported);

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        Assert.Equal(CrossResolution.NodeMissing, Workspace.Resolve(in set, in pointing).How);
    }

    [Fact]
    public void Unpinned_identity_with_multiple_documents_is_detected_rather_than_guessed()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");
        Library impostor = MakeLibrary(in boot, "not-geometry", "9.9.9");

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);
        Workspace.Add(ref set, lib.Identity, impostor.Hash, in impostor.Src);

        Assert.Equal(CrossResolution.IdentityCollision,
            Workspace.Resolve(in set, Pointing(in lib, pinned: false)).How);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pinned_versions_of_one_library_resolve_independently_of_load_order(bool reverse)
    {
        Table(out var boot, out _);
        Library first = MakeLibrary(in boot, "geometry", "1.0.0");
        Library second = MakeLibrary(in boot, "geometry", "2.0.0");
        second.Identity = first.Identity;
        second.Src.Identity = first.Identity;
        // The later version need not preserve a declaration's local node number.
        second.Exported = SourceBuild.AddChild(ref second.Src, second.Exported, boot.Hole);
        Workspace.Set set = Workspace.Create();
        if (reverse)
        {
            Workspace.Add(ref set, second.Identity, second.Hash, in second.Src);
            Workspace.Add(ref set, first.Identity, first.Hash, in first.Src);
        }
        else
        {
            Workspace.Add(ref set, first.Identity, first.Hash, in first.Src);
            Workspace.Add(ref set, second.Identity, second.Hash, in second.Src);
        }
        var oldReference = Pointing(in first);
        var newReference = Pointing(in second);
        var oldTarget = Workspace.Resolve(in set, in oldReference);
        var newTarget = Workspace.Resolve(in set, in newReference);
        Assert.True(oldTarget.IsBound); Assert.True(newTarget.IsBound);
        Assert.Equal(first.Exported, oldTarget.Node); Assert.Equal(second.Exported, newTarget.Node);
        Assert.Equal(first.Hash, set.Sources[oldTarget.Source].Hash);
        Assert.Equal(second.Hash, set.Sources[newTarget.Source].Hash);
        oldReference.Hash = ContentHash.Of(Encoding.UTF8.GetBytes("not-loaded"));
        Assert.Equal(CrossResolution.VersionMismatch, Workspace.Resolve(in set, in oldReference).How);
    }

    [Fact]
    public void A_pin_does_not_choose_between_duplicate_matching_candidates()
    {
        Table(out var boot, out _);
        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");
        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);
        Assert.Equal(CrossResolution.IdentityCollision, Workspace.Resolve(in set, Pointing(in lib)).How);
    }

    [Fact]
    public void An_unpinned_reference_resolves_and_reports_that_it_is_unpinned()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library lib = MakeLibrary(in boot, "geometry", "1.0.0");

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, lib.Identity, lib.Hash, in lib.Src);

        CrossRef resolved = Workspace.Resolve(in set, Pointing(in lib, pinned: false));

        Assert.Equal(CrossResolution.Bound, resolved.How);
        Assert.False(resolved.Pinned);
    }

    [Fact]
    public void Linking_a_document_answers_every_external_reference_it_holds()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Library present = MakeLibrary(in boot, "geometry", "1.0.0");
        Library absent = MakeLibrary(in boot, "physics", "1.0.0");

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);

        ExternalKinds.Declare(ref src, in ids, root, Pointing(in present));
        ExternalKinds.Declare(ref src, in ids, root, Pointing(in absent));

        Workspace.Set set = Workspace.Create();
        Workspace.Add(ref set, present.Identity, present.Hash, in present.Src);

        CrossRef[] links = null;
        int count = Workspace.LinkAll(in set, in src, in table, ids.ExternalRef, ref links);

        Assert.Equal(2, count);
        Assert.Equal(CrossResolution.Bound, links[0].How);
        Assert.Equal(CrossResolution.DocumentMissing, links[1].How);

        // Each answer names the node that asked, so an editor can mark the right one.
        Assert.True(links[0].From.IsSome);
        Assert.NotEqual(links[0].From, links[1].From);
    }

    [Fact]
    public void A_document_with_no_external_references_costs_nothing()
    {
        KindBootstrap.Ids boot;
        ExternalKinds.Ids ids;
        KindTable table = Table(out boot, out ids);

        Source src = Source.Create(16);
        SourceBuild.MintIdentity(ref src);
        NodeId root = SourceBuild.AddRoot(ref src, boot.Hole);
        NodeId a = SourceBuild.AddChild(ref src, root, boot.Hole);
        NodeId b = SourceBuild.AddChild(ref src, root, boot.Hole);
        SourceBuild.AddRef(ref src, a, KindTable.InternRelation(ref table,
            new QualifiedName(SourceId.Mint(), "mentions")), b);

        Workspace.Set empty = Workspace.Create();

        CrossRef[] links = null;
        Assert.Equal(0, Workspace.LinkAll(in empty, in src, in table, ids.ExternalRef, ref links));
    }
}
