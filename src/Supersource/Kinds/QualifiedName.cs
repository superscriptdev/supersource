using System;

namespace Supersource.Kinds;
/// <summary>
/// A kind or relation’s library identity and local name. Unlike interned handles, qualified names
/// can be compared across independently loaded sources.
/// </summary>
public readonly struct QualifiedName : IEquatable<QualifiedName>
{
    /// <summary>Identity of the kind library source that declares this name.</summary>
    public readonly SourceId Library;

    /// <summary>Local name within the library; part of qualified identity.</summary>
    public readonly string Name;

    public QualifiedName(SourceId library, string name)
    {
        Library = library;
        Name = name;
    }

    public bool IsNone
    {
        get { return Name == null; }
    }

    public bool Equals(QualifiedName other)
    {
        return Library == other.Library && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is QualifiedName other && Equals(other);
    }

    /// <summary>FNV-1a over the library ID and name, stable across processes.</summary>
    public override int GetHashCode()
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        ulong hash = Offset;
        hash = Mix(hash, Library.Hi, Prime);
        hash = Mix(hash, Library.Lo, Prime);

        if (Name != null)
        {
            for (int i = 0; i < Name.Length; i++)
            {
                char c = Name[i];
                hash = (hash ^ (byte)(c & 0xFF)) * Prime;
                hash = (hash ^ (byte)(c >> 8)) * Prime;
            }
        }

        return (int)(hash ^ (hash >> 32));
    }

    static ulong Mix(ulong hash, ulong value, ulong prime)
    {
        for (int i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)(value >> (i * 8))) * prime;
        }
        return hash;
    }

    public static bool operator ==(QualifiedName a, QualifiedName b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(QualifiedName a, QualifiedName b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return Library.ToString() + "/" + (Name ?? "<none>");
    }
}
