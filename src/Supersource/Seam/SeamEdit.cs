using System;
using System.Collections.Generic;
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
            SeamEntry entry = Entry(in sent, in edited, out operations);
            if (operations == 0) return new SeamAnswer(SeamOutcome.Applied, Revision, 0, null);
            string agent = Environment.GetEnvironmentVariable(AgentVariable);
            var who = Acting.Value ?? (string.IsNullOrEmpty(agent) ? (writer, kind) : (agent, SeamWriter.Agent));
            return client.Write(Revision, who.Name, who.Kind, in entry);
        }
        finally
        {
            SourceFile.Close(ref sent);
        }
    }

    /// <summary>
    /// What a write carries: the operations that turn the source the session sent into the one the
    /// verb left, as a journal section's contents numbered by the session's kinds and relations. A
    /// kind or a relation they name that the session's source does not hold, and a relation such a
    /// kind's schema uses, take the next numbers after the session's own, in the order the
    /// operations first name them, and travel beside the journal.
    /// </summary>
    public static SeamEntry Entry(in SourceFile sent, in SourceFile edited, out int operations)
    {
        Operation[] ops = null;
        operations = StructuralDiff.Between(in sent.Src, in edited.Src, in edited.Table, ref ops);
        int[] kindSlot = Slots(edited.KindSlot, edited.Table.KindCount, out int kinds);
        int[] relationSlot = Slots(edited.RelationSlot, edited.Table.RelationCount, out int relations);
        var newKinds = new List<int>();
        var newRelations = new List<int>();
        for (int i = 0; i < operations; i++)
        {
            if (JournalCodec.NamesKind(ops[i].Kind) && kindSlot[ops[i].NodeKind.Value] < 0)
            {
                KindId kind = ops[i].NodeKind;
                kindSlot[kind.Value] = kinds + newKinds.Count;
                newKinds.Add(kind.Value);
                if (KindTable.HasSchema(in edited.Table, kind))
                    foreach (RelationId used in KindTable.UsedRelations(in edited.Table, kind))
                        Number(used.Value, relationSlot, relations, newRelations);
            }
            if (JournalCodec.NamesRelation(ops[i].Kind)) Number(ops[i].Relation.Value, relationSlot, relations, newRelations);
        }

        using var bytes = new MemoryStream();
        ByteWriter w = ByteWriter.Over(bytes);
        JournalCodec.Write(ref w, ops, operations, kindSlot, relationSlot);
        w.Flush();
        if (newKinds.Count == 0 && newRelations.Count == 0)
            return new SeamEntry(bytes.ToArray(), kinds, relations, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>());

        var kindNames = new QualifiedName[newKinds.Count];
        for (int i = 0; i < kindNames.Length; i++) kindNames[i] = KindTable.NameOf(in edited.Table, new KindId(newKinds[i]));
        var relationNames = new QualifiedName[newRelations.Count];
        for (int i = 0; i < relationNames.Length; i++) relationNames[i] = KindTable.RelationNameOf(in edited.Table, new RelationId(newRelations[i]));
        byte[] schemas = SchemaSection.Write(in edited.Table, newKinds.ToArray(), newRelations.ToArray(), relationSlot, kinds, relations);
        return new SeamEntry(bytes.ToArray(), kinds, relations, SourceWrite.WriteNames(kindNames), SourceWrite.WriteNames(relationNames), schemas);
    }

    // A file's slots by handle, as long as the table, with how many the file numbers.
    static int[] Slots(int[] read, int count, out int numbered)
    {
        var slots = new int[Math.Max(count, read?.Length ?? 0)];
        Array.Fill(slots, -1);
        numbered = 0;
        for (int i = 0; i < (read?.Length ?? 0); i++)
        {
            slots[i] = read[i];
            if (read[i] >= numbered) numbered = read[i] + 1;
        }
        return slots;
    }

    static void Number(int relation, int[] slots, int numbered, List<int> added)
    {
        if (slots[relation] >= 0) return;
        slots[relation] = numbered + added.Count;
        added.Add(relation);
    }

    /// <summary>
    /// How a session reads a write's journal: its own kinds and relations by the numbers its source
    /// gives them, then the write's extras, which this declares in the source's table with their
    /// schemas and numbers on in its slots. False, with nothing declared, when the extras start
    /// anywhere but at the source's own counts, since then the session's kinds changed after the
    /// tool read them and the tool reads the state again.
    /// </summary>
    public static bool Numbering(ref SourceFile file, in SeamEntry entry, out KindId[] kinds, out RelationId[] relations)
    {
        int[] kindSlot = Slots(file.KindSlot, file.Table.KindCount, out int kindCount);
        int[] relationSlot = Slots(file.RelationSlot, file.Table.RelationCount, out int relationCount);
        kinds = null;
        relations = null;
        if (entry.FirstKind != kindCount || entry.FirstRelation != relationCount) return false;

        QualifiedName[] kindNames = Names(entry.KindNames);
        QualifiedName[] relationNames = Names(entry.RelationNames);
        kinds = new KindId[kindCount + kindNames.Length];
        relations = new RelationId[relationCount + relationNames.Length];
        for (int i = 0; i < kindSlot.Length; i++) if (kindSlot[i] >= 0) kinds[kindSlot[i]] = new KindId(i);
        for (int i = 0; i < relationSlot.Length; i++) if (relationSlot[i] >= 0) relations[relationSlot[i]] = new RelationId(i);
        for (int i = 0; i < kindNames.Length; i++) kinds[kindCount + i] = KindTable.Intern(ref file.Table, kindNames[i]);
        for (int i = 0; i < relationNames.Length; i++) relations[relationCount + i] = KindTable.InternRelation(ref file.Table, relationNames[i]);
        if (entry.Schemas.Length > 0) SchemaSection.Read(ByteReader.Over(entry.Schemas), ref file.Table, kinds, relations);

        // The source numbers them from now on, so the next write's extras start after them.
        kindSlot = Slots(kindSlot, file.Table.KindCount, out _);
        relationSlot = Slots(relationSlot, file.Table.RelationCount, out _);
        for (int i = 0; i < kindNames.Length; i++) kindSlot[kinds[kindCount + i].Value] = kindCount + i;
        for (int i = 0; i < relationNames.Length; i++) relationSlot[relations[relationCount + i].Value] = relationCount + i;
        file.KindSlot = kindSlot;
        file.RelationSlot = relationSlot;
        return true;
    }

    static QualifiedName[] Names(byte[] section) =>
        section.Length == 0 ? Array.Empty<QualifiedName>() : SourceRead.ReadNames(ByteReader.Over(section), SourceWrite.FormatMajor);

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
        var before = Acting.Value;
        Acting.Value = (name, kind);
        return new Restore(before);
    }

    // It flows with the call into a thread the call starts, as the command line runs each verb on one.
    static readonly System.Threading.AsyncLocal<(string Name, SeamWriter Kind)?> Acting = new();

    /// <summary>
    /// The variable an MCP server sets, to its client's name, for a process it runs a tool call
    /// in, so a write that process sends goes under the agent's name as one sent in the server would.
    /// </summary>
    public const string AgentVariable = "SUPERSOURCE_AGENT";

    sealed class Restore : IDisposable
    {
        readonly (string, SeamWriter)? before;
        public Restore((string, SeamWriter)? before) => this.before = before;
        public void Dispose() => Acting.Value = before;
    }

    public void Dispose() => client.Dispose();
}
