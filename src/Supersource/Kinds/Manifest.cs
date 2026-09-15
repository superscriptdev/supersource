using System;

namespace Supersource.Kinds;
public struct ManifestEntry
{
    public NodeId Node;
    public External External;

    /// <summary>True when the reference names another source rather than a binary.</summary>
    public bool IsSource;

    /// <summary>Whether the external reference supplies a version pin.</summary>
    public bool Pinned;
}

/// <summary>
/// Queries external references from the graph. A stored manifest is a cache, not an independent
/// source of dependency data.
/// </summary>
public static class Manifest
{
    public static int Compute(
        in Source src,
        in ExternalKinds.Ids ids,
        ref ManifestEntry[] into)
    {
        if (into == null)
        {
            into = new ManifestEntry[8];
        }

        int found = 0;

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != ids.ExternalRef)
            {
                continue;
            }

            External external;
            if (!ExternalKinds.TryDecode(SourceWalk.Payload(in src, node), out external))
            {
                continue;
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }

            into[found].Node = node;
            into[found].External = external;
            into[found].IsSource = !external.Source.IsNone;
            into[found].Pinned = !string.IsNullOrEmpty(external.Pin);
            found++;
        }

        return found;
    }

    /// <summary>Reports external references without a pin; does not reject them.</summary>
    public static int Unpinned(
        in Source src,
        in ExternalKinds.Ids ids,
        ref NodeId[] into)
    {
        ManifestEntry[] entries = null;
        int count = Compute(in src, in ids, ref entries);

        if (into == null)
        {
            into = new NodeId[4];
        }

        int found = 0;
        for (int i = 0; i < count; i++)
        {
            if (entries[i].Pinned)
            {
                continue;
            }
            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = entries[i].Node;
            found++;
        }
        return found;
    }

    /// <summary>Checks fetched bytes against the reference’s content hash.</summary>
    public static bool Verify(in External external, ReadOnlySpan<byte> fetched)
    {
        return !external.Hash.IsNone && ContentHash.Of(fetched) == external.Hash;
    }

    /// <summary>Counts linked external dependencies.</summary>
    public static int LinkedCount(in Source src, in ExternalKinds.Ids ids)
    {
        ManifestEntry[] entries = null;
        return Compute(in src, in ids, ref entries);
    }
}
