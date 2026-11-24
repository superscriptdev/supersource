using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Supersource.Seam;

/// <summary>What the session did with an outside tool's write.</summary>
public enum SeamOutcome
{
    /// <summary>The operations applied, at <see cref="SeamAnswer.Revision"/>.</summary>
    Applied,
    /// <summary>They are held as proposal <see cref="SeamAnswer.Proposal"/>, for the reason the sentence gives.</summary>
    Held,
    /// <summary>They were refused, for the reason the sentence gives.</summary>
    Refused,
}

/// <summary>The session's answer to a write through the seam.</summary>
public readonly record struct SeamAnswer(SeamOutcome Outcome, long Revision, long Proposal, string Sentence);

/// <summary>Who writes through the seam, numbered as the session's wire numbers its participants.</summary>
public enum SeamWriter : long
{
    /// <summary>A model writing through the MCP tools, whose write is held until the session's person lets it write.</summary>
    Agent = 1,
    /// <summary>A program such as the command line.</summary>
    Tool = 2,
}

/// <summary>The session refused a tool's ask, in a sentence a person or a model can act on.</summary>
public sealed class SeamRefusal : Exception
{
    public SeamRefusal(string sentence) : base(sentence) { }
}

/// <summary>
/// An outside tool's connection to the session that holds a source (Quire docs/sessions.md,
/// "Outside tools write through the session"). `State` reads the source as it stands, and `Write`
/// sends the tool's operations as one journal entry. The connection never joins the session: the
/// session joins a writer as a participant under its name the first time it writes.
///
/// Every message is one frame: four little-endian bytes giving the length of what follows, a byte
/// naming its kind, then its fields, a word as eight little-endian bytes and a byte string as its
/// four-byte length and its bytes. The engine's sockets carry each send as one message with a
/// four-byte little-endian length of its own before it, so a frame goes out inside one such message,
/// and the session's answers are read out of the messages that arrive, a frame split across two of
/// them read whole.
/// </summary>
public sealed class SeamClient : IDisposable
{
    // The frame kinds, as the session numbers them.
    const byte StateAsk = 1, StateFrame = 2, Apply = 3, Applied = 4, Held = 5, Refused = 6;
    const int Header = 5;
    const int FrameLimit = 64 << 20;

    readonly TcpClient tcp;
    readonly NetworkStream stream;
    readonly Lease lease;

    SeamClient(TcpClient tcp, Lease lease)
    {
        this.tcp = tcp;
        this.lease = lease;
        stream = tcp.GetStream();
    }

    /// <summary>A connection to the server a lease names, on loopback. Waits as long as the timeout for each step.</summary>
    public static SeamClient Connect(Lease lease, int timeoutMs = 10000)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs, NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(IPAddress.Loopback, lease.Port).Wait(timeoutMs))
                throw new IOException("the session holding the source did not answer on port " + lease.Port);
        }
        catch (AggregateException error) when (error.InnerException is SocketException socket)
        {
            tcp.Dispose();
            throw new IOException("the session holding the source cannot be reached on port " + lease.Port + ": " + socket.Message.TrimEnd('.'));
        }
        return new SeamClient(tcp, lease);
    }

    /// <summary>The source as it stands: the session's revision, and the source as v5 bytes whose
    /// next id starts a block of ids kept for this tool.</summary>
    public (long Revision, byte[] Source) State()
    {
        var w = new Frame(StateAsk);
        w.Bytes(lease.Token);
        Send(w.Finish());
        byte[] frame = Receive();
        var r = new Fields(frame);
        if (frame[4] == Refused) throw new SeamRefusal(r.Text());
        if (frame[4] != StateFrame) throw new IOException("the session answered an ask for its source with a frame of kind " + frame[4]);
        long revision = r.Word();
        byte[] source = r.Bytes();
        r.Whole();
        return (revision, source);
    }

    /// <summary>
    /// Sends operations, encoded as one journal entry, as a write of the revision they were made
    /// against, and answers what the session did with them.
    /// </summary>
    public SeamAnswer Write(long revision, string writer, SeamWriter kind, byte[] entry)
    {
        var w = new Frame(Apply);
        w.Bytes(lease.Token);
        w.Word(revision);
        w.Bytes(Encoding.UTF8.GetBytes(writer));
        w.Word((long)kind);
        w.Bytes(entry);
        Send(w.Finish());
        while (true)
        {
            byte[] frame = Receive();
            var r = new Fields(frame);
            switch (frame[4])
            {
                case Applied: { long at = r.Word(); r.Whole(); return new SeamAnswer(SeamOutcome.Applied, at, 0, null); }
                case Held: { long proposal = r.Word(); string why = r.Text(); r.Whole(); return new SeamAnswer(SeamOutcome.Held, 0, proposal, why); }
                case Refused: { string why = r.Text(); r.Whole(); return new SeamAnswer(SeamOutcome.Refused, 0, 0, why); }
            }
            // A frame meant for everyone joined, such as the writer's arrival, is not this tool's answer.
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        tcp.Dispose();
    }

    // What the messages that arrived carry and no frame has taken yet.
    readonly MemoryStream arrived = new();

    void Send(byte[] frame)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        stream.Write(length);
        stream.Write(frame, 0, frame.Length);
    }

    byte[] Receive()
    {
        while (true)
        {
            byte[] have = arrived.GetBuffer();
            int held = (int)arrived.Length;
            if (held >= 4)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(have);
                if (n < 1 || n > FrameLimit) throw new IOException("the session sent a frame of " + n + " bytes, which no frame takes");
                if (held >= n + 4)
                {
                    byte[] frame = have.AsSpan(0, n + 4).ToArray();
                    byte[] rest = have.AsSpan(n + 4, held - n - 4).ToArray();
                    arrived.SetLength(0);
                    arrived.Write(rest);
                    return frame;
                }
            }
            arrived.Write(Message());
        }
    }

    // The next message the socket carries, without its length.
    byte[] Message()
    {
        var length = new byte[4];
        ReadExactly(length, 0, 4);
        int n = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (n < 0 || n > FrameLimit + 4) throw new IOException("the session sent a message of " + n + " bytes, which no frame takes");
        var message = new byte[n];
        ReadExactly(message, 0, n);
        return message;
    }

    void ReadExactly(byte[] into, int at, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(into, at, count);
            if (read == 0) throw new IOException("the session closed the connection before it answered");
            at += read;
            count -= read;
        }
    }

    // A frame being written: its length, filled in last, its kind, then its fields.
    sealed class Frame
    {
        readonly MemoryStream bytes = new();

        public Frame(byte kind)
        {
            bytes.Write(stackalloc byte[4]);
            bytes.WriteByte(kind);
        }

        public void Word(long value)
        {
            Span<byte> word = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(word, value);
            bytes.Write(word);
        }

        public void Bytes(byte[] value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            bytes.Write(length);
            bytes.Write(value);
        }

        public byte[] Finish()
        {
            byte[] frame = bytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 4);
            return frame;
        }
    }

    // A whole frame's fields, past its header.
    struct Fields
    {
        readonly byte[] frame;
        int at;

        public Fields(byte[] frame)
        {
            this.frame = frame;
            at = Header;
            if (frame.Length < Header) throw new IOException("the session sent a frame with no kind");
        }

        public long Word()
        {
            Need(8);
            long value = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(at, 8));
            at += 8;
            return value;
        }

        public byte[] Bytes()
        {
            Need(4);
            int n = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(at, 4));
            at += 4;
            if (n < 0) throw new IOException("the session sent a frame cut short");
            Need(n);
            byte[] value = frame.AsSpan(at, n).ToArray();
            at += n;
            return value;
        }

        public string Text() => Encoding.UTF8.GetString(Bytes());

        public void Whole()
        {
            if (at != frame.Length) throw new IOException("the session sent a frame longer than its fields");
        }

        void Need(int n)
        {
            if (n > frame.Length - at) throw new IOException("the session sent a frame cut short");
        }
    }
}
