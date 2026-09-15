using System;
using Supersource.Kinds;

namespace Supersource.Resolve;
/// <summary>Outcome of resolving a cross-source reference.</summary>
public enum CrossResolution : byte
{
    /// <summary>Resolved to a live node in a source the workspace holds.</summary>
    Bound = 0,

    /// <summary>No registered source has the requested identity.</summary>
    DocumentMissing = 1,

    /// <summary>The identity is registered, but no entry matches the requested content hash.</summary>
    VersionMismatch = 2,

    /// <summary>The requested source version exists, but its target node is not live.</summary>
    NodeMissing = 3,

    /// <summary>More than one registered source matches the requested identity and optional hash.</summary>
    IdentityCollision = 4,
}

/// <summary>Resolution result containing a workspace index and a source-local node ID.</summary>
public struct CrossRef
{
    public CrossResolution How;

    /// <summary>Index into the workspace. Meaningful only when <see cref="How"/> is Bound.</summary>
    public int Source;

    /// <summary>Node ID local to the resolved source.</summary>
    public NodeId Node;

    /// <summary>The external-reference node in the *referring* source that this resolves.</summary>
    public NodeId From;

    /// <summary>
    /// Whether the external reference supplies a content hash. Unpinned references can still
    /// resolve.
    /// </summary>
    public bool Pinned;

    public bool IsBound
    {
        get { return How == CrossResolution.Bound; }
    }
}

/// <summary>A source address and workspace used to follow local and cross-source references.</summary>
public struct Reach
{
    public Workspace.Set Set;

    /// <summary>The source being read, as an index into <see cref="Set"/>.</summary>
    public int Home;

    /// <summary>The `external-ref` kind, which is the only thing an edge can land on that
    /// means "somewhere else".</summary>
    public KindId ExternalRef;

    public static Reach Of(in Workspace.Set set, int home, KindId externalRef)
    {
        Reach reach;
        reach.Set = set;
        reach.Home = home;
        reach.ExternalRef = externalRef;
        return reach;
    }

    /// <summary>
    /// A workspace of exactly one source, which is what a reader that has never heard of
    /// linking is already doing.
    /// </summary>
    public static Reach Alone(in Source src)
    {
        Workspace.Set set = Workspace.Create(1);
        int home = Workspace.Add(ref set, src.Identity, default, in src);

        return Of(in set, home, KindId.Unknown);
    }

    /// <summary>Where a node lives, once one has been followed to.</summary>
    public struct At
    {
        public int Source;
        public NodeId Node;

        public bool IsSome
        {
            get { return Node.IsSome; }
        }
    }

    /// <summary>
    /// Follows a reference using the current home source supplied by the caller. A registered copy
    /// of the Source struct may have stale counts and array references after edits.
    /// </summary>
    public static At To(in Reach reach, in Source home, in At from, RelationId relation)
    {
        At found = default;

        CrossRef across = from.Source == reach.Home
            ? Workspace.Follow(in reach.Set, from.Source, in home,
                               reach.ExternalRef, from.Node, relation)
            : Workspace.Follow(in reach.Set, from.Source,
                               in reach.Set.Sources[from.Source].Src,
                               reach.ExternalRef, from.Node, relation);

        if (!across.IsBound) { return found; }

        found.Source = across.Source;
        found.Node = across.Node;
        return found;
    }

    /// <summary>
    /// Returns the current home source for local addresses, otherwise the registered workspace
    /// source.
    /// </summary>
    public static ref readonly Source In(in Reach reach, in Source home, in At at)
    {
        if (at.Source == reach.Home) { return ref home; }

        return ref reach.Set.Sources[at.Source].Src;
    }

    /// <summary>Where the source being read starts: its root, as an <see cref="At"/>.</summary>
    public static At Here(in Reach reach, NodeId node)
    {
        At at;
        at.Source = reach.Home;
        at.Node = node;
        return at;
    }
}

/// <summary>One source the workspace holds, with the identity and hash it was loaded under.</summary>
public struct LoadedSource
{
    public SourceId Identity;

    /// <summary>SHA-256 of the bytes this was loaded from. What a pin is checked against.</summary>
    public ContentHash Hash;

    public Source Src;
}

/// <summary>
/// Resolves external-reference nodes against explicitly registered sources. Does not fetch data or
/// mutate unresolved references. Missing identities, versions, nodes and ambiguous registrations
/// remain distinct results.
/// </summary>
public static class Workspace
{
    public struct Set
    {
        public LoadedSource[] Sources;
        public int Count;
    }

    public static Set Create(int capacity = 4)
    {
        Set set;
        set.Sources = new LoadedSource[capacity < 1 ? 1 : capacity];
        set.Count = 0;
        return set;
    }

    /// <summary>
    /// Registers a Source under caller-supplied identity and hash. Does not verify that metadata
    /// against the original file bytes; the loader must do so.
    /// </summary>
    public static int Add(ref Set set, SourceId identity, ContentHash hash, in Source src)
    {
        if (set.Count == set.Sources.Length)
        {
            Array.Resize(ref set.Sources, set.Sources.Length == 0 ? 16 : set.Sources.Length * 2);
        }

        set.Sources[set.Count].Identity = identity;
        set.Sources[set.Count].Hash = hash;
        set.Sources[set.Count].Src = src;
        set.Count++;
        return set.Count - 1;
    }

    /// <summary>
    /// Follows a local edge, resolving through the workspace if its target is an external-reference
    /// node. Local targets return Bound with Pinned set; absent targets return DocumentMissing.
    /// </summary>
    public static CrossRef Follow(
        in Set set,
        int from,
        in Source src,
        KindId externalRef,
        NodeId node,
        RelationId relation)
    {
        CrossRef result = default;
        result.Node = SourceWalk.Target(in src, node, relation);

        if (result.Node.IsNone || !src.IsLive(result.Node))
        {
            result.How = CrossResolution.DocumentMissing;
            result.Source = -1;
            return result;
        }

        if (src.Nodes[result.Node.Index].Kind != externalRef)
        {
            result.How = CrossResolution.Bound;
            result.Source = from;
            result.Pinned = true;
            return result;
        }

        NodeId hop = result.Node;

        if (!ExternalKinds.TryDecode(SourceWalk.Payload(in src, hop), out External external))
        {
            result.How = CrossResolution.DocumentMissing;
            result.Source = -1;
            result.From = hop;
            return result;
        }

        CrossRef across = Resolve(in set, in external);
        across.From = hop;
        return across;
    }

    /// <summary>Resolves source identity, optional content hash and node ID against registered sources.</summary>
    public static CrossRef Resolve(in Set set, in External external)
    {
        CrossRef result = default;
        result.Pinned = !external.Hash.IsNone;

        int found = -1;
        int firstIdentity = -1;
        for (int i = 0; i < set.Count; i++)
        {
            if (!set.Sources[i].Identity.Equals(external.Source))
            {
                continue;
            }

            if (firstIdentity < 0) firstIdentity = i;
            // A source identity can have multiple published versions. A content pin selects
            // one of them; an unrelated version must not turn an exact match into a collision.
            if (result.Pinned && !set.Sources[i].Hash.Equals(external.Hash)) continue;

            if (found >= 0)
            {
                // Even after applying the pin there is more than one candidate. Do not
                // choose by load order (loaders may deduplicate identical byte snapshots).
                result.How = CrossResolution.IdentityCollision;
                return result;
            }

            found = i;
        }

        if (firstIdentity < 0)
        {
            result.How = CrossResolution.DocumentMissing;
            return result;
        }

        // Distinguish an absent identity from a registered identity with no matching version.
        if (found < 0)
        {
            result.How = CrossResolution.VersionMismatch;
            result.Source = firstIdentity;
            return result;
        }

        NodeId target = new NodeId(external.Node);
        if (external.Node < 0 || !set.Sources[found].Src.IsLive(target))
        {
            result.How = CrossResolution.NodeMissing;
            result.Source = found;
            return result;
        }

        result.How = CrossResolution.Bound;
        result.Source = found;
        result.Node = target;
        return result;
    }

    /// <summary>
    /// Resolves external references that name another source. Skips binary dependencies without a
    /// source identity.
    /// </summary>
    public static int LinkAll(
        in Set set,
        in Source src,
        in KindTable table,
        KindId externalRef,
        ref CrossRef[] into)
    {
        if (into == null)
        {
            into = new CrossRef[8];
        }

        int found = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            if (src.Nodes[i].Kind.Value != externalRef.Value)
            {
                continue;
            }

            External external;
            if (!ExternalKinds.TryDecode(SourceWalk.Payload(in src, node), out external))
            {
                // Unrecognized external payloads remain stored but cannot be resolved here.
                continue;
            }

            if (external.Source.IsNone)
            {
                continue;
            }

            CrossRef resolved = Resolve(in set, in external);
            resolved.From = node;

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }

            into[found] = resolved;
            found++;
        }

        return found;
    }
}
