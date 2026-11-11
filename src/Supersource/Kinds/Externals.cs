using System;

namespace Supersource.Kinds;
public struct ExternalUse
{
    public NodeId Node;
    public External External;

    /// <summary>True when the reference names another source rather than a foreign file.</summary>
    public bool IsSource;

    /// <summary>Whether its dependency carries a content hash, and so names one version.</summary>
    public bool Pinned;
}

/// <summary>Queries over the external references a source holds, computed from the graph.</summary>
public static class Externals
{
    public static int Compute(
        in Source src,
        in ExternalKinds.Ids ids,
        ref ExternalUse[] into)
    {
        if (into == null)
        {
            into = new ExternalUse[8];
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
            if (!ExternalKinds.TryDecode(in src, node, out external))
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
            into[found].Pinned = !external.Hash.IsNone;
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
        ExternalUse[] entries = null;
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

    /// <summary>Checks a fetched foreign file's bytes against the pin its dependency carries.</summary>
    public static bool Verify(in External external, ReadOnlySpan<byte> fetched)
    {
        return !external.Hash.IsNone && ContentHash.Of(fetched) == external.Hash;
    }

    /// <summary>Counts the external references whose payload and dependency decode.</summary>
    public static int LinkedCount(in Source src, in ExternalKinds.Ids ids)
    {
        ExternalUse[] entries = null;
        return Compute(in src, in ids, ref entries);
    }
}
