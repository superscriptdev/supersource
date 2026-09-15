using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Supersource.Kinds;
/// <summary>SHA-256 digest used to identify external content independently of its locator.</summary>
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

/// <summary>What an external reference points at, decoded from its payload.</summary>
public struct External
{
    /// <summary>Fetch locator; source identity and content hash determine the target.</summary>
    public string Locator;

    /// <summary>What it is. The identity.</summary>
    public ContentHash Hash;

    /// <summary>Optional version label carried by the external reference.</summary>
    public string Pin;

    /// <summary>Target source identity, when referring to a source rather than an external binary.</summary>
    public SourceId Source;

    /// <summary>Node ID local to the target source.</summary>
    public long Node;
}

/// <summary>
/// Encodes external references as nodes carrying locator, hash and target information. Ordinary
/// local edges point at these nodes.
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

    /// <summary>A reference to something outside this source. Payload is the encoded External.</summary>
    public static readonly QualifiedName ExternalRef = N("external-ref");

    /// <summary>Kind name for a derived dependency manifest. The graph remains authoritative.</summary>
    public static readonly QualifiedName Manifest = N("manifest");

    /// <summary>Placeholder kind for an unresolved external reference.</summary>
    public static readonly QualifiedName Unresolved = N("unresolved");

    public struct Ids
    {
        public KindId ExternalRef;
        public KindId Manifest;
        public KindId Unresolved;
    }

    public static Ids Install(ref KindTable table)
    {
        Ids ids;
        ids.ExternalRef = KindTable.Intern(ref table, ExternalRef);
        ids.Manifest = KindTable.Intern(ref table, Manifest);
        ids.Unresolved = KindTable.Intern(ref table, Unresolved);

        // Published record layout for external reference metadata.
        KindTable.DefineSchema(ref table, ids.ExternalRef,
            StructuralFact.Declaration, PayloadFormat.Record, DisplayShape.Leaf,
            0x5588BBFF, default);

        KindTable.DefineSchema(ref table, ids.Manifest,
            StructuralFact.None, PayloadFormat.None, DisplayShape.Block,
            0x777777FF, default);

        // Unresolved references retain their label.
        KindTable.DefineSchema(ref table, ids.Unresolved,
            StructuralFact.None, PayloadFormat.Utf8, DisplayShape.Leaf,
            0xCC4444FF, default);

        return ids;
    }

    public static byte[] Encode(in External external)
    {
        byte[] locator = Encoding.UTF8.GetBytes(external.Locator ?? string.Empty);
        byte[] pin = Encoding.UTF8.GetBytes(external.Pin ?? string.Empty);

        byte[] buffer = new byte[32 + 16 + 8 + 2 + locator.Length + 2 + pin.Length];
        int at = 0;

        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Hash.A); at += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Hash.B); at += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Hash.C); at += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Hash.D); at += 8;

        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Source.Hi); at += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Source.Lo); at += 8;

        // Sixty-four bits, like every other node id: the source it names has the same id
        // space this one does.
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(buffer, at, 8), external.Node); at += 8;

        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)locator.Length); at += 2;
        Array.Copy(locator, 0, buffer, at, locator.Length); at += locator.Length;

        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), (ushort)pin.Length); at += 2;
        Array.Copy(pin, 0, buffer, at, pin.Length);

        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out External external)
    {
        external = default;
        if (payload.Length < 32 + 16 + 8 + 2)
        {
            return false;
        }

        int at = 0;
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        ulong c = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        ulong d = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        external.Hash = new ContentHash(a, b, c, d);

        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8)); at += 8;
        external.Source = new SourceId(hi, lo);

        external.Node = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(at, 8)); at += 8;

        int locatorLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2)); at += 2;
        if (at + locatorLength + 2 > payload.Length)
        {
            return false;
        }
        external.Locator = Encoding.UTF8.GetString(payload.Slice(at, locatorLength)); at += locatorLength;

        int pinLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2)); at += 2;
        if (at + pinLength > payload.Length)
        {
            return false;
        }
        external.Pin = Encoding.UTF8.GetString(payload.Slice(at, pinLength));

        return true;
    }

    /// <summary>Add an external reference node. Everything else points at it with an ordinary edge.</summary>
    public static NodeId Declare(
        ref Source src,
        in Ids ids,
        NodeId parent,
        in External external)
    {
        NodeId node = SourceBuild.AddChild(ref src, parent, ids.ExternalRef);
        SourceBuild.SetPayload(ref src, node, Encode(in external));
        return node;
    }
}
