using System;
using System.IO;

namespace Supersource;

/// <summary>A node's payload as a read-only, seekable stream over the source's own storage.</summary>
sealed class PayloadStream : Stream
{
    readonly Source src;
    readonly NodeId node;
    readonly long length;
    long position;

    public PayloadStream(Source src, NodeId node)
    {
        this.src = src;
        this.node = node;
        length = SourceWalk.PayloadLength(in src, node);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set => position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(new Span<byte>(buffer, offset, count));
    }

    public override int Read(Span<byte> into)
    {
        int count = (int)Math.Min(into.Length, length - position);
        if (count <= 0) return 0;
        SourceWalk.ReadPayload(in src, node, position, into.Slice(0, count));
        position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length + offset,
        };
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
