using System;
using Supersource.Kinds;

namespace Supersource.Derive;

/// <summary>The reproducibility claim stored on a derivation. Applications are responsible for verifying it.</summary>
public enum Tier : byte
{
    /// <summary>Claims identical output bytes for identical inputs across machines.</summary>
    BitExact = 0,

    /// <summary>Identifies the actual output by its hash; a different output produces a different version.</summary>
    ContentAddressed = 1,

    /// <summary>Stores a captured result with provenance, without claiming it can be reproduced.</summary>
    Captured = 2,
}

/// <summary>
/// Kinds and relations for transform inputs, outputs and provenance. Executing transforms belongs
/// to the application.
/// </summary>
public static class DeriveKinds
{
    /// <summary>ASCII "Superscript_derv".</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F64657276UL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Library, name);
    }

    /// <summary>
    /// A derivation: references its inputs and its transform, and carries its declared
    /// reproducibility tier as payload.
    /// </summary>
    public static readonly QualifiedName Derivation = N("derivation");

    /// <summary>A build target whose transform produces an artifact from an entry point.</summary>
    public static readonly QualifiedName BuildTarget = N("build-target");

    /// <summary>What a derivation consumes.</summary>
    public static readonly QualifiedName Input = N("input");

    /// <summary>Derivation → its transform function.</summary>
    public static readonly QualifiedName Transform = N("transform");

    /// <summary>Artifact → the derivation that produced it.</summary>
    public static readonly QualifiedName ProducedBy = N("produced-by");

    /// <summary>Build target → its entry-point declaration.</summary>
    public static readonly QualifiedName EntryPoint = N("entry-point");

    /// <summary>
    /// Generated node → its input key. Regeneration matches this reference within one generator’s
    /// outputs to preserve existing node IDs.
    /// </summary>
    public static readonly QualifiedName GeneratedFrom = N("generated-from");

    public struct Ids
    {
        public KindId Derivation;
        public KindId BuildTarget;
        public RelationId Input;
        public RelationId Transform;
        public RelationId ProducedBy;
        public RelationId EntryPoint;
        public RelationId GeneratedFrom;
    }

    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.Input = KindTable.InternRelation(ref table, Input);
        ids.Transform = KindTable.InternRelation(ref table, Transform);
        ids.ProducedBy = KindTable.InternRelation(ref table, ProducedBy);
        ids.EntryPoint = KindTable.InternRelation(ref table, EntryPoint);
        ids.GeneratedFrom = KindTable.InternRelation(ref table, GeneratedFrom);

        ids.Derivation = KindTable.Intern(ref table, Derivation);
        ids.BuildTarget = KindTable.Intern(ref table, BuildTarget);

        // The payload stores one byte naming the claimed reproducibility tier.
        KindTable.DefineSchema(
            ref table, ids.Derivation,
            StructuralFact.None,
            PayloadFormat.Record,
            DisplayShape.Block,
            0,
            new RelationId[] { ids.Input, ids.Transform });

        KindTable.DefineSchema(
            ref table, ids.BuildTarget,
            StructuralFact.None,
            PayloadFormat.Record,
            DisplayShape.Block,
            0,
            new RelationId[] { ids.EntryPoint, ids.Transform });

        return ids;
    }

    /// <summary>
    /// Identifies this vocabulary’s output provenance relations. Applications can permit
    /// these explicitly when checking relations attached by their generation helpers.
    /// </summary>
    public static bool IsProvenance(in KindTable table, RelationId relation)
    {
        if (relation.IsUnknown)
        {
            return false;
        }

        QualifiedName name = KindTable.RelationNameOf(in table, relation);
        return name == ProducedBy || name == GeneratedFrom;
    }

    public static byte[] EncodeTier(Tier tier)
    {
        return new byte[] { (byte)tier };
    }

    public static Tier DecodeTier(ReadOnlySpan<byte> payload)
    {
        // An absent declaration must not imply the stronger BitExact claim.
        return payload.Length == 0 ? Tier.ContentAddressed : (Tier)payload[0];
    }
}

/// <summary>
/// Finds dependent derived content by walking provenance and containment. It reports affected nodes
/// without recomputing or deleting them.
/// </summary>
public static class Staleness
{
    /// <summary>
    /// Returns derived nodes downstream of changed, following input and provenance edges and
    /// containment. Builds a back-reference index; use the indexed overload for repeated queries.
    /// </summary>
    public static int Downstream(
        in Source src,
        in DeriveKinds.Ids ids,
        NodeId changed,
        ref NodeId[] into)
    {
        Backrefs index = default;
        Backrefs.Build(in src, ref index);
        return Downstream(in src, in ids, in index, changed, ref into);
    }

    /// <summary>Uses an existing back-reference index. Rejects an index from a different source.</summary>
    public static int Downstream(
        in Source src,
        in DeriveKinds.Ids ids,
        in Backrefs index,
        NodeId changed,
        ref NodeId[] into)
    {
        if (!Backrefs.IsCurrent(in index, in src))
        {
            throw new InvalidOperationException(
                "the reverse index was built from a different source; rebuild it first");
        }

        if (into == null)
        {
            into = new NodeId[16];
        }

        bool[] seen = new bool[src.NodeCount];
        NodeId[] queue = new NodeId[16];
        int head = 0;
        int tail = 0;

        queue[tail] = changed;
        tail++;
        if (changed.Index >= 0 && changed.Index < seen.Length)
        {
            seen[changed.Index] = true;
        }

        int found = 0;

        while (head < tail)
        {
            NodeId at = queue[head];
            head++;

            // Containment also propagates changes: a transform consuming a model depends on edits
            // to its children.
            ReadOnlySpan<Backref> edges = Backrefs.To(in index, at);
            NodeId parent = SourceWalk.Parent(in src, at);

            // Merged in slot order, so the answer does not depend on which of the two ways
            // in a node happened to arrive by.
            long container = parent.IsSome ? parent.Index : long.MaxValue;
            int e = 0;

            while (e < edges.Length || container != long.MaxValue)
            {
                long nextEdge = e < edges.Length ? edges[e].From.Index : long.MaxValue;
                long candidate;

                if (container <= nextEdge)
                {
                    candidate = container;
                    container = long.MaxValue;
                }
                else
                {
                    candidate = nextEdge;
                    if (!Carries(in src, in ids, edges[e]))
                    {
                        e++;
                        continue;
                    }
                    e++;
                }

                if (seen[candidate] || !src.IsLive(new NodeId(candidate)))
                {
                    continue;
                }

                seen[candidate] = true;

                if (tail == queue.Length)
                {
                    Array.Resize(ref queue, queue.Length == 0 ? 16 : queue.Length * 2);
                }
                queue[tail] = new NodeId(candidate);
                tail++;

                // Recipes propagate the change; only derived output nodes are reported.
                if (src.Nodes[candidate].IsDerived)
                {
                    if (found == into.Length)
                    {
                        Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
                    }
                    into[found] = new NodeId(candidate);
                    found++;
                }
            }
        }

        return found;
    }

    /// <summary>Whether this edge is one staleness travels along.</summary>
    static bool Carries(in Source src, in DeriveKinds.Ids ids, Backref edge)
    {
        RelationId relation = SourceWalk.RefAt(in src, edge.From, edge.RefIndex).Relation;
        return relation == ids.Input
            || relation == ids.ProducedBy
            || relation == ids.Transform
            || relation == ids.EntryPoint;
    }

    /// <summary>Returns the producing derivation, or None when no produced-by edge exists.</summary>
    public static NodeId ProducedBy(in Source src, in DeriveKinds.Ids ids, NodeId artifact)
    {
        return SourceWalk.Target(in src, artifact, ids.ProducedBy);
    }

    /// <summary>Returns the nodes referenced as derivation inputs.</summary>
    public static int Inputs(
        in Source src,
        in DeriveKinds.Ids ids,
        NodeId derivation,
        ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[8];
        }

        int found = 0;
        ReadOnlySpan<Ref> refs = SourceWalk.References(in src, derivation);
        for (int r = 0; r < refs.Length; r++)
        {
            if (refs[r].Relation != ids.Input || refs[r].IsUnbound)
            {
                continue;
            }
            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = refs[r].Target;
            found++;
        }
        return found;
    }

    /// <summary>Follows an artifact’s provenance chain through the first input of each derivation.</summary>
    public static int Provenance(
        in Source src,
        in DeriveKinds.Ids ids,
        NodeId artifact,
        ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[8];
        }

        int found = 0;
        NodeId at = artifact;

        while (true)
        {
            NodeId derivation = ProducedBy(in src, in ids, at);
            if (derivation.IsNone || !src.IsLive(derivation))
            {
                break;
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = derivation;
            found++;

            NodeId[] inputs = null;
            int count = Inputs(in src, in ids, derivation, ref inputs);
            if (count == 0)
            {
                break;
            }

            // Follow the first input upstream. A derivation with several inputs has a
            // provenance DAG rather than a chain; walking all of it is the caller's job,
            // and this is the spine.
            at = inputs[0];
        }

        return found;
    }
}

/// <summary>
/// Optional generation lifecycle helpers. Regenerate matches existing outputs by their
/// generated-from input references, retaining matched node IDs, allocating new outputs and
/// deleting unmatched generated outputs. Applications choose when to invoke this behavior.
/// Input keys establish correspondence; independently generated sources can allocate different
/// node IDs for the same keys.
/// </summary>
public static class Generation
{
    /// <summary>
    /// Reconciles outputs against input keys and returns the output count in key order. Retains
    /// matching nodes, creates missing nodes and deletes unmatched generated outputs. Does not
    /// write payloads.
    /// </summary>
    public static int Regenerate(
        ref Source src,
        in DeriveKinds.Ids ids,
        NodeId generator,
        NodeId parent,
        ReadOnlySpan<NodeId> keys,
        KindId kind,
        ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[keys.Length < 4 ? 4 : keys.Length];
        }

        // What this generator produced before. Matched by key, never by position.
        NodeId[] existing = null;
        int existingCount = Existing(in src, in ids, generator, ref existing);

        bool[] kept = new bool[existingCount];
        int found = 0;

        for (int i = 0; i < keys.Length; i++)
        {
            NodeId node = NodeId.None;

            for (int e = 0; e < existingCount; e++)
            {
                if (kept[e])
                {
                    continue;
                }
                if (KeyOf(in src, in ids, existing[e]) == keys[i])
                {
                    node = existing[e];
                    kept[e] = true;
                    break;
                }
            }

            if (node.IsNone)
            {
                // A key nobody has generated from before. Only here does anything allocate.
                node = SourceBuild.AddChild(ref src, parent, kind);
                src.Nodes[node.Index].Flags |= NodeFlags.Generated;
                SourceBuild.AddRef(ref src, node, ids.ProducedBy, generator);
                SourceBuild.AddRef(ref src, node, ids.GeneratedFrom, keys[i]);
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = node;
            found++;
        }

        // Delete outputs with removed keys, except materialized nodes whose Generated flag was
        // cleared.
        for (int e = 0; e < existingCount; e++)
        {
            if (!kept[e] && src.Nodes[existing[e].Index].IsGenerated)
            {
                SourceBuild.Delete(ref src, existing[e]);
            }
        }

        return found;
    }

    /// <summary>Every live node this generator produced, in slot order.</summary>
    public static int Existing(
        in Source src,
        in DeriveKinds.Ids ids,
        NodeId generator,
        ref NodeId[] into)
    {
        if (into == null)
        {
            into = new NodeId[8];
        }

        int found = 0;
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }
            if (SourceWalk.Target(in src, node, ids.ProducedBy) != generator)
            {
                continue;
            }

            if (found == into.Length)
            {
                Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
            }
            into[found] = node;
            found++;
        }
        return found;
    }

    /// <summary>Returns the input key referenced by generated-from, or None.</summary>
    public static NodeId KeyOf(in Source src, in DeriveKinds.Ids ids, NodeId node)
    {
        return SourceWalk.Target(in src, node, ids.GeneratedFrom);
    }

    /// <summary>Clears Derived and Generated flags while retaining provenance references.</summary>
    public static void Materialize(ref Source src, NodeId node)
    {

        src.Nodes[node.Index].Flags &= ~(NodeFlags.Generated | NodeFlags.Derived);
    }

    /// <summary>An optional sync convention: include nodes not marked Generated. Core persistence does not use this filter.</summary>
    public static bool IsSynced(in Source src, NodeId node)
    {
        return !src.Nodes[node.Index].IsGenerated;
    }

    /// <summary>An optional lifecycle convention: derived or generated nodes can be discarded by their producer.</summary>
    public static bool IsDisposable(in Source src, NodeId node)
    {
        return src.Nodes[node.Index].IsDerived || src.Nodes[node.Index].IsGenerated;
    }
}
