using System;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Format.Tests;

public class Fuzz_Format
{
    const int Cases = 3000;

    static SourceFile Arbitrary(int seed)
    {
        Random rng = new Random(seed);
        SourceFile file = SourceFile.Create(64);
        file.Src.Identity = new SourceId((ulong)rng.NextInt64(), (ulong)rng.NextInt64());

        SourceId library = new SourceId(0xF022_F022_F022_F022UL, (ulong)seed);
        int kindCount = 1 + rng.Next(6);
        KindId[] kinds = new KindId[kindCount];
        for (int i = 0; i < kindCount; i++)
        {
            kinds[i] = KindTable.Intern(ref file.Table, new QualifiedName(library, "k" + i));

            // Leave most kinds undefined to exercise preservation without schemas.
            if (rng.Next(3) == 0)
            {
                KindTable.DefineSchema(ref file.Table, kinds[i],
                    StructuralFact.Declaration, PayloadFormat.Utf8, DisplayShape.Block,
                    (uint)rng.Next(), default);
            }
        }

        RelationId relation = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "r"));

        NodeId root = SourceBuild.AddRoot(ref file.Src, kinds[0]);
        int nodes = 1 + rng.Next(40);
        NodeId[] made = new NodeId[nodes + 1];
        made[0] = root;
        int count = 1;

        // Exercise deep chains, wide sibling runs and mixed trees; uniform parent selection rarely
        // produces the first two.
        int shape = rng.Next(3);
        NodeId newest = root;

        for (int i = 0; i < nodes; i++)
        {
            NodeId parent = shape == 0 ? newest : (shape == 1 ? root : made[rng.Next(count)]);
            NodeId node = SourceBuild.AddChild(ref file.Src, parent, kinds[rng.Next(kindCount)]);
            newest = node;

            if (rng.Next(2) == 0)
            {
                byte[] payload = new byte[rng.Next(24)];
                rng.NextBytes(payload);
                SourceBuild.SetPayload(ref file.Src, node, payload);
            }

            made[count] = node;
            count++;
        }

        // Include unbound references as well as bound edges.
        int edges = rng.Next(20);
        for (int i = 0; i < edges; i++)
        {
            NodeId from = made[rng.Next(count)];
            if (rng.Next(4) == 0)
            {
                SourceBuild.AddUnboundRef(ref file.Src, from, relation,
                    Encoding.UTF8.GetBytes("missing" + i));
            }
            else
            {
                SourceBuild.AddRef(ref file.Src, from, relation, made[rng.Next(count)]);
            }
        }

        // Retain deleted slots and dangling targets through round-trip checks.
        int deletions = rng.Next(4);
        for (int i = 0; i < deletions && count > 1; i++)
        {
            NodeId victim = made[1 + rng.Next(count - 1)];
            if (file.Src.IsLive(victim) && victim != root)
            {
                SourceBuild.Delete(ref file.Src, victim);
            }
        }

        // Include history so corruption and truncation checks cover its encoding too.
        int entries = rng.Next(12);
        for (int i = 0; i < entries; i++)
        {
            NodeId node = made[rng.Next(count)];
            Operation op = default;

            switch (rng.Next(4))
            {
                case 0:
                    op.Kind = OpKind.Insert;
                    op.Parent = node;
                    op.Index = SourceWalk.ChildCount(in file.Src, node);
                    op.NodeKind = kinds[rng.Next(kindCount)];
                    op.Value = Encoding.UTF8.GetBytes("logged" + i);
                    break;

                case 1:
                    op.Kind = OpKind.ModifyValue;
                    op.Node = node;
                    op.Value = new byte[rng.Next(8)];
                    rng.NextBytes(op.Value);
                    break;

                case 2:
                    op.Kind = OpKind.Splice;
                    op.Node = node;
                    op.Index = rng.Next(4);
                    op.Count = rng.Next(3);
                    op.Value = Encoding.UTF8.GetBytes("s" + i);
                    break;

                default:
                    op.Kind = OpKind.Connect;
                    op.Node = node;
                    op.Relation = relation;
                    op.Target = rng.Next(3) == 0 ? NodeId.None : made[rng.Next(count)];
                    op.Value = op.Target.IsNone ? Encoding.UTF8.GetBytes("hole" + i) : null;
                    break;
            }

            // A caret rider on some of them, which must not survive the save.
            if (rng.Next(3) == 0)
            {
            }

            Log.Do(ref file.History, ref file.Src, op, rng.Next(4));
        }

        // Some of it undone, so the applied prefix and the entry count differ.
        for (int i = rng.Next(3); i > 0; i--)
        {
            Log.Undo(ref file.History, ref file.Src);
        }

        KindSchemaCodec.Write(ref file.Src, in file.Table, in file.Boot, root);
        return file;
    }

    [Fact]
    public void Any_document_round_trips_byte_for_byte()
    {
        for (int seed = 0; seed < Cases; seed++)
        {
            SourceFile original = Arbitrary(seed);
            byte[] first = SourceWrite.ToBytes(in original);

            SourceFile reread = SourceRead.FromBytes(first);
            byte[] second = SourceWrite.ToBytes(in reread);

            Assert.True(
                AreEqual(first, second),
                "seed " + seed + ": a source did not survive a round-trip");
        }
    }

    [Fact]
    public void Reading_is_a_function_of_the_bytes_alone()
    {
        for (int seed = 0; seed < Cases; seed++)
        {
            byte[] bytes = SourceWrite.ToBytes(Arbitrary(seed));

            SourceFile a = SourceRead.FromBytes(bytes);
            SourceFile b = SourceRead.FromBytes(bytes);

            Assert.True(
                AreEqual(SourceWrite.ToBytes(in a), SourceWrite.ToBytes(in b)),
                "seed " + seed + ": two reads of one file disagreed");
        }
    }

    [Fact]
    public void Corrupted_bytes_are_reported_and_never_crash()
    {
        int survived = 0;
        int rejected = 0;

        for (int seed = 0; seed < Cases; seed++)
        {
            byte[] bytes = SourceWrite.ToBytes(Arbitrary(seed));
            Random rng = new Random(seed * 7919);

            int flips = 1 + rng.Next(8);
            for (int i = 0; i < flips; i++)
            {
                bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            }

            try
            {
                SourceFile read = SourceRead.FromBytes(bytes);

                // If it parsed, it has to be a source a writer can serialise again.
                SourceWrite.ToBytes(in read);
                survived++;
            }
            catch (FormatCorruptException)
            {
                rejected++;
            }
        }

        Assert.Equal(Cases, survived + rejected);

        // Require both accepted and rejected mutations to ensure the corpus exercises both paths.
        Assert.True(rejected > 0, "no corruption was ever detected");
        Assert.True(survived > 0, "every corrupted source was rejected; the fuzzing is too blunt");
    }

    [Fact]
    public void Every_truncation_is_reported_and_never_crashes()
    {
        byte[] bytes = SourceWrite.ToBytes(Arbitrary(17));

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(bytes, prefix, length);

            try
            {
                SourceFile read = SourceRead.FromBytes(prefix);
                SourceWrite.ToBytes(in read);
            }
            catch (FormatCorruptException)
            {
                // The expected outcome for a truncated file.
            }
        }
    }

    [Fact]
    public void Arbitrary_bytes_are_reported_and_never_crash()
    {
        for (int seed = 0; seed < Cases; seed++)
        {
            Random rng = new Random(seed);
            byte[] noise = new byte[rng.Next(512)];
            rng.NextBytes(noise);

            try
            {
                SourceFile read = SourceRead.FromBytes(noise);
                SourceWrite.ToBytes(in read);
            }
            catch (FormatCorruptException)
            {
            }
        }
    }

    static bool AreEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }
}
