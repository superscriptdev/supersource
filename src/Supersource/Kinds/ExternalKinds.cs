using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Supersource.Kinds;
/// <summary>
/// A SHA-256 that says which exact version something is: of a source, its state
/// (<see cref="Format.SourceHash"/>); of a foreign file, its bytes.
/// </summary>
public readonly struct ContentHash : IEquatable<ContentHash>
{
    public readonly ulong A, B, C, D;

    public ContentHash(ulong a, ulong b, ulong c, ulong d)
    {
        A = a; B = b; C = c; D = d;
    }

    public bool IsNone
    {
        get { return A == 0 && B == 0 && C == 0 && D == 0; }
    }

    public static ContentHash Of(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return From(digest);
    }

    /// <summary>Computes SHA-256 by streaming the file rather than loading it into memory.</summary>
    public static ContentHash OfFile(string path)
    {
        using (IncrementalHash running = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
        {
            byte[] window = new byte[1 << 16];
            for (;;)
            {
                int got = stream.Read(window, 0, window.Length);
                if (got <= 0)
                {
                    break;
                }
                running.AppendData(window, 0, got);
            }

            Span<byte> digest = stackalloc byte[32];
            running.GetHashAndReset(digest);
            return From(digest);
        }
    }

    /// <summary>A hash from the thirty-two bytes a SHA-256 produced.</summary>
    public static ContentHash FromDigest(ReadOnlySpan<byte> digest)
    {
        return From(digest);
    }

    static ContentHash From(ReadOnlySpan<byte> digest)
    {
        return new ContentHash(
            BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(0, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(24, 8)));
    }

    /// <summary>The thirty-two bytes, big-endian, as they came out of the hash.</summary>
    public void WriteTo(Span<byte> into)
    {
        BinaryPrimitives.WriteUInt64BigEndian(into.Slice(0, 8), A);
        BinaryPrimitives.WriteUInt64BigEndian(into.Slice(8, 8), B);
        BinaryPrimitives.WriteUInt64BigEndian(into.Slice(16, 8), C);
        BinaryPrimitives.WriteUInt64BigEndian(into.Slice(24, 8), D);
    }

    public bool Equals(ContentHash other)
    {
        return A == other.A && B == other.B && C == other.C && D == other.D;
    }

    public override bool Equals(object obj)
    {
        return obj is ContentHash other && Equals(other);
    }

    public override int GetHashCode()
    {
        ulong mixed = A ^ B ^ C ^ D;
        return (int)(mixed ^ (mixed >> 32));
    }

    public static bool operator ==(ContentHash a, ContentHash b) { return a.Equals(b); }
    public static bool operator !=(ContentHash a, ContentHash b) { return !a.Equals(b); }

    public override string ToString()
    {
        return A.ToString("x16") + B.ToString("x16") + C.ToString("x16") + D.ToString("x16");
    }
}

/// <summary>What an external reference points at: a node in another source, or a foreign file.</summary>
public struct External
{
    /// <summary>Where to fetch it. A hint only: the source id and the content hash say what it is.</summary>
    public string Locator;

    /// <summary>
    /// Which version, when it is pinned: for a source, the content hash of its state; for a
    /// foreign file, the hash of its bytes. None when unpinned.
    /// </summary>
    public ContentHash Hash;

    /// <summary>The source, when the target is a source rather than a foreign file.</summary>
    public SourceId Source;

    /// <summary>The node within that source.</summary>
    public long Node;
}

/// <summary>
/// References into other sources, written with two kinds. A **dependency** holds another
/// source's address once — its id, the content hash it is pinned to, and a locator — however
/// many references go there. An **external reference** stands for one node in a dependency: its
/// payload is the node id, and its first reference, `of`, names the dependency. Ordinary local
/// edges point at external references, so a reference into another source is drawn exactly like
/// a local one, and moving a pin to a new version changes one dependency.
///
/// Both are nodes the application places, the way a filesystem holds links among files. Two
/// versions of one source are two dependencies.
/// </summary>
public static class ExternalKinds
{
    /// <summary>ASCII "Superscript_extn".</summary>
    public static readonly SourceId Library =
        new SourceId(0x5375706572736372UL, 0x6970745F6578746EUL);

    static QualifiedName N(string name)
    {
        return new QualifiedName(Library, name);
    }

    /// <summary>One node in a dependency. Payload: the node id.</summary>
    public static readonly QualifiedName ExternalRef = N("external-ref");

    /// <summary>Another source, or a foreign file. Payload: its id and its pin, then the locator.</summary>
    public static readonly QualifiedName Dependency = N("dependency");

    /// <summary>External reference → its dependency.</summary>
    public static readonly QualifiedName Of = N("of");

    /// <summary>
    /// The dependency's fixed part: the source id, then the content hash, each as the bytes its
    /// hex spelling reads (big-endian), so the payload shows what every tool prints.
    /// </summary>
    const int AddressBytes = 16 + 32;

    public struct Ids
    {
        public KindId ExternalRef;
        public KindId Dependency;
        public RelationId Of;
    }

    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.Of = KindTable.InternRelation(ref table, Of);
        ids.ExternalRef = KindTable.Intern(ref table, ExternalRef);
        ids.Dependency = KindTable.Intern(ref table, Dependency);

        KindTable.DefineSchema(ref table, ids.ExternalRef, StructuralFact.None,
            PayloadFormat.One(PayloadElement.Of(8, PayloadField.Of("node", PayloadScalar.I64, 0))),
            new RelationId[] { ids.Of });

        KindTable.DefineSchema(ref table, ids.Dependency, StructuralFact.None,
            PayloadFormat.TextAfter(PayloadElement.Of(AddressBytes,
                PayloadField.Of("source", PayloadScalar.U8, 0, 16),
                PayloadField.Of("hash", PayloadScalar.U8, 16, 32))),
            default);

        return ids;
    }

    /// <summary>A dependency's payload.</summary>
    public static byte[] EncodeDependency(SourceId source, ContentHash hash, string locator)
    {
        byte[] where = Encoding.UTF8.GetBytes(locator ?? string.Empty);
        byte[] buffer = new byte[AddressBytes + where.Length];
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 0, 8), source.Hi);
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 8, 8), source.Lo);
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 16, 8), hash.A);
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 24, 8), hash.B);
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 32, 8), hash.C);
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, 40, 8), hash.D);
        Array.Copy(where, 0, buffer, AddressBytes, where.Length);
        return buffer;
    }

    public static bool TryDecodeDependency(
        ReadOnlySpan<byte> payload, out SourceId source, out ContentHash hash, out string locator)
    {
        source = default;
        hash = default;
        locator = null;
        if (payload.Length < AddressBytes)
        {
            return false;
        }

        source = new SourceId(
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(8, 8)));
        hash = new ContentHash(
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(24, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(32, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(40, 8)));
        locator = Encoding.UTF8.GetString(payload.Slice(AddressBytes));
        return true;
    }

    /// <summary>An external reference's payload: the node id in its dependency.</summary>
    public static byte[] EncodeNode(long node)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, node);
        return buffer;
    }

    /// <summary>
    /// What an external reference points at: its node id, and the address of the dependency its
    /// first reference names. False when either is missing or malformed.
    /// </summary>
    public static bool TryDecode(in Source src, NodeId externalRef, out External external)
    {
        external = default;
        if (!src.IsLive(externalRef))
        {
            return false;
        }

        if (SourceWalk.PayloadLength(in src, externalRef) != 8 || SourceWalk.RefCount(in src, externalRef) == 0)
        {
            return false;
        }
        ReadOnlySpan<byte> payload = SourceWalk.Payload(in src, externalRef);

        NodeId dependency = SourceWalk.RefAt(in src, externalRef, 0).Target;
        if (!src.IsLive(dependency) || !SourceWalk.IsContiguous(in src, dependency)
            || !TryDecodeDependency(SourceWalk.Payload(in src, dependency),
                out external.Source, out external.Hash, out external.Locator))
        {
            return false;
        }

        external.Node = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return true;
    }

    /// <summary>The dependency an external reference names, or none.</summary>
    public static NodeId DependencyOf(in Source src, NodeId externalRef)
    {
        return src.IsLive(externalRef) && SourceWalk.RefCount(in src, externalRef) > 0
            ? SourceWalk.RefAt(in src, externalRef, 0).Target
            : NodeId.None;
    }

    /// <summary>
    /// The dependency this source already has for that address, or none. The source id and the
    /// hash say what a dependency is, so a different locator for the same pair is the same
    /// dependency; only an unpinned foreign file, which has neither, is told apart by its locator.
    /// </summary>
    public static NodeId FindDependency(in Source src, in Ids ids, SourceId source, ContentHash hash, string locator)
    {
        bool byLocator = source.IsNone && hash.IsNone;
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != ids.Dependency || !SourceWalk.IsContiguous(in src, node))
            {
                continue;
            }
            if (TryDecodeDependency(SourceWalk.Payload(in src, node), out SourceId has, out ContentHash at, out string where)
                && has == source && at == hash
                && (!byLocator || string.Equals(where, locator ?? string.Empty, StringComparison.Ordinal)))
            {
                return node;
            }
        }
        return NodeId.None;
    }

    /// <summary>The dependency for that address, made under the root if this source has none yet.</summary>
    public static NodeId DependencyFor(ref Source src, in Ids ids, SourceId source, ContentHash hash, string locator)
    {
        NodeId found = FindDependency(in src, in ids, source, hash, locator);
        if (!found.IsNone)
        {
            return found;
        }

        NodeId made = SourceBuild.AddChild(ref src, src.Root, ids.Dependency);
        SourceBuild.SetPayload(ref src, made, EncodeDependency(source, hash, locator));
        return made;
    }

    /// <summary>Add an external reference node. Everything else points at it with an ordinary edge.</summary>
    public static NodeId Declare(
        ref Source src,
        in Ids ids,
        NodeId parent,
        in External external)
    {
        NodeId dependency = DependencyFor(ref src, in ids, external.Source, external.Hash, external.Locator);
        NodeId node = SourceBuild.AddChild(ref src, parent, ids.ExternalRef);
        SourceBuild.SetPayload(ref src, node, EncodeNode(external.Node));
        SourceBuild.AddRef(ref src, node, ids.Of, dependency);
        return node;
    }

    /// <summary>Points an existing external reference somewhere else, keeping its id and every edge into it.</summary>
    public static void Retarget(ref Source src, in Ids ids, NodeId externalRef, in External external)
    {
        NodeId dependency = DependencyFor(ref src, in ids, external.Source, external.Hash, external.Locator);
        SourceBuild.SetPayload(ref src, externalRef, EncodeNode(external.Node));
        if (SourceWalk.RefCount(in src, externalRef) > 0)
        {
            SourceBuild.RemoveRef(ref src, externalRef, 0);
        }
        SourceBuild.InsertRef(ref src, externalRef, 0, Ref.Bound(ids.Of, dependency));
    }
}
