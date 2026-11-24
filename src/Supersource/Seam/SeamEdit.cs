using System;
using System.IO;
using Supersource.Compare;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;

namespace Supersource.Seam;

/// <summary>
/// One verb's visit to the session that holds a source: the source as it stands, which the verb
/// reads, or edits as it would edit the file, and the edit sent back as one journal entry (Quire
/// docs/sessions.md, "Outside tools write through the session"). A verb opens one when it finds a
/// live lease beside the file, and works on the file itself when it finds none.
/// </summary>
public sealed class SeamEdit : IDisposable
{
    /// <summary>The lease the verb found.</summary>
    public readonly Lease Lease;

    /// <summary>The session's revision the source was read at, which a write is made against.</summary>
    public readonly long Revision;

    /// <summary>The source as the session sent it, as v5 bytes.</summary>
    public readonly byte[] Bytes;

    readonly SeamClient client;

    SeamEdit(Lease lease, SeamClient client, long revision, byte[] bytes)
    {
        Lease = lease;
        this.client = client;
        Revision = revision;
        Bytes = bytes;
    }

    /// <summary>
    /// The session holding the file at a path, with the source as it stands there, or false when
    /// no live lease is beside the file. A lease whose session cannot be reached, or that refuses
    /// the ask, throws, since writing the file under a live lease would race the session.
    /// </summary>
    public static bool Open(string path, out SeamEdit edit)
    {
        edit = null;
        if (!Lease.Find(path, out Lease lease)) return false;
        SeamClient client = SeamClient.Connect(lease);
        try
        {
            var (revision, bytes) = client.State();
            edit = new SeamEdit(lease, client, revision, bytes);
            return true;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>A copy of the source as the session sent it, for the verb to read or edit.</summary>
    public SourceFile Read() => SourceRead.FromBytes(Bytes);

    /// <summary>
    /// A source's bytes as a verb reads them: the open session's while a live lease is beside the
    /// file, else the file's. A live lease whose session cannot be reached, or refuses the ask,
    /// throws an IOException, since the file may be behind what the session shows.
    /// </summary>
    public static byte[] BytesOf(string path)
    {
        SeamEdit seam;
        try
        {
            if (!Open(path, out seam)) return File.ReadAllBytes(path);
        }
        catch (SeamRefusal refusal)
        {
            throw new IOException("the session holding " + path + " refused to read it: " + refusal.Message);
        }
        using (seam) return seam.Bytes;
    }

    /// <summary>A source as a verb reads it, as <see cref="BytesOf"/> says.</summary>
    public static SourceFile ReadOf(string path)
    {
        if (!Lease.Find(path, out _)) return SourceRead.FromFile(path);
        return SourceRead.FromBytes(BytesOf(path));
    }

    /// <summary>
    /// Sends what the verb made of the source as one write, under the writer's name, and answers
    /// what the session did with it. An edit that changes nothing sends nothing and answers that it
    /// applied at the revision it was read at.
    /// </summary>
    public SeamAnswer Send(in SourceFile edited, string writer, SeamWriter kind, out int operations)
    {
        SourceFile sent = Read();
        try
        {
            if (!Entry(in sent, in edited, out byte[] entry, out operations, out string refusal))
                return new SeamAnswer(SeamOutcome.Refused, 0, 0, refusal);
            if (operations == 0) return new SeamAnswer(SeamOutcome.Applied, Revision, 0, null);
            string agent = Environment.GetEnvironmentVariable(AgentVariable);
            var who = Acting ?? (string.IsNullOrEmpty(agent) ? (writer, kind) : (agent, SeamWriter.Agent));
            return client.Write(Revision, who.Name, who.Kind, entry);
        }
        finally
        {
            SourceFile.Close(ref sent);
        }
    }

    /// <summary>
    /// The journal entry a write carries: the operations that turn the source the session sent
    /// into the one the verb left, as a journal section's contents with the kind and relation
    /// numbers of the session's source. False, with the sentence, when an operation names a kind or
    /// a relation that source does not hold, since the session could not read it.
    /// </summary>
    public static bool Entry(in SourceFile sent, in SourceFile edited, out byte[] entry, out int operations, out string refusal)
    {
        entry = null;
        refusal = null;
        Operation[] ops = null;
        operations = StructuralDiff.Between(in sent.Src, in edited.Src, in edited.Table, ref ops);
        for (int i = 0; i < operations; i++)
        {
            int kind = JournalCodec.NamesKind(ops[i].Kind) ? ops[i].NodeKind.Value : -1;
            if (kind >= 0 && (kind >= edited.KindSlot.Length || edited.KindSlot[kind] < 0))
            {
                refusal = "the edit makes a node of kind " + KindTable.NameOf(in edited.Table, ops[i].NodeKind)
                    + ", which the open session's source does not hold yet, so write it when no session holds the file";
                return false;
            }
            int relation = JournalCodec.NamesRelation(ops[i].Kind) ? ops[i].Relation.Value : -1;
            if (relation >= 0 && (relation >= edited.RelationSlot.Length || edited.RelationSlot[relation] < 0))
            {
                refusal = "the edit makes a reference of a relation the open session's source does not hold yet, "
                    + "so write it when no session holds the file";
                return false;
            }
        }
        using var bytes = new MemoryStream();
        ByteWriter w = ByteWriter.Over(bytes);
        JournalCodec.Write(ref w, ops, operations, edited.KindSlot, edited.RelationSlot);
        w.Flush();
        entry = bytes.ToArray();
        return true;
    }

    /// <summary>What a verb says of an answer, in one line.</summary>
    public static string Said(SeamAnswer answer) => answer.Outcome switch
    {
        SeamOutcome.Applied => "applied to the open session at revision " + answer.Revision + ", which writes the file when it saves",
        SeamOutcome.Held => "held by the open session as proposal " + answer.Proposal + ": " + answer.Sentence,
        _ => "refused by the open session: " + answer.Sentence,
    };

    /// <summary>
    /// Who writes in place of the verb's own name while a scope is open: the MCP server opens one
    /// with its client's name for each tool call, so an agent's write arrives as the agent's.
    /// </summary>
    public static IDisposable As(string name, SeamWriter kind)
    {
        var before = Acting;
        Acting = (name, kind);
        return new Restore(before);
    }

    [ThreadStatic] static (string Name, SeamWriter Kind)? Acting;

    /// <summary>
    /// The variable an MCP server sets, to its client's name, for a process it runs a tool call
    /// in, so a write that process sends goes under the agent's name as one sent in the server would.
    /// </summary>
    public const string AgentVariable = "SUPERSOURCE_AGENT";

    sealed class Restore : IDisposable
    {
        readonly (string, SeamWriter)? before;
        public Restore((string, SeamWriter)? before) => this.before = before;
        public void Dispose() => Acting = before;
    }

    public void Dispose() => client.Dispose();
}
