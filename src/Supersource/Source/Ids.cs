using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Supersource;

/// <summary>
/// A 128-bit source identity used by external references. Internal node IDs are local. Minting is
/// probabilistic; callers must handle sources claiming the same identity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SourceId : IEquatable<SourceId>
{
    public readonly ulong Hi;
    public readonly ulong Lo;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SourceId(ulong hi, ulong lo)
    {
        Hi = hi;
        Lo = lo;
    }

    /// <summary>An unminted source. Legal — a source only needs an identity once something outside it points in.</summary>
    public static SourceId None
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return new SourceId(0, 0); }
    }

    public bool IsNone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return Hi == 0 && Lo == 0; }
    }

    /// <summary>Mints a cryptographically random source identity.</summary>
    public static SourceId Mint()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        ulong hi = 0;
        ulong lo = 0;
        for (int i = 0; i < 8; i++)
        {
            hi = (hi << 8) | bytes[i];
            lo = (lo << 8) | bytes[i + 8];
        }

        // Zero is the None sentinel; the odds of drawing it are not worth a branch
        // elsewhere, but they are not zero either.
        if (hi == 0 && lo == 0)
        {
            lo = 1;
        }

        return new SourceId(hi, lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(SourceId other)
    {
        return Hi == other.Hi && Lo == other.Lo;
    }

    public override bool Equals(object obj)
    {
        return obj is SourceId other && Hi == other.Hi && Lo == other.Lo;
    }

    public override int GetHashCode()
    {
        ulong mixed = Hi ^ Lo;
        return (int)(mixed ^ (mixed >> 32));
    }

    public static bool operator ==(SourceId a, SourceId b)
    {
        return a.Hi == b.Hi && a.Lo == b.Lo;
    }

    public static bool operator !=(SourceId a, SourceId b)
    {
        return a.Hi != b.Hi || a.Lo != b.Lo;
    }

    public override string ToString()
    {
        return Hi.ToString("x16") + Lo.ToString("x16");
    }
}

/// <summary>
/// A durable, source-local slot index. Allocated IDs are never reassigned after deletion.
/// Compaction preserves IDs and retained deleted slots.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NodeId : IEquatable<NodeId>
{
    /// <summary>Stored as index + 1 so default(NodeId) is None rather than node zero.</summary>
    readonly long biased;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NodeId(long index)
    {
        biased = index + 1;
    }

    /// <summary>Slot in Source.Nodes. -1 when None.</summary>
    public long Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased - 1; }
    }

    /// <summary>No node. A reference holding None is unbound. Equal to `default`.</summary>
    public static NodeId None
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return default; }
    }

    public bool IsNone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased <= 0; }
    }

    public bool IsSome
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased > 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(NodeId other)
    {
        return biased == other.biased;
    }

    public override bool Equals(object obj)
    {
        return obj is NodeId other && biased == other.biased;
    }

    public override int GetHashCode()
    {
        return (int)(biased ^ (biased >> 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(NodeId a, NodeId b)
    {
        return a.biased == b.biased;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(NodeId a, NodeId b)
    {
        return a.biased != b.biased;
    }

    public override string ToString()
    {
        return Index < 0 ? "none" : "n" + Index.ToString();
    }
}

/// <summary>
/// A handle into one KindTable. Remap by qualified name when comparing or combining independently
/// loaded sources.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct KindId : IEquatable<KindId>
{
    /// <summary>Stored as handle + 1 so default(KindId) is Unknown rather than handle zero.</summary>
    readonly int biased;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KindId(int value)
    {
        biased = value + 1;
    }

    /// <summary>Interned handle. -1 when Unknown.</summary>
    public int Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased - 1; }
    }

    /// <summary>An unresolved kind handle. Nodes carrying it remain storable.</summary>
    public static KindId Unknown
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return default; }
    }

    public bool IsUnknown
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased <= 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(KindId other)
    {
        return biased == other.biased;
    }

    public override bool Equals(object obj)
    {
        return obj is KindId other && biased == other.biased;
    }

    public override int GetHashCode()
    {
        return biased;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(KindId a, KindId b)
    {
        return a.biased == b.biased;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(KindId a, KindId b)
    {
        return a.biased != b.biased;
    }

    public override string ToString()
    {
        return Value < 0 ? "unknown" : "k" + Value.ToString();
    }
}

/// <summary>
/// A relation handle local to one KindTable, with a qualified name for remapping. Containment uses
/// child runs and parent links rather than reference edges.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RelationId : IEquatable<RelationId>
{
    /// <summary>Stored as handle + 1 so default(RelationId) is Unknown.</summary>
    readonly int biased;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RelationId(int value)
    {
        biased = value + 1;
    }

    /// <summary>Unbiased table handle, or -1 for Unknown.</summary>
    public int Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased - 1; }
    }

    /// <summary>Unresolved relation handle.</summary>
    public static RelationId Unknown
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return default; }
    }

    public bool IsUnknown
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return biased <= 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(RelationId other)
    {
        return biased == other.biased;
    }

    public override bool Equals(object obj)
    {
        return obj is RelationId other && biased == other.biased;
    }

    public override int GetHashCode()
    {
        return biased;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(RelationId a, RelationId b)
    {
        return a.biased == b.biased;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(RelationId a, RelationId b)
    {
        return a.biased != b.biased;
    }

    public override string ToString()
    {
        return Value < 0 ? "unknown" : "r" + Value.ToString();
    }
}
