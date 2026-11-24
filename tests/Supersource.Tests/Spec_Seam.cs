using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Supersource.Compare;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Seam;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

/// <summary>
/// The seam's tool side (Quire docs/sessions.md, "Outside tools write through the session"): a
/// lease beside a file, the frames a tool sends and reads, and a write sent as one journal entry
/// that the session replays onto the source it sent.
/// </summary>
public class Spec_Seam : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    readonly string dir = Path.Combine(Path.GetTempPath(), "ss-seam-" + Guid.NewGuid().ToString("n"));

    public Spec_Seam() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    static SourceFile Chapter(out KindId paragraph)
    {
        var file = SourceFile.Create(64);
        KindId chapter = KindTable.Intern(ref file.Table, new QualifiedName(Library, "chapter"));
        paragraph = KindTable.Intern(ref file.Table, new QualifiedName(Library, "paragraph"));
        KindTable.DefineSchema(ref file.Table, chapter, StructuralFact.ChildOrderSignificant, PayloadFormat.Text, default);
        KindTable.DefineSchema(ref file.Table, paragraph, StructuralFact.None, PayloadFormat.Text, default);
        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, chapter);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes("a chapter"));
        NodeId first = SourceBuild.AddNode(ref file.Src, paragraph);
        SourceBuild.SetPayload(ref file.Src, first, Encoding.UTF8.GetBytes("the first paragraph"));
        SourceBuild.AppendChild(ref file.Src, root, first);
        return file;
    }

    [Fact]
    public void A_lease_reads_back_as_written_and_one_expired_or_misspelt_is_none()
    {
        var lease = new Lease("Ada's Super Studio", 51234, new byte[] { 0x0f, 0xa0, 0x33 }, 1_900_000_000_000);
        Assert.True(Lease.TryRead(lease.Text(), out Lease read));
        Assert.Equal(lease.Holder, read.Holder);
        Assert.Equal(lease.Port, read.Port);
        Assert.Equal(lease.Token, read.Token);
        Assert.Equal(lease.Expires, read.Expires);
        Assert.Contains("token 0fa033\n", lease.Text());

        string source = Path.Combine(dir, "held.ss");
        File.WriteAllText(Lease.PathOf(source), lease.Text());
        Assert.True(Lease.Find(source, out _));
        File.WriteAllText(Lease.PathOf(source), (lease with { Expires = 1 }).Text());
        Assert.False(Lease.Find(source, out _));
        Assert.False(Lease.TryRead(lease.Text().Replace("superscript-lease 1", "superscript-lease 2"), out _));
        Assert.False(Lease.TryRead(lease.Text().Replace("token 0fa033", "token 0FA033"), out _));
        Assert.False(Lease.TryRead(lease.Text().Replace("holder ", "owner "), out _));
    }

    [Fact]
    public void A_write_is_the_journal_entry_that_turns_the_source_sent_into_the_one_edited()
    {
        var made = Chapter(out KindId paragraph);
        byte[] sent = SourceWrite.ToBytes(in made);
        SourceFile.Close(ref made);

        SourceFile before = SourceRead.FromBytes(sent), edited = SourceRead.FromBytes(sent);
        NodeId second = SourceBuild.AddNode(ref edited.Src, paragraph);
        SourceBuild.SetPayload(ref edited.Src, second, Encoding.UTF8.GetBytes("a second paragraph"));
        SourceBuild.AppendChild(ref edited.Src, edited.Src.Root, second);
        SourceBuild.SetPayload(ref edited.Src, new NodeId(1), Encoding.UTF8.GetBytes("the first paragraph, edited"));

        Assert.True(SeamEdit.Entry(in before, in edited, out byte[] entry, out int operations, out string refusal), refusal);
        Assert.True(operations > 0);
        Source replayed = Replayed(sent, entry);
        Operation[] left = null;
        Assert.Equal(0, StructuralDiff.Between(in replayed, in edited.Src, in edited.Table, ref left));
        Assert.Equal(second, SourceWalk.Children(in replayed, replayed.Root)[1] is long id ? new NodeId(id) : default);
        SourceFile.Close(ref before);
        SourceFile.Close(ref edited);
    }

    [Fact]
    public void A_tool_reads_the_source_through_the_session_and_its_write_arrives_whole_under_its_name()
    {
        var made = Chapter(out KindId paragraph);
        byte[] sent = SourceWrite.ToBytes(in made);
        SourceFile.Close(ref made);
        byte[] token = { 1, 2, 3, 4 };
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] apply = null;
        Task session = Task.Run(() =>
        {
            using TcpClient tool = listener.AcceptTcpClient();
            NetworkStream s = tool.GetStream();
            byte[] ask = Frame(s);
            Assert.Equal(1, ask[4]);
            Send(s, 2, w => { Word(w, 41); Bytes(w, sent); });
            apply = Frame(s);
            Send(s, 5, w => { Word(w, 7); Bytes(w, Encoding.UTF8.GetBytes("Ada's setting holds an agent's writes for her")); });
        });

        string path = Path.Combine(dir, "held.ss");
        File.WriteAllText(Lease.PathOf(path), new Lease("Ada", port, token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000).Text());
        Assert.True(SeamEdit.Open(path, out SeamEdit edit));
        SeamAnswer answer;
        using (edit)
        {
            Assert.Equal(41, edit.Revision);
            SourceFile copy = edit.Read();
            NodeId added = SourceBuild.AddNode(ref copy.Src, paragraph);
            SourceBuild.AppendChild(ref copy.Src, copy.Src.Root, added);
            using (SeamEdit.As("Claude Code", SeamWriter.Agent))
                answer = edit.Send(in copy, "supersource put", SeamWriter.Tool, out int operations);
            SourceFile.Close(ref copy);
        }
        Assert.True(session.Wait(10000));
        listener.Stop();

        Assert.Equal(SeamOutcome.Held, answer.Outcome);
        Assert.Equal(7, answer.Proposal);
        Assert.Equal("held by the open session as proposal 7: Ada's setting holds an agent's writes for her", SeamEdit.Said(answer));
        // The apply: the token, the revision read, the writer's name and kind, and the entry.
        int at = 5;
        Assert.Equal(token, Field(apply, ref at));
        Assert.Equal(41, Long(apply, ref at));
        Assert.Equal("Claude Code", Encoding.UTF8.GetString(Field(apply, ref at)));
        Assert.Equal((long)SeamWriter.Agent, Long(apply, ref at));
        byte[] entry = Field(apply, ref at);
        Assert.Equal(apply.Length, at);
        Assert.Equal(3, SourceWalk.ChildCount(Replayed(sent, entry), new NodeId(0)) + 1);
    }

    [Fact]
    public void A_session_that_refuses_the_token_says_so_and_a_lease_on_a_closed_port_cannot_be_reached()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task session = Task.Run(() =>
        {
            using TcpClient tool = listener.AcceptTcpClient();
            Frame(tool.GetStream());
            Send(tool.GetStream(), 6, w => Bytes(w, Encoding.UTF8.GetBytes("The lease's token does not match this session's.")));
        });
        string path = Path.Combine(dir, "held.ss");
        long later = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
        File.WriteAllText(Lease.PathOf(path), new Lease("Ada", port, new byte[] { 9 }, later).Text());
        var refused = Assert.Throws<SeamRefusal>(() => SeamEdit.Open(path, out _));
        Assert.Equal("The lease's token does not match this session's.", refused.Message);
        Assert.True(session.Wait(10000));
        listener.Stop();

        File.WriteAllText(Lease.PathOf(path), new Lease("Ada", port, new byte[] { 9 }, later).Text());
        Assert.Throws<IOException>(() => SeamEdit.Open(path, out _));
    }

    // The source the session sent, with an apply's entry replayed onto it as the session would.
    static Source Replayed(byte[] sent, byte[] entry)
    {
        SourceFile file = SourceRead.FromBytes(sent);
        var kinds = new KindId[file.Table.KindCount];
        var relations = new RelationId[file.Table.RelationCount];
        for (int i = 0; i < file.KindSlot.Length; i++) if (file.KindSlot[i] >= 0 && file.KindSlot[i] < kinds.Length) kinds[file.KindSlot[i]] = new KindId(i);
        for (int i = 0; i < file.RelationSlot.Length; i++) if (file.RelationSlot[i] >= 0 && file.RelationSlot[i] < relations.Length) relations[file.RelationSlot[i]] = new RelationId(i);
        Assert.True(JournalCodec.Replay(ByteReader.Over(entry, 0, entry.Length - JournalCodec.DigestBytes), ref file.Src, kinds, relations), "the entry of " + entry.Length + " bytes, version " + entry[0]);
        return file.Src;
    }

    // A frame as the engine's sockets carry it: one message, with a length of its own before the frame.
    static byte[] Frame(NetworkStream s)
    {
        var length = new byte[4];
        Exactly(s, length, 0, 4);
        int n = BinaryPrimitives.ReadInt32LittleEndian(length);
        var frame = new byte[n];
        Exactly(s, frame, 0, n);
        Assert.Equal(n - 4, BinaryPrimitives.ReadInt32LittleEndian(frame));
        return frame;
    }

    static void Message(NetworkStream s, ReadOnlySpan<byte> bytes)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        s.Write(length);
        s.Write(bytes);
    }

    static void Exactly(NetworkStream s, byte[] into, int at, int count)
    {
        while (count > 0)
        {
            int read = s.Read(into, at, count);
            Assert.True(read > 0);
            at += read;
            count -= read;
        }
    }

    static void Send(NetworkStream s, byte kind, Action<MemoryStream> fields)
    {
        var w = new MemoryStream();
        w.Write(new byte[4]);
        w.WriteByte(kind);
        fields(w);
        byte[] frame = w.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 4);
        // In two messages, as a frame longer than a receive's buffer arrives, which the tool reads whole.
        int half = frame.Length / 2;
        Message(s, frame.AsSpan(0, half));
        Message(s, frame.AsSpan(half));
    }

    static void Word(MemoryStream w, long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        w.Write(b);
    }

    static void Bytes(MemoryStream w, byte[] value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value.Length);
        w.Write(b);
        w.Write(value);
    }

    static long Long(byte[] frame, ref int at)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(at, 8));
        at += 8;
        return value;
    }

    static byte[] Field(byte[] frame, ref int at)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(at, 4));
        at += 4;
        byte[] value = frame.AsSpan(at, n).ToArray();
        at += n;
        return value;
    }
}
