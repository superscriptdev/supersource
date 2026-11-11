using System;
using System.Buffers.Binary;
using System.Text;
using Supersource.Kinds;

namespace Supersource.Format;

/// <summary>
/// How a file of major version 4 or older wrote an external reference: one node carrying the
/// whole address of what it named, repeated for every reference into the same source.
///
/// <code>
/// external-ref  payload: hash(32) source(16) node(i64) locatorLength(u16) locator pinLength(u16) pin
/// </code>
///
/// Reading such a file gives each distinct source-and-version one dependency node under the
/// root, points every external reference at its dependency, and leaves the node id as its
/// payload. The free-text pin label is dropped; the hash is the pin. The hash is kept as written,
/// which for these files is a hash of the target's bytes: until it is repinned to the target's
/// content hash, a reference resolves as the wrong version rather than silently as the right one.
/// </summary>
static class LegacyExternals
{
    public static void Convert(ref SourceFile file)
    {
        KindId kind = KindTable.Lookup(in file.Table, ExternalKinds.ExternalRef);
        if (kind.IsUnknown)
        {
            return;
        }

        NodeId[] nodes = new NodeId[8];
        int count = 0;
        for (long i = 0; i < file.Src.NodeCount; i++)
        {
            if (file.Src.IsLive(new NodeId(i)) && file.Src.Nodes[i].Kind == kind)
            {
                if (count == nodes.Length)
                {
                    Array.Resize(ref nodes, count * 2);
                }
                nodes[count++] = new NodeId(i);
            }
        }

        ExternalKinds.Ids ids = ExternalKinds.Install(ref file.Table);
        for (int i = 0; i < count; i++)
        {
            if (!Decode(SourceWalk.Payload(in file.Src, nodes[i]), out External old))
            {
                continue;
            }

            NodeId dependency = ExternalKinds.DependencyFor(ref file.Src, in ids, old.Source, old.Hash, old.Locator);
            SourceBuild.SetPayload(ref file.Src, nodes[i], ExternalKinds.EncodeNode(old.Node));
            SourceBuild.InsertRef(ref file.Src, nodes[i], 0, Ref.Bound(ids.Of, dependency));
        }
    }

    static bool Decode(ReadOnlySpan<byte> payload, out External external)
    {
        external = default;
        if (payload.Length < 32 + 16 + 8 + 2)
        {
            return false;
        }

        external.Hash = new ContentHash(
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(24, 8)));
        external.Source = new SourceId(
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(32, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(40, 8)));
        external.Node = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(48, 8));

        int at = 56;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
        at += 2;
        if (at + length > payload.Length)
        {
            return false;
        }
        external.Locator = Encoding.UTF8.GetString(payload.Slice(at, length));
        return true;
    }
}
