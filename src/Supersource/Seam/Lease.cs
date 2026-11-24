using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supersource.Seam;

/// <summary>
/// The lease a server keeps beside a source's file while it holds the source (Quire
/// docs/sessions.md, "Outside tools write through the session"): who holds it, the loopback port
/// its server listens on, the token a tool sends, and when it ends unless renewed, in Unix
/// milliseconds. An expired lease is ignored, so a server that stops holds the file for no longer
/// than the expiry. The holder is text a person reads, and the format gives it no meaning.
/// </summary>
public readonly record struct Lease(string Holder, int Port, byte[] Token, long Expires)
{
    /// <summary>The lease file's first line, which names its version.</summary>
    public const string VersionLine = "superscript-lease 1";

    /// <summary>Where the lease of a source's file is: beside it, named for it.</summary>
    public static string PathOf(string source) => source + ".lease";

    /// <summary>Whether the lease still holds at a moment, in Unix milliseconds.</summary>
    public bool LiveAt(long now) => Expires > now;

    /// <summary>
    /// The live lease beside a source's file. False when there is no lease file, it cannot be read,
    /// it is of another version, or it has expired.
    /// </summary>
    public static bool Find(string source, out Lease lease)
    {
        lease = default;
        string path = PathOf(source);
        string text;
        try
        {
            if (!File.Exists(path)) return false;
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return false;
        }
        return TryRead(text, out lease) && lease.LiveAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// A lease file's text read back: its version line, then `holder`, `address 127.0.0.1:port`,
    /// `token` in lower-case hexadecimal and `expires`, one to a line. False when a line is missing
    /// or says something a lease does not.
    /// </summary>
    public static bool TryRead(string text, out Lease lease)
    {
        lease = default;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0] != VersionLine) return false;
        string holder = null;
        int port = -1;
        byte[] token = null;
        long expires = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            if (line.StartsWith("holder ", StringComparison.Ordinal)) holder = line.Substring(7);
            else if (line.StartsWith("address 127.0.0.1:", StringComparison.Ordinal))
            {
                if (!int.TryParse(line.AsSpan(18), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535) return false;
            }
            else if (line.StartsWith("token ", StringComparison.Ordinal))
            {
                token = Hex(line.Substring(6));
                if (token == null) return false;
            }
            else if (line.StartsWith("expires ", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan(8), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out expires)) return false;
            }
            else return false;
        }
        if (holder == null || port < 0 || token == null || expires < 0) return false;
        lease = new Lease(holder, port, token, expires);
        return true;
    }

    /// <summary>The lease as its file says it, the inverse of <see cref="TryRead"/>.</summary>
    public string Text()
    {
        var b = new StringBuilder();
        b.Append(VersionLine).Append('\n');
        b.Append("holder ").Append(Holder).Append('\n');
        b.Append("address 127.0.0.1:").Append(Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("token ").Append(Convert.ToHexString(Token).ToLowerInvariant()).Append('\n');
        b.Append("expires ").Append(Expires.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return b.ToString();
    }

    // Pairs of lower-case hexadecimal digits as bytes, or null.
    static byte[] Hex(string digits)
    {
        if (digits.Length % 2 != 0) return null;
        var bytes = new byte[digits.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int high = Digit(digits[2 * i]), low = Digit(digits[2 * i + 1]);
            if (high < 0 || low < 0) return null;
            bytes[i] = (byte)(high * 16 + low);
        }
        return bytes;
    }

    static int Digit(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
}
