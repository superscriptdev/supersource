using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Supersource;
using Supersource.Cli;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.Cli.Tests;

/// <summary>
/// `cat` prints a source as text and `put` reads that text back as the operations it means. The
/// source here is built by hand, so what the tests assert about bytes is about the bytes.
/// </summary>
public class Spec_Put : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "supersource-put-" + Guid.NewGuid());
    readonly string path;
    readonly string identity;

    // n0 doc; n1 a table of two rows; n2 a note about row 1 of it; n3 200 undescribed bytes.
    public Spec_Put()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "tables.ss");

        var file = SourceFile.Create();
        var library = new SourceId(41, 43);
        RelationId rowOf = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "row-of"));
        KindTable.DefineRelation(ref file.Table, rowOf, PayloadFormat.One(PayloadScalar.U32));
        RelationId about = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "about"));
        KindTable.DefineRelation(ref file.Table, about, PayloadFormat.None);

        KindId doc = Kind(ref file, library, "doc", PayloadFormat.Text);
        KindId table = Kind(ref file, library, "table", PayloadFormat.Many(
            PayloadElement.Of(13,
                PayloadField.Of("id", PayloadScalar.U32, 0),
                PayloadField.Of("x", PayloadScalar.F64, 4),
                PayloadField.Of("flag", PayloadScalar.Bool, 12)),
            PayloadElement.Of(4, PayloadField.Of("rows", PayloadScalar.U32, 0))));
        KindId note = Kind(ref file, library, "note", PayloadFormat.Text, rowOf, about);
        KindId blob = Kind(ref file, library, "blob", PayloadFormat.Opaque);

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, doc);
        NodeId rows = SourceBuild.AddChild(ref file.Src, root, table);
        SourceBuild.SetPayload(ref file.Src, rows, Table((1, 1.5, true), (2, -2.25, false)));
        NodeId said = SourceBuild.AddChild(ref file.Src, root, note);
        SourceBuild.SetPayload(ref file.Src, said, Encoding.UTF8.GetBytes("first"));
        SourceBuild.AddRef(ref file.Src, said, rowOf, rows, U32(1));
        NodeId bytes = SourceBuild.AddChild(ref file.Src, root, blob);
        var filler = new byte[200];
        for (int i = 0; i < filler.Length; i++) filler[i] = (byte)i;
        SourceBuild.SetPayload(ref file.Src, bytes, filler);

        identity = file.Src.Identity.ToString();
        File.WriteAllBytes(path, SourceWrite.ToBytes(in file));
    }

    static KindId Kind(ref SourceFile file, SourceId library, string name, PayloadFormat payload,
        params RelationId[] uses)
    {
        KindId kind = KindTable.Intern(ref file.Table, new QualifiedName(library, name));
        KindTable.DefineSchema(ref file.Table, kind, StructuralFact.None, payload, uses);
        return kind;
    }

    /// <summary>A table's bytes, laid out by hand: a u32 count, then 13-byte rows.</summary>
    static byte[] Table(params (uint Id, double X, bool Flag)[] rows)
    {
        byte[] bytes = new byte[4 + 13 * rows.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows.Length);
        for (int r = 0; r < rows.Length; r++)
        {
            Span<byte> row = bytes.AsSpan(4 + 13 * r, 13);
            BinaryPrimitives.WriteUInt32LittleEndian(row, rows[r].Id);
            BinaryPrimitives.WriteDoubleLittleEndian(row.Slice(4), rows[r].X);
            row[12] = rows[r].Flag ? (byte)1 : (byte)0;
        }
        return bytes;
    }

    static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    [Fact]
    public void A_table_prints_as_its_header_its_columns_and_a_row_per_line()
    {
        string text = Cat();
        Assert.Contains(
            "  table  {rows 2} [id, x, flag]   n1\n" +
            "    | 1, 1.5, true\n" +
            "    | 2, -2.25, false\n", text);
        Assert.Contains("    --row-of 1--> n1\n", text);
        Assert.Contains("  blob  <200 bytes>   n3\n", text);
    }

    [Fact]
    public void What_cat_prints_reads_back_as_no_change()
    {
        byte[] before = File.ReadAllBytes(path);
        var put = Put(Cat());
        Assert.Equal(0, put.Code);
        Assert.Contains("no change", put.Output);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("hostile.ss")]
    [InlineData("ordinary.ss")]
    [InlineData("program.ss")]
    [InlineData("sprite.ss")]
    [InlineData("vec.ss")]
    public void Every_fixture_reads_back_as_no_change(string name)
    {
        string copy = Path.Combine(directory, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", name), copy);

        var cat = Run("cat", copy);
        Assert.Equal(0, cat.Code);
        var put = Run("put", copy, "--text", cat.Output, "--dry-run");
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("no change", put.Output);
    }

    [Fact]
    public void A_changed_row_is_a_splice_of_that_row()
    {
        var put = Put(Cat().Replace("| 2, -2.25, false", "| 2, 99, true"));
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("1  Splice  n1  at 17 -13 +13 bytes", put.Output);
        Assert.Contains("saved 1 edit(s)", put.Output);
        Assert.Equal(Table((1, 1.5, true), (2, 99, true)), PayloadOf(1));
    }

    [Fact]
    public void A_row_written_as_text_lands_as_the_bytes_the_schema_describes()
    {
        string text = Cat()
            .Replace("{rows 2}", "{rows 3}")
            .Replace("    | 2, -2.25, false\n", "    | 2, -2.25, false\n    | 3, 1e-3, true\n");
        var put = Put(text);
        Assert.True(put.Code == 0, put.Error);
        Assert.Equal(Table((1, 1.5, true), (2, -2.25, false), (3, 0.001, true)), PayloadOf(1));
        Assert.Contains("    | 3, 0.001, true\n", Cat());
    }

    [Fact]
    public void A_new_node_is_named_by_a_handle_and_a_reference_can_point_at_it()
    {
        string text = Cat()
            .Replace("    --row-of 1--> n1\n", "    --row-of 1--> n1\n    --about--> @second\n")
            + "  note  \"second\"  @second\n";
        var put = Put(text);
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("Insert  n4  in n0 at 3  note  @second", put.Output);

        string now = Cat();
        Assert.Contains("    --about--> n4\n", now);
        Assert.Contains("  note  \"second\"   n4\n", now);
    }

    [Fact]
    public void A_node_the_text_leaves_out_is_deleted_and_a_moved_one_keeps_its_id()
    {
        string text =
            "doc   n0\n" +
            "  note  \"first\"   n2\n" +
            "    --row-of 1--> n1\n" +
            "  table  {rows 2} [id, x, flag]   n1\n" +
            "    | 1, 1.5, true\n" +
            "    | 2, -2.25, false\n";
        var put = Put(text);
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("Move  n2", put.Output);
        Assert.Contains("Delete  n3", put.Output);

        string now = Cat();
        Assert.True(now.IndexOf("   n2\n", StringComparison.Ordinal) < now.IndexOf("   n1\n", StringComparison.Ordinal));
        Assert.DoesNotContain("blob", now);
        Assert.Contains("--row-of 1--> n1\n", now);
    }

    [Fact]
    public void A_subtree_is_restated_on_its_own()
    {
        var cat = Run("cat", path, "n1");
        Assert.Equal(0, cat.Code);
        Assert.DoesNotContain("note", cat.Output);

        var put = Put(cat.Output.Replace("| 1, 1.5, true", "| 1, 2.5, true"));
        Assert.True(put.Code == 0, put.Error);
        Assert.Equal(Table((1, 2.5, true), (2, -2.25, false)), PayloadOf(1));
        Assert.Contains("note  \"first\"   n2", Cat());
        Assert.Contains("blob  <200 bytes>   n3", Cat());
    }

    [Fact]
    public void A_string_keeps_its_quotes_backslashes_and_line_breaks()
    {
        var put = Put(Cat().Replace("\"first\"", "\"say \\\"hi\\\"\\nC:\\\\dir\""));
        Assert.True(put.Code == 0, put.Error);
        Assert.Equal("say \"hi\"\nC:\\dir", Encoding.UTF8.GetString(PayloadOf(2)));
        Assert.Contains("note  \"say \\\"hi\\\"\\nC:\\\\dir\"   n2", Cat());
        Assert.Contains("no change", Put(Cat()).Output);
    }

    [Fact]
    public void A_retype_keeps_the_id_and_what_points_at_it()
    {
        var put = Put(Cat().Replace("  note  \"first\"   n2", "  doc  \"first\"   n2"));
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("Retype  n2", put.Output);
        Assert.Contains("  doc  \"first\"   n2\n    --row-of 1--> n1\n", Cat());
    }

    [Fact]
    public void A_reference_to_a_deleted_node_is_written_as_one()
    {
        string text = Cat();
        int table = text.IndexOf("  table", StringComparison.Ordinal);
        int note = text.IndexOf("  note", StringComparison.Ordinal);
        var put = Put(text.Remove(table, note - table));
        Assert.True(put.Code == 0, put.Error);
        Assert.Contains("1 more node(s) now hold a reference to a deleted node", put.Output);

        string now = Cat();
        Assert.Contains("--row-of 1--> n1 (deleted)\n", now);
        Assert.Contains("no change", Put(now).Output);

        var refused = Put(now.Replace(" (deleted)", ""));
        Assert.Equal(1, refused.Code);
        Assert.Contains("n1 was deleted; write `n1 (deleted)`", refused.Error);
    }

    [Theory]
    [InlineData("    | 2, -2.25, false", "    | 2, -2.25", "line 7: this row has 2 value(s); each row has 3: [id, x, flag]")]
    [InlineData("[id, x, flag]", "[id, y, flag]", "line 5: the columns are [id, x, flag], not [id, y, flag]")]
    [InlineData("| 2, -2.25, false", "| 2, x, false", "line 7: `x` is not a f64")]
    [InlineData("| 1, 1.5, true", "| -1, 1.5, true", "line 6: `-1` is not a u32")]
    [InlineData("--row-of 1--> n1", "--row-of 1--> n9", "line 9: there is no node n9")]
    [InlineData("  note  \"first\"   n2", "  note  \"first\"   n1", "line 8: n1 is already on line 5")]
    [InlineData("--row-of 1--> n1", "--row-of {row 1}--> n1", "line 9: `{row` is not a u32")]
    [InlineData("  note  \"first\"", "  note  first", "line 8: expected the node's id")]
    public void A_mistake_is_refused_by_its_line_and_nothing_is_written(string from, string to, string said)
    {
        byte[] before = File.ReadAllBytes(path);
        string text = Cat();
        Assert.Contains(from, text);

        var put = Put(text.Replace(from, to));
        Assert.Equal(1, put.Code);
        Assert.Contains(said, put.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Text_printed_from_another_source_is_refused()
    {
        var put = Put(Cat().Replace("source " + identity, "source 0123456789abcdef0123456789abcdef"));
        Assert.Equal(1, put.Code);
        Assert.Contains("this text is from source 0123456789abcdef0123456789abcdef", put.Error);
    }

    [Fact]
    public void An_outermost_line_names_a_node_already_there()
    {
        var put = Put("note  \"loose\"\n");
        Assert.Equal(1, put.Code);
        Assert.Contains("line 1: an outermost line names a node already there", put.Error);
    }

    [Fact]
    public void A_dry_run_reports_the_operations_and_writes_nothing()
    {
        byte[] before = File.ReadAllBytes(path);
        var put = Run("put", path, "--text", Cat().Replace("\"first\"", "\"changed\""), "--dry-run");
        Assert.Equal(0, put.Code);
        Assert.Contains("ModifyValue  n2", put.Output);
        Assert.Contains("dry run: 1 edit(s) checked; file unchanged", put.Output);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A source holding every payload form, printed, then made again from that text as new nodes:
    /// what the reader makes of the printer's text must be the bytes it printed.
    /// </summary>
    [Fact]
    public void Every_payload_form_reads_back_as_the_bytes_it_was_printed_from()
    {
        string forms = Path.Combine(directory, "forms.ss");
        var file = SourceFile.Create();
        var library = new SourceId(47, 53);
        RelationId span = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "span"));
        KindTable.DefineRelation(ref file.Table, span,
            PayloadFormat.TextAfter(PayloadElement.Of(4, PayloadField.Of("at", PayloadScalar.U32, 0))));
        RelationId about = KindTable.InternRelation(ref file.Table, new QualifiedName(library, "about"));
        KindTable.DefineRelation(ref file.Table, about, PayloadFormat.None);

        KindId group = Kind(ref file, library, "group", PayloadFormat.None, span, about);
        var payloads = new (KindId Kind, byte[] Bytes)[]
        {
            (Kind(ref file, library, "vertices", PayloadFormat.Many(PayloadElement.Of(16,
                PayloadField.Of("position", PayloadScalar.F32, 0, 3),
                PayloadField.Of("visible", PayloadScalar.Bool, 12)))),
                Bytes(0, 0, 128, 63, 0, 0, 0, 64, 0, 0, 64, 192, 1, 0, 0, 0,
                      205, 204, 204, 61, 0, 0, 0, 0, 0, 0, 128, 127, 0, 0, 0, 0)),
            (Kind(ref file, library, "stamp", PayloadFormat.One(PayloadElement.Of(18,
                PayloadField.Of("source", PayloadScalar.U8, 0, 16),
                PayloadField.Of("scale", PayloadScalar.F16, 16)))),
                Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 0x66, 0x2e)),
            (Kind(ref file, library, "address", PayloadFormat.TextAfter(PayloadElement.Of(8,
                PayloadField.Of("offset", PayloadScalar.I64, 0)))),
                Concat(Bytes(0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff), Encoding.UTF8.GetBytes("tab\there"))),
            (Kind(ref file, library, "pixels", PayloadFormat.Many(PayloadElement.Sized(4), PayloadElement.Sized(8))),
                Bytes(2, 0, 0, 0, 1, 0, 0, 0, 0xe0, 0x30, 0x30, 0xff, 0x30, 0xe0, 0x30, 0xff)),
            (Kind(ref file, library, "bytes", PayloadFormat.Many(PayloadScalar.U8)),
                Bytes(0, 1, 2, 3, 250, 251, 252, 253, 254, 255)),
            (Kind(ref file, library, "cell", PayloadFormat.One(PayloadElement.Sized(3))), Bytes(7, 8, 9)),
            (Kind(ref file, library, "column", PayloadFormat.Many(PayloadScalar.I16)), Bytes(0xff, 0x7f, 0x00, 0x80, 3, 0)),
            (Kind(ref file, library, "words", PayloadFormat.Text), Encoding.UTF8.GetBytes("\"quoted\" \\ é\u0001")),
            (Kind(ref file, library, "flag", PayloadFormat.One(PayloadScalar.Bool)), Bytes(1)),
        };

        SourceBuild.MintIdentity(ref file.Src);
        NodeId root = SourceBuild.AddRoot(ref file.Src, group);
        NodeId first = SourceBuild.AddChild(ref file.Src, root, group);
        for (int i = 0; i < payloads.Length; i++)
        {
            NodeId node = SourceBuild.AddChild(ref file.Src, first, payloads[i].Kind);
            SourceBuild.SetPayload(ref file.Src, node, payloads[i].Bytes);
        }
        var spanned = new byte[5];
        spanned[0] = 9;
        spanned[4] = (byte)'x';
        SourceBuild.AddRef(ref file.Src, first, span, root, spanned);
        SourceBuild.AddUnboundRef(ref file.Src, first, about, Encoding.UTF8.GetBytes("a \"name\""));

        // Bytes that do not fit their format, beside the rest: printed as they are, and kept.
        KindId count = Kind(ref file, library, "count", PayloadFormat.One(PayloadScalar.U32));
        SourceBuild.SetPayload(ref file.Src, SourceBuild.AddChild(ref file.Src, root, count), Bytes(1, 2, 3));
        File.WriteAllBytes(forms, SourceWrite.ToBytes(in file));

        string whole = Run("cat", forms).Output.Replace("\r\n", "\n");
        Assert.Contains("\n  count  0x010203   n11\n", whole);
        var same = Run("put", forms, "--text", whole);
        Assert.True(same.Code == 0, same.Error);
        Assert.Contains("no change", same.Output);

        var cat = Run("cat", forms, "n1");
        Assert.Equal(0, cat.Code);
        string text = cat.Output.Replace("\r\n", "\n");
        Assert.Contains("group   n1\n", text);
        Assert.Contains("  vertices  [position, visible]   n2\n", text);
        Assert.Contains("    | (1 2 -3), true\n", text);
        Assert.Contains("    | (0.1 0 Infinity), false\n", text);
        Assert.Contains("  stamp  {source 0102030405060708090a0b0c0d0e0f10, scale 0.1}   n3\n", text);
        Assert.Contains("  address  {offset -1} \"tab\\there\"   n4\n", text);
        Assert.Contains("  pixels  0x0200000001000000   n5\n    | 0xe03030ff\n    | 0x30e030ff\n", text);
        Assert.Contains("  bytes  0x00010203fafbfcfdfeff   n6\n", text);
        Assert.Contains("  cell  0x070809   n7\n", text);
        Assert.Contains("  column   n8\n    | 32767\n    | -32768\n    | 3\n", text);
        Assert.Contains("  words  \"\\\"quoted\\\" \\\\ é\\u0001\"   n9\n", text);
        Assert.Contains("  flag  true   n10\n", text);
        Assert.Contains("  --span {at 9} \"x\"--> n0\n", text);
        Assert.Contains("  --about--> unbound \"a \\\"name\\\"\"\n", text);

        // The subtree again under the root, with no ids: every node made anew, from the text.
        var copy = new StringBuilder("group   n0\n");
        int from = text.IndexOf("group   n1\n", StringComparison.Ordinal);
        foreach (string line in text.Substring(from).Split('\n'))
        {
            if (line.Length == 0) continue;
            copy.Append("  ").Append(System.Text.RegularExpressions.Regex.Replace(line, "   n[0-9]+$", "")).Append('\n');
        }
        var made = Run("put", forms, "--text", copy.ToString());
        Assert.True(made.Code == 0, made.Error);

        SourceFile after = SourceRead.FromFile(forms);
        try
        {
            NodeId again = SourceWalk.ChildAt(in after.Src, after.Src.Root, 0);
            Assert.NotEqual(first, again);
            Assert.Equal(payloads.Length, SourceWalk.ChildCount(in after.Src, again));
            for (int i = 0; i < payloads.Length; i++)
            {
                NodeId node = SourceWalk.ChildAt(in after.Src, again, i);
                Assert.Equal(payloads[i].Kind, after.Src.Nodes[node.Index].Kind);
                Assert.Equal(payloads[i].Bytes, SourceWalk.Payload(in after.Src, node).ToArray());
            }

            ReadOnlySpan<Ref> refs = SourceWalk.References(in after.Src, again);
            Assert.Equal(2, refs.Length);
            Assert.Equal(spanned, SourceWalk.EdgeData(in after.Src, in refs[0]).ToArray());
            Assert.Equal("a \"name\"", Encoding.UTF8.GetString(SourceWalk.Label(in after.Src, in refs[1])));
        }
        finally
        {
            SourceFile.Close(ref after);
        }
    }

    static byte[] Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length];
        for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
        return bytes;
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] both = new byte[a.Length + b.Length];
        a.CopyTo(both, 0);
        b.CopyTo(both, a.Length);
        return both;
    }

    byte[] PayloadOf(long node)
    {
        SourceFile file = SourceRead.FromFile(path);
        try
        {
            return SourceWalk.Payload(in file.Src, new NodeId(node)).ToArray();
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    string Cat()
    {
        var cat = Run("cat", path);
        Assert.Equal(0, cat.Code);
        return cat.Output.Replace("\r\n", "\n");
    }

    (int Code, string Output, string Error) Put(string text)
    {
        return Run("put", path, "--text", text);
    }

    static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var oldOutput = Console.Out;
        var oldError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return (Program.Main(args), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOutput);
            Console.SetError(oldError);
        }
    }

    public void Dispose() => Directory.Delete(directory, true);
}
