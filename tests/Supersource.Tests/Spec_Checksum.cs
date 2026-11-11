using System;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Format.Tests;

public class Spec_Checksum : IDisposable
{
    static readonly SourceId Library = SourceId.Mint();
    readonly string dir;

    public Spec_Checksum()
    {
        dir = Path.Combine(Path.GetTempPath(), "supersource-digest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    SourceFile Built(string text)
    {
        SourceFile file = SourceFile.Create(16);
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(Library, "leaf"));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None,
            PayloadFormat.Text, default);

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, kind);
        SourceBuild.SetPayload(ref file.Src, root, Encoding.UTF8.GetBytes(text));

        for (int i = 0; i < 12; i++)
        {
            NodeId node = SourceBuild.AddChild(ref file.Src, root, kind);
            SourceBuild.SetPayload(ref file.Src, node, Encoding.UTF8.GetBytes(text + " " + i));
        }

        return file;
    }

    string Written(string name, string text)
    {
        SourceFile file = Built(text);
        string path = Path.Combine(dir, name);
        SourceWrite.ToFile(in file, path);
        return path;
    }

    [Fact]
    public void A_file_this_build_wrote_verifies()
    {
        string path = Written("intact.ss", "hello");

        Assert.True(SourceRead.Verify(path, out ContentHash recorded, out ContentHash actual));
        Assert.False(recorded.IsNone);
        Assert.Equal(recorded, actual);
    }

    [Fact]
    public void One_flipped_bit_is_caught()
    {
        string path = Written("rot.ss", "the quick brown fox");

        SectionSpan[] spans = null;
        using (FileStream stream = File.OpenRead(path))
        {
            SourceRead.Sections(stream, ref spans);
        }

        // Inside the node section, so the graph still parses and only the content moved.
        long at = -1;
        for (int i = 0; i < spans.Length; i++)
        {
            if (spans[i].Id == (uint)SectionId.Nodes)
            {
                at = spans[i].At + spans[i].Length / 2;
            }
        }
        Assert.True(at > 0);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[at] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        Assert.False(SourceRead.Verify(path, out ContentHash recorded, out ContentHash actual));
        Assert.NotEqual(recorded, actual);

        // And the file still opens: a digest reports, it does not refuse.
        SourceFile still = SourceRead.FromFile(path);
        SourceFile.Close(ref still);
    }

    /// <summary>A version 2 file, written before files carried a digest.</summary>
    static byte[] Undigested()
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "legacy", "ordinary-v2.ss"));
    }

    [Fact]
    public void A_file_with_no_digest_is_not_a_failure()
    {
        string path = Path.Combine(dir, "nodigest.ss");
        File.WriteAllBytes(path, Undigested());

        Assert.False(SourceRead.Verify(path, out ContentHash recorded, out _));
        Assert.True(recorded.IsNone);
        Assert.True(SourceRead.DigestOf(path).IsNone);
    }

    [Fact]
    public void A_save_brings_a_file_without_a_digest_to_the_current_format_with_one()
    {
        SourceFile old = SourceRead.FromBytes(Undigested());
        Assert.Equal(DigestKind.None, old.DigestKind);

        SourceFile back = SourceRead.FromBytes(SourceWrite.ToBytes(in old));
        Assert.Equal(DigestKind.Sha256, back.DigestKind);
        Assert.Equal(SourceWrite.FormatMajor, back.Major);
    }

    [Fact]
    public void A_file_with_a_digest_round_trips_byte_for_byte()
    {
        SourceFile file = Built("stable");
        byte[] first = SourceWrite.ToBytes(in file);

        for (int i = 0; i < 3; i++)
        {
            SourceFile back = SourceRead.FromBytes(first);
            Assert.Equal(DigestKind.Sha256, back.DigestKind);

            byte[] next = SourceWrite.ToBytes(in back);
            Assert.Equal(first, next);
            first = next;
        }
    }

    [Fact]
    public void Changing_the_content_changes_the_digest()
    {
        string one = Written("one.ss", "before");
        string two = Written("two.ss", "after");

        Assert.NotEqual(SourceRead.DigestOf(one), SourceRead.DigestOf(two));
    }

    [Fact]
    public void Saving_over_a_file_somebody_else_wrote_is_refused()
    {
        string path = Written("shared.ss", "original");

        SourceFile mine = SourceRead.FromFile(path);
        try
        {
            // Somebody else saves while we hold it.
            SourceFile theirs = Built("theirs");
            SourceWrite.ToFile(in theirs, path);

            Assert.False(SourceWrite.Replace(ref mine, path, out string refusal));
            Assert.Contains("written since", refusal);
        }
        finally
        {
            SourceFile.Close(ref mine);
        }

        // Their work is still there.
        SourceFile after = SourceRead.FromFile(path);
        Assert.Equal("theirs", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, after.Src.Root)));
        SourceFile.Close(ref after);
    }

    [Fact]
    public void Saving_over_a_file_nobody_touched_goes_through()
    {
        string path = Written("quiet.ss", "mine");

        SourceFile mine = SourceRead.FromFile(path);
        Source edited = SourceCompact.Compact(in mine.Src);
        SourceFile.Close(ref mine);

        SourceFile save = SourceRead.FromFile(path);
        try
        {
            SourceBuild.SetPayload(ref save.Src, save.Src.Root, Encoding.UTF8.GetBytes("edited"));
            Assert.True(SourceWrite.Replace(ref save, path, out string refusal));
            Assert.Null(refusal);
        }
        finally
        {
            SourceFile.Close(ref save);
        }

        SourceFile after = SourceRead.FromFile(path);
        Assert.Equal("edited", Encoding.UTF8.GetString(SourceWalk.Payload(in after.Src, after.Src.Root)));
        SourceFile.Close(ref after);
    }
}
