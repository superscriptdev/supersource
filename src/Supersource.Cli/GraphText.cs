using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;

namespace Supersource.Cli;

/// <summary>
/// A source as text: what `cat` prints and `put` reads back. One line per node — its kind, its
/// payload as its schema reads it, its id — then the rows of a `many` payload, its references,
/// and its children, each indented under it:
///
/// <code>
/// doc   n0
///   table  {rows 2} [id, x, flag]   n1
///     | 1, 1.5, true
///     | 2, -2.25, false
///   note  "check this row"   n2
///     --row-of 1--> n1
/// </code>
///
/// Printing is lossless, so text that comes back unchanged is no change. A payload that does not
/// read as its format says is printed as its bytes, `0x…`, and so is anything the format leaves
/// undescribed; one too large to hold at once is `&lt;N bytes&gt;`, which reads back as "keep it".
/// Reading is directed by the schemas: the kind decides how its payload is read, the relation how
/// its edge data is.
/// </summary>
public static class GraphText
{
    // ---- printing -----------------------------------------------------------------------

    /// <summary>The two lines that open a listing: the source and its counts.</summary>
    public static void Header(in SourceFile file, TextWriter into)
    {
        into.WriteLine("source " + file.Src.Identity);
        into.WriteLine(file.Src.NodeCount + " slots, " +
            (file.Src.NodeCount - file.Src.DeletedNodeCount()) + " live");
        into.WriteLine();
    }

    /// <summary>
    /// A subtree, iteratively, so deep containment cannot overflow the stack. Children are pushed
    /// in reverse so they print in order.
    /// </summary>
    public static void Print(in SourceFile file, NodeId root, TextWriter into)
    {
        long[] work = new long[64];
        int[] depths = new int[64];
        int count = 0;

        work[count] = root.Index;
        depths[count] = 0;
        count++;

        while (count > 0)
        {
            count--;
            NodeId id = new NodeId(work[count]);
            int depth = depths[count];

            Node(in file, id, depth, into, false);

            ReadOnlySpan<long> children = SourceWalk.Children(in file.Src, id);
            if (count + children.Length > work.Length)
            {
                int size = work.Length;
                while (size < count + children.Length) size *= 2;
                Array.Resize(ref work, size);
                Array.Resize(ref depths, size);
            }

            for (int c = children.Length - 1; c >= 0; c--)
            {
                work[count] = children[c];
                depths[count] = depth + 1;
                count++;
            }
        }
    }

    /// <summary>
    /// One node: its line, its rows and its references. With <paramref name="indices"/>, each
    /// reference also shows its position and its relation's qualified name, as `node` prints it;
    /// that form is for reading only.
    /// </summary>
    public static void Node(in SourceFile file, NodeId id, int depth, TextWriter into, bool indices)
    {
        var line = new StringBuilder();
        line.Append(' ', depth * 2);

        KindId kind = file.Src.Nodes[id.Index].Kind;
        line.Append(KindName(in file.Table, kind));
        if (!KindTable.HasSchema(in file.Table, kind)) line.Append(" [no schema]");

        PayloadFormat format = FormatOf(in file.Table, kind);
        string part;
        int rows;
        bool whole = SourceWalk.IsContiguous(in file.Src, id);
        ReadOnlySpan<byte> payload = whole ? SourceWalk.Payload(in file.Src, id) : ReadOnlySpan<byte>.Empty;
        if (whole)
        {
            part = Payload(in format, payload, out rows, elide: true);
        }
        else
        {
            part = "<" + SourceWalk.PayloadLength(in file.Src, id) + " bytes>";
            rows = 0;
        }

        if (part.Length > 0) line.Append("  ").Append(part);
        line.Append("   ").Append(id.ToString());
        into.WriteLine(line.ToString());

        string under = new string(' ', depth * 2 + 2);
        for (int r = 0; r < rows; r++)
        {
            into.WriteLine(under + "| " + Row(in format, payload, r));
        }

        ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, id);
        for (int r = 0; r < refs.Length; r++)
        {
            var edge = new StringBuilder(under);
            if (indices) edge.Append('[').Append(r).Append("] ");
            edge.Append(Reference(in file, in refs[r]));
            if (indices)
            {
                edge.Append("  (relation ")
                    .Append(KindTable.RelationNameOf(in file.Table, refs[r].Relation)).Append(')');
            }
            into.WriteLine(edge.ToString());
        }
    }

    /// <summary>`--relation data--> target`, as a reference line reads.</summary>
    public static string Reference(in SourceFile file, in Ref edge)
    {
        var text = new StringBuilder("--");
        text.Append(RelationName(in file.Table, edge.Relation));

        ReadOnlySpan<byte> data = SourceWalk.EdgeData(in file.Src, in edge);
        if (edge.IsBound && data.Length > 0)
        {
            PayloadFormat format = KindTable.RelationSchemaOf(in file.Table, edge.Relation).Payload;
            text.Append(' ').Append(Inline(in format, data));
        }

        text.Append("--> ");
        if (edge.IsUnbound)
        {
            text.Append("unbound ").Append(Utf8(data, out string label) ? Quote(label) : Raw(data));
        }
        else
        {
            text.Append(edge.Target.ToString());
            if (!file.Src.IsLive(edge.Target)) text.Append(" (deleted)");
        }
        return text.ToString();
    }

    /// <summary>
    /// A kind's local name, or its qualified one when two libraries in this table use the same
    /// local name, so the text names exactly one kind.
    /// </summary>
    public static string KindName(in KindTable table, KindId kind)
    {
        QualifiedName name = KindTable.NameOf(in table, kind);
        if (name.IsNone) return "<unknown-kind>";
        for (int i = 0; i < table.KindCount; i++)
        {
            if (i == kind.Value) continue;
            QualifiedName other = KindTable.NameOf(in table, new KindId(i));
            if (!other.IsNone && other.Name == name.Name) return name.ToString();
        }
        return name.Name;
    }

    public static string RelationName(in KindTable table, RelationId relation)
    {
        QualifiedName name = KindTable.RelationNameOf(in table, relation);
        if (name.IsNone) return "<unknown-relation>";
        for (int i = 0; i < table.RelationCount; i++)
        {
            if (i == relation.Value) continue;
            QualifiedName other = KindTable.RelationNameOf(in table, new RelationId(i));
            if (!other.IsNone && other.Name == name.Name) return name.ToString();
        }
        return name.Name;
    }

    /// <summary>How a kind's payload reads; bytes, for a kind the file carries no schema for.</summary>
    public static PayloadFormat FormatOf(in KindTable table, KindId kind)
    {
        return KindTable.HasSchema(in table, kind)
            ? KindTable.SchemaOf(in table, kind).Payload
            : PayloadFormat.Opaque;
    }

    /// <summary>
    /// Bytes nobody reads as numbers or text, past which <see cref="Payload"/> with
    /// <c>elide</c> prints a byte count instead: `&lt;N bytes&gt;`, which `put` reads as "keep it".
    /// </summary>
    public const int ShownBytes = 64;

    /// <summary>
    /// The part of a payload on its node's line, and how many rows follow on lines of their own.
    /// Empty for an empty payload. With <paramref name="elide"/>, a payload that would print as
    /// more than <see cref="ShownBytes"/> raw bytes prints as its length.
    /// </summary>
    public static string Payload(in PayloadFormat format, ReadOnlySpan<byte> payload, out int rows,
        bool elide = false)
    {
        rows = 0;
        if (payload.Length == 0) return string.Empty;

        switch (format.Form)
        {
            case PayloadForm.Text when format.HeaderSize == 0:
                return Utf8(payload, out string text) ? Quote(text) : Whole(payload, elide);

            case PayloadForm.Text when payload.Length >= format.HeaderSize
                && Describable(in format.Header)
                && Utf8(payload.Slice(format.HeaderSize), out string after):
                return Element(in format.Header, payload.Slice(0, format.HeaderSize)) + " " + Quote(after);

            case PayloadForm.One when payload.Length == format.Element.Size && Describable(in format.Element):
                return Element(in format.Element, payload);

            case PayloadForm.Many when Fits(in format, payload.Length) && Describable(in format.Header):
            {
                var line = new StringBuilder();
                if (format.HeaderSize > 0)
                {
                    line.Append(Element(in format.Header, payload.Slice(0, format.HeaderSize)));
                }

                ReadOnlySpan<byte> elements = payload.Slice(format.ElementsStart);
                if (format.Element.Is(PayloadScalar.U8))
                {
                    // Bytes declared as bytes: one run, not a row per byte.
                    if (elide && elements.Length > ShownBytes) return Whole(payload, elide);
                    if (elements.Length > 0)
                    {
                        if (line.Length > 0) line.Append(' ');
                        line.Append(Raw(elements));
                    }
                    return line.ToString();
                }

                if (Columned(in format.Element))
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(Columns(in format.Element));
                }
                rows = format.ElementCount(payload.Length);
                return line.ToString();
            }

            default:
                return Whole(payload, elide);
        }
    }

    /// <summary>A whole payload as its bytes, or as its length when there are too many to read.</summary>
    static string Whole(ReadOnlySpan<byte> payload, bool elide)
    {
        return elide && payload.Length > ShownBytes ? "<" + payload.Length + " bytes>" : Raw(payload);
    }

    /// <summary>One row of a `many` payload: its fields' values, in the order the schema declares them.</summary>
    public static string Row(in PayloadFormat format, ReadOnlySpan<byte> payload, int row)
    {
        int size = format.Element.Size;
        ReadOnlySpan<byte> bytes = payload.Slice(format.ElementsStart + row * size, size);
        if (!format.Element.IsDescribed) return Raw(bytes);

        var text = new StringBuilder();
        PayloadField[] fields = format.Element.Fields;
        for (int f = 0; f < fields.Length; f++)
        {
            if (f > 0) text.Append(", ");
            text.Append(Value(in fields[f], bytes));
        }
        return text.ToString();
    }

    /// <summary>
    /// A payload or edge data on one line: what <see cref="Payload"/> prints, except that a
    /// `many` that would need rows is printed as its bytes.
    /// </summary>
    public static string Inline(in PayloadFormat format, ReadOnlySpan<byte> bytes)
    {
        string part = Payload(in format, bytes, out int rows);
        return rows == 0 ? part : Raw(bytes);
    }

    /// <summary>Everything a payload prints as, node line and rows together, for comparing two.</summary>
    public static string Rendered(in PayloadFormat format, ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(Payload(in format, payload, out int rows));
        for (int r = 0; r < rows; r++) text.Append('\n').Append(Row(in format, payload, r));
        return text.ToString();
    }

    static bool Fits(in PayloadFormat format, int length)
    {
        return format.HasElements && length >= format.ElementsStart
            && (length - format.ElementsStart) % format.Element.Size == 0;
    }

    /// <summary>
    /// Whether an element prints as itself: one unnamed scalar, named fields, or bytes when it is
    /// only sized. Several fields one of which has no name cannot be written back by name.
    /// </summary>
    static bool Describable(in PayloadElement element)
    {
        if (!element.IsDescribed || Single(in element)) return true;
        for (int f = 0; f < element.Fields.Length; f++)
        {
            if (string.IsNullOrEmpty(element.Fields[f].Name)) return false;
        }
        return true;
    }

    /// <summary>One unnamed scalar, which prints as its bare value.</summary>
    static bool Single(in PayloadElement element)
    {
        return element.Fields != null && element.Fields.Length == 1
            && string.IsNullOrEmpty(element.Fields[0].Name) && element.Fields[0].Repeat == 1;
    }

    /// <summary>Whether a row's values need a header naming them.</summary>
    static bool Columned(in PayloadElement element)
    {
        return element.IsDescribed && !Single(in element);
    }

    static string Columns(in PayloadElement element)
    {
        var text = new StringBuilder("[");
        for (int f = 0; f < element.Fields.Length; f++)
        {
            if (f > 0) text.Append(", ");
            text.Append(ColumnName(in element.Fields[f]));
        }
        return text.Append(']').ToString();
    }

    static string ColumnName(in PayloadField field)
    {
        return string.IsNullOrEmpty(field.Name) ? "_" : field.Name;
    }

    /// <summary>One element: its value when it is one unnamed scalar, its named fields otherwise.</summary>
    static string Element(in PayloadElement element, ReadOnlySpan<byte> bytes)
    {
        if (!element.IsDescribed) return Raw(bytes);
        if (Single(in element)) return Scalar(element.Fields[0].Type, bytes.Slice(element.Fields[0].Offset));

        var text = new StringBuilder("{");
        for (int f = 0; f < element.Fields.Length; f++)
        {
            if (f > 0) text.Append(", ");
            text.Append(element.Fields[f].Name).Append(' ').Append(Value(in element.Fields[f], bytes));
        }
        return text.Append('}').ToString();
    }

    /// <summary>A field's value: a scalar, a run of them in parentheses, or hex for a run of bytes.</summary>
    static string Value(in PayloadField field, ReadOnlySpan<byte> element)
    {
        // A run of bytes is an id or a digest far more often than a list of numbers.
        if (field.Type == PayloadScalar.U8 && field.Repeat > 1)
        {
            return Convert.ToHexString(element.Slice(field.Offset, field.Repeat)).ToLowerInvariant();
        }
        if (field.Repeat == 1) return Scalar(field.Type, element.Slice(field.Offset));

        int size = PayloadScalars.Size(field.Type);
        var text = new StringBuilder("(");
        for (int n = 0; n < field.Repeat; n++)
        {
            if (n > 0) text.Append(' ');
            text.Append(Scalar(field.Type, element.Slice(field.Offset + n * size)));
        }
        return text.Append(')').ToString();
    }

    static string Scalar(PayloadScalar type, ReadOnlySpan<byte> at)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        switch (type)
        {
            case PayloadScalar.I8: return ((sbyte)at[0]).ToString(c);
            case PayloadScalar.U8: return at[0].ToString(c);
            case PayloadScalar.Bool: return at[0] == 0 ? "false" : "true";
            case PayloadScalar.I16: return BinaryPrimitives.ReadInt16LittleEndian(at).ToString(c);
            case PayloadScalar.U16: return BinaryPrimitives.ReadUInt16LittleEndian(at).ToString(c);
            case PayloadScalar.F16: return BinaryPrimitives.ReadHalfLittleEndian(at).ToString(c);
            case PayloadScalar.I32: return BinaryPrimitives.ReadInt32LittleEndian(at).ToString(c);
            case PayloadScalar.U32: return BinaryPrimitives.ReadUInt32LittleEndian(at).ToString(c);
            case PayloadScalar.F32: return BinaryPrimitives.ReadSingleLittleEndian(at).ToString("R", c);
            case PayloadScalar.I64: return BinaryPrimitives.ReadInt64LittleEndian(at).ToString(c);
            case PayloadScalar.U64: return BinaryPrimitives.ReadUInt64LittleEndian(at).ToString(c);
            case PayloadScalar.F64: return BinaryPrimitives.ReadDoubleLittleEndian(at).ToString("R", c);
            default: return "?";
        }
    }

    /// <summary>Bytes as they are: `0x` and lowercase hex.</summary>
    public static string Raw(ReadOnlySpan<byte> bytes)
    {
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static bool Utf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    /// <summary>A string in double quotes, escaped the way JSON escapes one, so it stays on its line.</summary>
    public static string Quote(string text)
    {
        var into = new StringBuilder("\"");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"': into.Append("\\\""); break;
                case '\\': into.Append("\\\\"); break;
                case '\n': into.Append("\\n"); break;
                case '\r': into.Append("\\r"); break;
                case '\t': into.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f || c == 0x85 || c == 0x2028 || c == 0x2029)
                    {
                        into.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else into.Append(c);
                    break;
            }
        }
        return into.Append('"').ToString();
    }

    // ---- reading ------------------------------------------------------------------------

    /// <summary>One node line as read: what it says the node is, and what is under it.</summary>
    public sealed class Entry
    {
        public int Line;
        public int Indent;
        public KindId Kind;

        /// <summary>The node it restates, or none for a node the text adds.</summary>
        public NodeId Id = NodeId.None;

        /// <summary>A new node's name for the rest of the text: `@name`.</summary>
        public string Handle;

        /// <summary>Null when the line says to keep the payload as it is: `&lt;N bytes&gt;`.</summary>
        public byte[] Payload;

        public readonly List<Edge> Edges = new List<Edge>();
        public readonly List<Entry> Children = new List<Entry>();

        /// <summary>For a `many` payload: rows still to come, after the header already read.</summary>
        internal MemoryStream Rows;
        internal bool TakesRows;
        internal PayloadFormat Format;
    }

    /// <summary>One reference line as read.</summary>
    public sealed class Edge
    {
        public int Line;
        public RelationId Relation;

        /// <summary>A node id, or none for an unbound reference or one naming a handle.</summary>
        public NodeId Target = NodeId.None;
        public string Handle;
        public bool Unbound;

        /// <summary>Written as `(deleted)`: a dangling reference, kept as one.</summary>
        public bool Dangling;

        /// <summary>The edge data: for an unbound reference, its label.</summary>
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>
    /// Reads text into the entries it restates, resolving kind and relation names against the
    /// file's table and reading every payload and edge data as its schema says. Throws
    /// <see cref="ArgumentException"/> naming the line when the text does not read.
    /// </summary>
    public static List<Entry> Read(string text, in SourceFile file)
    {
        var tops = new List<Entry>();
        var open = new List<Entry>();
        bool started = false;

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string raw = lines[n];
            int number = n + 1;
            if (raw.Trim().Length == 0) continue;

            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;
            if (indent < raw.Length && raw[indent] == '\t')
            {
                throw Fail(number, "indent with spaces, not tabs");
            }
            string body = raw.Substring(indent).TrimEnd();

            if (!started && indent == 0 && IsPreamble(body, in file, number)) continue;
            started = true;

            // The entry this line belongs under: the nearest one above it, less indented.
            while (open.Count > 0 && open[open.Count - 1].Indent >= indent) Close(open, number);
            Entry owner = open.Count > 0 ? open[open.Count - 1] : null;

            try
            {
                if (body.StartsWith("|", StringComparison.Ordinal))
                {
                    if (owner == null) throw new FormatException("a row belongs under the node whose payload it is part of");
                    ReadRow(owner, body.Substring(1));
                }
                else if (body.StartsWith("--", StringComparison.Ordinal))
                {
                    if (owner == null) throw new FormatException("a reference belongs under the node it leaves from");
                    owner.Edges.Add(ReadEdge(body, in file, number));
                }
                else
                {
                    Entry entry = ReadNode(body, in file);
                    entry.Line = number;
                    entry.Indent = indent;
                    if (owner == null) tops.Add(entry);
                    else owner.Children.Add(entry);
                    open.Add(entry);
                }
            }
            catch (Exception error) when (error is FormatException || error is OverflowException
                || error is ArgumentException || error is FormatCorruptException)
            {
                throw Fail(number, error.Message);
            }
        }

        while (open.Count > 0) Close(open, lines.Length);
        return tops;
    }

    static ArgumentException Fail(int line, string message)
    {
        return new ArgumentException("line " + line + ": " + message);
    }

    /// <summary>
    /// The lines `cat` opens with. The source line must name this source: text printed from
    /// another one names other nodes by the same ids.
    /// </summary>
    static bool IsPreamble(string body, in SourceFile file, int number)
    {
        if (body.StartsWith("source ", StringComparison.Ordinal) && body.Length == 7 + 32
            && IsHex(body.AsSpan(7)))
        {
            if (!string.Equals(body.Substring(7), file.Src.Identity.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(number, "this text is from source " + body.Substring(7)
                    + ", not " + file.Src.Identity);
            }
            return true;
        }

        string[] words = body.Split(' ');
        return words.Length == 4 && words[1] == "slots," && words[3] == "live"
            && long.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && long.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Finishes the innermost open entry: a `many` payload is its header and its rows.</summary>
    static void Close(List<Entry> open, int number)
    {
        Entry entry = open[open.Count - 1];
        open.RemoveAt(open.Count - 1);
        if (entry.Rows == null) return;

        byte[] rows = entry.Rows.ToArray();
        byte[] whole = new byte[entry.Payload.Length + rows.Length];
        entry.Payload.CopyTo(whole, 0);
        rows.CopyTo(whole, entry.Payload.Length);
        entry.Payload = whole;
        entry.Rows = null;
    }

    static Entry ReadNode(string body, in SourceFile file)
    {
        var at = new Cursor(body);
        var entry = new Entry();

        string name = at.Word();
        entry.Kind = Edit.FindKind(in file, name, requireSchema: false);
        at.Space();
        at.Take("[no schema]");

        PayloadFormat format = FormatOf(in file.Table, entry.Kind);
        entry.Format = format;
        at.Space();

        if (at.AtTail())
        {
            entry.Payload = Array.Empty<byte>();
            entry.TakesRows = format.Form == PayloadForm.Many && format.HeaderSize == 0 && Rowed(in format);
        }
        else if (at.Peek() == '<')
        {
            at.Kept();
            entry.Payload = null;
        }
        else
        {
            entry.Payload = ReadPayload(ref at, in format, out bool exact);
            entry.TakesRows = !exact && format.Form == PayloadForm.Many && Rowed(in format);
        }

        if (entry.TakesRows) entry.Rows = new MemoryStream();

        at.Space();
        if (!at.AtEnd)
        {
            string tail = at.Word();
            if (tail.StartsWith("@", StringComparison.Ordinal) && tail.Length > 1)
            {
                entry.Handle = tail.Substring(1);
            }
            else if (IsId(tail))
            {
                entry.Id = Program.ParseNode(tail);
            }
            else
            {
                throw new FormatException("expected the node's id (`n12`), a new node's `@name`, or the end of the line; found `"
                    + tail + "`");
            }
        }

        at.Space();
        if (!at.AtEnd) throw new FormatException("unexpected `" + at.Rest + "` after the node's id");
        return entry;
    }

    /// <summary>
    /// How many bytes a format's own reading takes as `0x…` before anything else on the line: all
    /// of them (0) for opaque bytes and a `many u8`, an only-sized first element's size, or -1
    /// when the format never starts with bytes — where `0x…` can only be the payload exactly.
    /// </summary>
    static int LeadingBytes(in PayloadFormat format)
    {
        switch (format.Form)
        {
            case PayloadForm.Opaque:
                return 0;
            case PayloadForm.One:
                return format.Element.IsDescribed ? -1 : format.Element.Size;
            case PayloadForm.Text:
                return format.HeaderSize > 0 && !format.Header.IsDescribed ? format.HeaderSize : -1;
            case PayloadForm.Many:
                if (format.HeaderSize > 0) return format.Header.IsDescribed ? -1 : format.HeaderSize;
                return format.Element.Is(PayloadScalar.U8) ? 0 : -1;
            default:
                return -1;
        }
    }

    /// <summary>A `many` whose elements come as rows rather than as one run of bytes.</summary>
    static bool Rowed(in PayloadFormat format)
    {
        return format.HasElements && !format.Element.Is(PayloadScalar.U8);
    }

    /// <summary>
    /// A payload's node-line part, read as its format says. For a `many` with rows, that is the
    /// header (and the columns, checked), and the rows follow on lines of their own.
    /// <paramref name="exact"/> says the text gave the bytes themselves, `0x…`, where the format
    /// reads otherwise: what `cat` prints for a payload that does not fit its format.
    /// </summary>
    static byte[] ReadPayload(ref Cursor at, in PayloadFormat format, out bool exact)
    {
        exact = false;
        if (at.AtRaw())
        {
            Cursor look = at;
            byte[] raw = look.Raw();
            int leading = LeadingBytes(in format);
            if (leading < 0 || (leading > 0 && raw.Length != leading))
            {
                at = look;
                exact = true;
                return raw;
            }
        }

        switch (format.Form)
        {
            case PayloadForm.None:
                throw new FormatException("this kind declares no payload");

            case PayloadForm.Opaque:
                if (!at.AtRaw()) throw new FormatException("an undescribed payload is written as bytes, `0x…`");
                return at.Raw();

            case PayloadForm.Text:
            {
                byte[] header = Array.Empty<byte>();
                if (format.HeaderSize > 0)
                {
                    header = ReadElement(ref at, in format.Header);
                    at.Space();
                }
                byte[] text = at.Peek() == '"'
                    ? new UTF8Encoding(false, true).GetBytes(at.Quoted())
                    : Array.Empty<byte>();
                return Join(header, text);
            }

            case PayloadForm.One:
                return ReadElement(ref at, in format.Element);

            case PayloadForm.Many:
            {
                if (!format.HasElements) throw new FormatException("a `many` of no size is written as bytes, `0x…`");
                byte[] header = Array.Empty<byte>();
                if (format.HeaderSize > 0)
                {
                    header = ReadElement(ref at, in format.Header);
                    at.Space();
                }

                if (format.Element.Is(PayloadScalar.U8))
                {
                    byte[] run = at.AtRaw() ? at.Raw() : Array.Empty<byte>();
                    return Join(header, run);
                }

                // Edge data has no lines of its own, so a `many` with elements there is printed
                // as its bytes, which took the branch above; what reaches here has none.
                if (at.Peek() == '[')
                {
                    string columns = at.Bracketed();
                    string expected = Columns(in format.Element);
                    if (Columned(in format.Element) && !SameColumns(columns, expected))
                    {
                        throw new FormatException("the columns are " + expected + ", not " + columns);
                    }
                }
                return header;
            }

            default:
                throw new FormatException("a payload of an unknown form is written as bytes, `0x…`");
        }
    }

    static bool SameColumns(string given, string expected)
    {
        return Normalise(given) == Normalise(expected);
    }

    static string Normalise(string columns)
    {
        var text = new StringBuilder();
        for (int i = 0; i < columns.Length; i++)
        {
            if (columns[i] != ' ') text.Append(columns[i]);
        }
        return text.ToString();
    }

    static void ReadRow(Entry owner, string text)
    {
        if (!owner.TakesRows)
        {
            PayloadFormat format = owner.Format;
            bool rowed = format.Form == PayloadForm.Many && Rowed(in format);
            if (owner.Payload == null) throw new FormatException("a payload kept as it is (`<N bytes>`) cannot also be given rows");
            if (rowed && format.HeaderSize > 0 && owner.Payload.Length == 0)
            {
                throw new FormatException("this node's header comes first, on its line, before its rows");
            }
            throw new FormatException(rowed
                ? "a payload given as bytes, `0x…`, is the whole payload and has no rows"
                : "rows belong to a `many` payload, and this node's payload is not one");
        }

        PayloadElement element = owner.Format.Element;
        var at = new Cursor(text);
        at.Space();
        byte[] row = new byte[element.Size];

        if (!element.IsDescribed)
        {
            Put(at.Raw(), row, 0, "a row");
        }
        else
        {
            PayloadField[] fields = element.Fields;
            for (int f = 0; f < fields.Length; f++)
            {
                if (f > 0)
                {
                    at.Space();
                    if (!at.Take(","))
                    {
                        throw new FormatException("this row has " + f + " value(s); each row has "
                            + fields.Length + ": " + Columns(in element));
                    }
                    at.Space();
                }
                if (at.AtEnd)
                {
                    throw new FormatException("this row has " + f + " value(s); each row has "
                        + fields.Length + ": " + Columns(in element));
                }
                ReadValue(ref at, in fields[f], row);
            }
        }

        at.Space();
        if (!at.AtEnd)
        {
            throw new FormatException("this row has more than the " + RowWidth(in element)
                + " value(s) each row has" + (element.IsDescribed ? ": " + Columns(in element) : ""));
        }
        owner.Rows.Write(row, 0, row.Length);
    }

    static int RowWidth(in PayloadElement element)
    {
        return element.IsDescribed ? element.Fields.Length : 1;
    }

    static Edge ReadEdge(string body, in SourceFile file, int number)
    {
        var at = new Cursor(body);
        var edge = new Edge { Line = number };
        at.Take("--");

        string name = at.RelationWord();
        if (name.Length == 0) throw new FormatException("a reference names its relation: `--about--> n12`");
        edge.Relation = Edit.FindRelation(in file, name);
        PayloadFormat format = KindTable.RelationSchemaOf(in file.Table, edge.Relation).Payload;

        at.Space();
        if (!at.Take("-->"))
        {
            edge.Data = ReadPayload(ref at, in format, out _);
            at.Space();
            if (!at.Take("-->")) throw new FormatException("expected `-->` after the edge data, found `" + at.Rest + "`");
        }
        at.Space();

        string target = at.Word();
        if (target == "unbound")
        {
            if (edge.Data.Length > 0)
            {
                throw new FormatException("an unbound reference's edge data is its label: `--> unbound \"name\"`");
            }
            edge.Unbound = true;
            at.Space();
            if (at.Peek() == '"') edge.Data = new UTF8Encoding(false, true).GetBytes(at.Quoted());
            else if (at.AtRaw()) edge.Data = at.Raw();
        }
        else if (target.StartsWith("@", StringComparison.Ordinal) && target.Length > 1)
        {
            edge.Handle = target.Substring(1);
        }
        else if (IsId(target))
        {
            edge.Target = Program.ParseNode(target);
            at.Space();
            edge.Dangling = at.Take("(deleted)");
        }
        else
        {
            throw new FormatException("a reference points at `n12`, a new node's `@name`, or `unbound`; found `"
                + target + "`");
        }

        at.Space();
        if (!at.AtEnd) throw new FormatException("unexpected `" + at.Rest + "` after the reference's target");
        return edge;
    }

    /// <summary>An element: bare for one unnamed scalar, `{name value, …}` for named fields, `0x…` when only sized.</summary>
    static byte[] ReadElement(ref Cursor at, in PayloadElement element)
    {
        byte[] bytes = new byte[element.Size];
        if (!element.IsDescribed)
        {
            Put(at.Raw(), bytes, 0, "this element");
            return bytes;
        }
        if (Single(in element))
        {
            ReadValue(ref at, in element.Fields[0], bytes);
            return bytes;
        }

        if (!at.Take("{")) throw new FormatException("expected `{` opening the fields " + FieldList(in element));
        var seen = new bool[element.Fields.Length];
        int found = 0;
        at.Space();
        while (!at.Take("}"))
        {
            if (found > 0)
            {
                if (!at.Take(",")) throw new FormatException("expected `,` or `}` between fields, found `" + at.Rest + "`");
                at.Space();
            }

            string name = at.FieldName();
            int f = Array.FindIndex(element.Fields, field => field.Name == name);
            if (f < 0) throw new FormatException("no field `" + name + "`; the fields are " + FieldList(in element));
            if (seen[f]) throw new FormatException("field `" + name + "` is given twice");
            seen[f] = true;
            found++;
            at.Space();
            ReadValue(ref at, in element.Fields[f], bytes);
            at.Space();
            if (at.AtEnd) throw new FormatException("expected `}` closing the fields");
        }

        for (int f = 0; f < seen.Length; f++)
        {
            if (!seen[f]) throw new FormatException("field `" + element.Fields[f].Name + "` is missing");
        }
        return bytes;
    }

    static string FieldList(in PayloadElement element)
    {
        var names = new string[element.Fields.Length];
        for (int f = 0; f < names.Length; f++) names[f] = element.Fields[f].Name;
        return string.Join(", ", names);
    }

    /// <summary>A field's value into an element's bytes, at the field's offset.</summary>
    static void ReadValue(ref Cursor at, in PayloadField field, byte[] element)
    {
        int size = PayloadScalars.Size(field.Type);
        if (field.Type == PayloadScalar.U8 && field.Repeat > 1)
        {
            byte[] run = at.Hex();
            if (run.Length != field.Repeat)
            {
                throw new FormatException(FieldLabel(in field) + " is " + field.Repeat + " bytes, "
                    + (2 * field.Repeat) + " hex digits; found " + run.Length + " byte(s)");
            }
            run.CopyTo(element, field.Offset);
            return;
        }

        if (field.Repeat == 1)
        {
            WriteScalar(field.Type, at.Token(), element, field.Offset);
            return;
        }

        if (!at.Take("(")) throw new FormatException(FieldLabel(in field) + " holds " + field.Repeat + " values, in `( )`");
        for (int n = 0; n < field.Repeat; n++)
        {
            at.Space();
            if (at.Peek() == ')') throw new FormatException(FieldLabel(in field) + " holds " + field.Repeat + " values; found " + n);
            WriteScalar(field.Type, at.Token(), element, field.Offset + n * size);
        }
        at.Space();
        if (!at.Take(")")) throw new FormatException(FieldLabel(in field) + " holds " + field.Repeat + " values; found more");
    }

    static string FieldLabel(in PayloadField field)
    {
        return string.IsNullOrEmpty(field.Name) ? "this value" : "`" + field.Name + "`";
    }

    static void WriteScalar(PayloadScalar type, string token, byte[] into, int at)
    {
        if (token.Length == 0) throw new FormatException("expected a " + PayloadScalars.Name(type));
        CultureInfo c = CultureInfo.InvariantCulture;
        const NumberStyles Integer = NumberStyles.AllowLeadingSign;
        const NumberStyles Real = NumberStyles.Float;
        Span<byte> span = new Span<byte>(into, at, PayloadScalars.Size(type));
        bool ok;

        switch (type)
        {
            case PayloadScalar.Bool:
                ok = token == "true" || token == "false";
                if (ok) span[0] = token == "true" ? (byte)1 : (byte)0;
                break;
            case PayloadScalar.I8:
                ok = sbyte.TryParse(token, Integer, c, out sbyte i8);
                span[0] = (byte)i8;
                break;
            case PayloadScalar.U8:
                ok = byte.TryParse(token, Integer, c, out byte u8);
                span[0] = u8;
                break;
            case PayloadScalar.I16:
                ok = short.TryParse(token, Integer, c, out short i16);
                BinaryPrimitives.WriteInt16LittleEndian(span, i16);
                break;
            case PayloadScalar.U16:
                ok = ushort.TryParse(token, Integer, c, out ushort u16);
                BinaryPrimitives.WriteUInt16LittleEndian(span, u16);
                break;
            case PayloadScalar.I32:
                ok = int.TryParse(token, Integer, c, out int i32);
                BinaryPrimitives.WriteInt32LittleEndian(span, i32);
                break;
            case PayloadScalar.U32:
                ok = uint.TryParse(token, Integer, c, out uint u32);
                BinaryPrimitives.WriteUInt32LittleEndian(span, u32);
                break;
            case PayloadScalar.I64:
                ok = long.TryParse(token, Integer, c, out long i64);
                BinaryPrimitives.WriteInt64LittleEndian(span, i64);
                break;
            case PayloadScalar.U64:
                ok = ulong.TryParse(token, Integer, c, out ulong u64);
                BinaryPrimitives.WriteUInt64LittleEndian(span, u64);
                break;
            case PayloadScalar.F16:
                ok = Half.TryParse(token, Real, c, out Half f16);
                BinaryPrimitives.WriteHalfLittleEndian(span, f16);
                break;
            case PayloadScalar.F32:
                ok = float.TryParse(token, Real, c, out float f32);
                BinaryPrimitives.WriteSingleLittleEndian(span, f32);
                break;
            case PayloadScalar.F64:
                ok = double.TryParse(token, Real, c, out double f64);
                BinaryPrimitives.WriteDoubleLittleEndian(span, f64);
                break;
            default:
                ok = false;
                break;
        }

        if (!ok)
        {
            throw new FormatException("`" + token + "` is not " + (type == PayloadScalar.Bool
                ? "true or false" : "a " + PayloadScalars.Name(type)));
        }
    }

    static void Put(byte[] value, byte[] into, int at, string what)
    {
        if (value.Length != into.Length - at)
        {
            throw new FormatException(what + " is " + (into.Length - at) + " bytes; found " + value.Length);
        }
        value.CopyTo(into, at);
    }

    static byte[] Join(byte[] a, byte[] b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        byte[] both = new byte[a.Length + b.Length];
        a.CopyTo(both, 0);
        b.CopyTo(both, a.Length);
        return both;
    }

    static bool IsId(string word)
    {
        if (word.Length < 2 || word[0] != 'n') return false;
        for (int i = 1; i < word.Length; i++)
        {
            if (word[i] < '0' || word[i] > '9') return false;
        }
        return true;
    }

    static bool IsHex(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i])) return false;
        }
        return true;
    }

    /// <summary>A position in one line of text.</summary>
    struct Cursor
    {
        readonly string text;
        int at;

        public Cursor(string text)
        {
            this.text = text;
            at = 0;
        }

        public bool AtEnd => at >= text.Length;
        public string Rest => text.Substring(at);
        public char Peek() => at < text.Length ? text[at] : '\0';

        public void Space()
        {
            while (at < text.Length && text[at] == ' ') at++;
        }

        public bool Take(string literal)
        {
            if (string.CompareOrdinal(text, at, literal, 0, literal.Length) != 0) return false;
            at += literal.Length;
            return true;
        }

        /// <summary>Up to the next space.</summary>
        public string Word()
        {
            int start = at;
            while (at < text.Length && text[at] != ' ') at++;
            return text.Substring(start, at - start);
        }

        /// <summary>A relation's name: up to a space or the `-->` that ends it.</summary>
        public string RelationWord()
        {
            int start = at;
            while (at < text.Length && text[at] != ' '
                && string.CompareOrdinal(text, at, "-->", 0, 3) != 0) at++;
            return text.Substring(start, at - start);
        }

        /// <summary>A field's name inside `{ }`: up to the space before its value.</summary>
        public string FieldName()
        {
            int start = at;
            while (at < text.Length && text[at] != ' ' && text[at] != ',' && text[at] != '}') at++;
            if (at == start) throw new FormatException("expected a field name, found `" + Rest + "`");
            return text.Substring(start, at - start);
        }

        /// <summary>
        /// A scalar's spelling: up to a space, a delimiter, or the `--` of an arrow, which no
        /// number contains.
        /// </summary>
        public string Token()
        {
            int start = at;
            while (at < text.Length)
            {
                char c = text[at];
                if (c == ' ' || c == ',' || c == '}' || c == ')' || c == ']' || c == '>') break;
                if (c == '-' && at > start && at + 1 < text.Length && text[at + 1] == '-') break;
                at++;
            }
            return text.Substring(start, at - start);
        }

        /// <summary>The node's id or a new node's handle, or nothing: what ends a node line.</summary>
        public bool AtTail()
        {
            if (AtEnd) return true;
            if (text[at] == '@') return true;
            if (text[at] != 'n') return false;
            int i = at + 1;
            if (i >= text.Length || text[i] < '0' || text[i] > '9') return false;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            return i == text.Length || text[i] == ' ';
        }

        public bool AtRaw()
        {
            return string.CompareOrdinal(text, at, "0x", 0, 2) == 0;
        }

        /// <summary>`0x` and hex digits.</summary>
        public byte[] Raw()
        {
            if (!Take("0x")) throw new FormatException("expected bytes, `0x…`, found `" + Rest + "`");
            return HexDigits();
        }

        /// <summary>Hex digits, with or without `0x`.</summary>
        public byte[] Hex()
        {
            Take("0x");
            return HexDigits();
        }

        byte[] HexDigits()
        {
            int start = at;
            while (at < text.Length && Uri.IsHexDigit(text[at])) at++;
            int count = at - start;
            if (count % 2 != 0) throw new FormatException("hex digits come in pairs; found " + count);
            return Convert.FromHexString(text.AsSpan(start, count));
        }

        /// <summary>`&lt;N bytes&gt;`: a payload kept as it is.</summary>
        public void Kept()
        {
            int close = text.IndexOf('>', at);
            if (close < 0) throw new FormatException("expected `<N bytes>`");
            string inside = text.Substring(at + 1, close - at - 1);
            if (!inside.EndsWith(" bytes", StringComparison.Ordinal)
                || !long.TryParse(inside.Substring(0, inside.Length - 6), NumberStyles.None,
                    CultureInfo.InvariantCulture, out _))
            {
                throw new FormatException("expected `<N bytes>`, found `<" + inside + ">`");
            }
            at = close + 1;
        }

        /// <summary>`[ … ]`, returned with its brackets.</summary>
        public string Bracketed()
        {
            int close = text.IndexOf(']', at);
            if (close < 0) throw new FormatException("expected `]` closing the columns");
            string inside = text.Substring(at, close - at + 1);
            at = close + 1;
            return inside;
        }

        /// <summary>A double-quoted string with JSON's escapes.</summary>
        public string Quoted()
        {
            if (!Take("\"")) throw new FormatException("expected a string in double quotes");
            var into = new StringBuilder();
            while (true)
            {
                if (at >= text.Length) throw new FormatException("a string is missing its closing `\"`");
                char c = text[at++];
                if (c == '"') return into.ToString();
                if (c != '\\')
                {
                    into.Append(c);
                    continue;
                }
                if (at >= text.Length) throw new FormatException("a string ends in the middle of an escape");
                char e = text[at++];
                switch (e)
                {
                    case '"': into.Append('"'); break;
                    case '\\': into.Append('\\'); break;
                    case '/': into.Append('/'); break;
                    case 'n': into.Append('\n'); break;
                    case 'r': into.Append('\r'); break;
                    case 't': into.Append('\t'); break;
                    case 'b': into.Append('\b'); break;
                    case 'f': into.Append('\f'); break;
                    case 'u':
                        if (at + 4 > text.Length || !IsHex(text.AsSpan(at, 4)))
                        {
                            throw new FormatException("`\\u` takes four hex digits");
                        }
                        into.Append((char)Convert.ToInt32(text.Substring(at, 4), 16));
                        at += 4;
                        break;
                    default:
                        throw new FormatException("`\\" + e + "` is not an escape; a backslash is written `\\\\`");
                }
            }
        }
    }
}
