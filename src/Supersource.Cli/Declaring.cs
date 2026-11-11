using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Supersource.Format;
using Supersource.Kinds;

namespace Supersource.Cli;

/// <summary>
/// Creating a source, and declaring the kinds a kind library holds.
///
/// A kind library is a source: <see cref="QualifiedName"/>'s `Library` is a
/// <see cref="SourceId"/>, so a kind's identity is the identity of the source that declares it
/// plus a local name. <see cref="Declare"/> writes that source.
/// </summary>
public static class Declaring
{
    // ---- new --------------------------------------------------------------------------------

    /// <summary>
    /// `new &lt;out.ss&gt; --root &lt;kind&gt; [--label text] [--kinds source.ss]
    /// [--identity mint|&lt;32 hex&gt;] [--force]`
    ///
    /// `--root` names a kind seeded in by `--kinds`, or else a plain name that becomes a kind
    /// of the new source's own, with a text payload: a source needs no other library's kind
    /// to start from. The schemas of the kinds it uses are embedded, so it states what its own
    /// nodes are.
    /// </summary>
    public static int New(string[] args)
    {
        if (args.Length < 2) return NewUsage();

        string path = args[1];
        string root = null;
        string label = null;
        string kinds = null;
        string identity = "mint";
        bool force = false;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when root == null && i + 1 < args.Length: root = args[++i]; break;
                case "--label" when label == null && i + 1 < args.Length: label = args[++i]; break;
                case "--kinds" when kinds == null && i + 1 < args.Length: kinds = args[++i]; break;
                case "--identity" when i + 1 < args.Length: identity = args[++i]; break;
                case "--force" when !force: force = true; break;
                default: return NewUsage();
            }
        }

        if (root == null) return NewUsage();

        SourceFile seed = default;
        SourceFile file = default;
        try
        {
            if (File.Exists(path) && !force)
            {
                return Refuse("new", path + " exists; pass --force to replace it");
            }

            file = SourceFile.Create();

            if (kinds != null)
            {
                if (!File.Exists(kinds))
                {
                    return Reject("new", "no kind source at " + kinds);
                }

                seed = SourceRead.FromFile(kinds);
                int brought = Seed(ref file.Table, in seed.Table);
                Console.WriteLine("kinds      " + brought + " seeded from " + kinds);
            }

            file.Src.Identity = Identity(identity, label);
            KindId rootKind = Seeded(in file.Table, root);
            if (rootKind.IsUnknown)
            {
                if (root.Length == 0 || root.Contains('/'))
                {
                    return Reject("new", "no seeded kind is " + root + ", and a kind of this source's own needs a plain name");
                }
                rootKind = KindTable.Intern(ref file.Table, new QualifiedName(file.Src.Identity, root));
                KindTable.DefineSchema(ref file.Table, rootKind, StructuralFact.None, PayloadFormat.Text, default);
            }
            KindSchema schema = KindTable.SchemaOf(in file.Table, rootKind);

            if (label != null && schema.Payload != PayloadFormat.Text)
            {
                return Reject("new",
                    "--label needs a root whose payload is text; " + root + " is " + schema.Payload);
            }

            NodeId at = SourceBuild.AddRoot(ref file.Src, rootKind);
            if (label != null)
            {
                SourceBuild.SetPayload(ref file.Src, at, PayloadCodec.WriteUtf8(label));
            }

            SourceWrite.ToFile(in file, path);

            Console.WriteLine("source     " + file.Src.Identity);
            Console.WriteLine("root       n" + at.Index + "  " + root);
            Console.WriteLine("wrote      " + path);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException || error is FormatException
            || error is InvalidOperationException || error is FormatCorruptException)
        {
            return Reject("new", error.Message);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("new I/O error: " + error.Message);
            return 2;
        }
        finally
        {
            SourceFile.Close(ref seed);
            SourceFile.Close(ref file);
        }
    }

    static int NewUsage()
    {
        Console.Error.WriteLine(
            "usage: new <out.ss> --root <kind> [--label <text>] [--kinds <source.ss>] "
            + "[--identity mint|<32 hex>] [--force]");
        return 1;
    }

    /// <summary>
    /// `mint` for an identity of its own (§5.1), or 32 hex digits for a source whose consumers
    /// pin it.
    /// </summary>
    static SourceId Identity(string requested, string label)
    {
        if (requested == "mint") return SourceId.Mint();

        if (requested.Length != 32)
        {
            throw new ArgumentException("--identity must be \"mint\" or 32 hex digits");
        }

        ulong hi = 0;
        ulong lo = 0;
        for (int i = 0; i < 32; i++)
        {
            if (!Uri.IsHexDigit(requested[i]))
            {
                throw new ArgumentException("--identity must be 32 hex digits");
            }

            ulong digit = (ulong)Convert.ToInt32(requested[i].ToString(), 16);
            if (i < 16) hi = (hi << 4) | digit;
            else lo = (lo << 4) | digit;
        }

        if (hi == 0 && lo == 0) throw new ArgumentException("--identity cannot be all zeroes");
        return new SourceId(hi, lo);
    }

    /// <summary>
    /// Copy every named kind and relation that carries a schema out of one table and into
    /// another, keeping its qualified name: a seeded kind stays the same kind, declared by the
    /// same library.
    ///
    /// Reads the seed's embedded schemas, which is what `SourceRead` populates a table from. A
    /// seed whose schemas are not embedded brings nothing.
    /// </summary>
    internal static int Seed(ref KindTable into, in KindTable from)
    {
        var relations = new Dictionary<int, RelationId>();

        for (int i = 0; i < from.RelationCount; i++)
        {
            var was = new RelationId(i);
            QualifiedName name = KindTable.RelationNameOf(in from, was);
            if (name.IsNone) continue;

            RelationId now = KindTable.InternRelation(ref into, name);
            RelationSchema schema = KindTable.RelationSchemaOf(in from, was);
            KindTable.DefineRelation(ref into, now, schema.Payload);
            relations[i] = now;
        }

        int brought = 0;
        var uses = new List<RelationId>();

        for (int i = 0; i < from.KindCount; i++)
        {
            var was = new KindId(i);
            QualifiedName name = KindTable.NameOf(in from, was);
            if (name.IsNone || !KindTable.HasSchema(in from, was)) continue;

            KindSchema schema = KindTable.SchemaOf(in from, was);

            uses.Clear();
            for (int r = 0; r < schema.RelationCount; r++)
            {
                RelationId used = from.RelationArena[schema.RelationOffset + r];
                if (relations.TryGetValue(used.Value, out RelationId now)) uses.Add(now);
            }

            KindId kind = KindTable.Intern(ref into, name);
            KindTable.DefineSchema(
                ref into, kind, schema.Facts, schema.Payload,
                uses.ToArray());
            brought++;
        }

        return brought;
    }

    // ---- declare ----------------------------------------------------------------------------

    /// <summary>
    /// `declare &lt;library.ss&gt; &lt;declaration.json&gt; [--dry-run]`
    ///
    /// Adds kinds and relations to the source, qualified by its own identity. Every field maps
    /// onto <see cref="KindTable.DefineSchema"/> and <see cref="KindTable.DefineRelation"/>: the
    /// facts and payload formats are those enumerations, spelled in kebab-case.
    /// </summary>
    public static int Declare(string[] args)
    {
        if (args.Length < 3) return DeclareUsage();

        bool dryRun = false;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--dry-run" && !dryRun) dryRun = true;
            else return DeclareUsage();
        }

        string path = args[1];
        SourceFile file = default;
        try
        {
            if (!File.Exists(path))
            {
                return Reject("declare", "no source at " + path + "; make one with `new`");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(args[2]));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "a declaration is an object with \"kinds\" and optionally \"relations\"");
            }

            Fields(root, "relations", "kinds");
            file = SourceRead.FromFile(path);

            // The library's own identity qualifies every name it declares.
            SourceId library = file.Src.Identity;
            if (library.IsNone)
            {
                return Reject("declare",
                    "this source has no identity, so it cannot qualify a kind name");
            }

            int relations = 0;
            if (root.TryGetProperty("relations", out JsonElement declared))
            {
                relations = Relations(ref file.Table, declared, library);
            }

            // A kind library's own kinds are written with it, so it needs no node to carry them.
            List<KindId> kinds = Kinds(ref file.Table, root, library);

            Console.WriteLine("library    " + library);
            Console.WriteLine("declared   " + kinds.Count + " kind(s), " + relations + " relation(s)");

            if (dryRun)
            {
                Console.WriteLine("dry run    nothing written");
                return 0;
            }

            if (!SourceWrite.Replace(ref file, path, out string refusal))
            {
                return Refuse("declare", refusal);
            }

            Console.WriteLine("wrote      " + path);
            return 0;
        }
        catch (Exception error) when (error is JsonException || error is ArgumentException
            || error is FormatException || error is InvalidOperationException
            || error is FormatCorruptException)
        {
            return Reject("declare", error.Message);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("declare I/O error: " + error.Message);
            return 2;
        }
        finally
        {
            SourceFile.Close(ref file);
        }
    }

    static int DeclareUsage()
    {
        Console.Error.WriteLine("usage: declare <library.ss> <declaration.json> [--dry-run]");
        return 1;
    }

    static int Relations(ref KindTable table, JsonElement declared, SourceId library)
    {
        if (declared.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("relations must be an array");
        }

        int count = 0;
        foreach (JsonElement each in declared.EnumerateArray())
        {
            if (each.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("each relation must be an object");
            }

            Fields(each, "name", "payload");
            string name = Text(each, "name");
            if (name.Length == 0) throw new ArgumentException("a relation needs a name");

            var qualified = new QualifiedName(library, name);
            if (!KindTable.LookupRelation(in table, qualified).Equals(RelationId.Unknown))
            {
                throw new ArgumentException("this library already declares the relation " + name);
            }

            RelationId id = KindTable.InternRelation(ref table, qualified);
            PayloadFormat payload = Payload(each);
            KindTable.DefineRelation(ref table, id, payload);
            count++;
        }

        return count;
    }

    static List<KindId> Kinds(ref KindTable table, JsonElement root, SourceId library)
    {
        if (!root.TryGetProperty("kinds", out JsonElement declared)
            || declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() == 0)
        {
            throw new ArgumentException("kinds must be a nonempty array");
        }

        var made = new List<KindId>();
        var uses = new List<RelationId>();

        foreach (JsonElement each in declared.EnumerateArray())
        {
            if (each.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("each kind must be an object");
            }

            Fields(each, "name", "facts", "payload", "uses");
            string name = Text(each, "name");
            if (name.Length == 0) throw new ArgumentException("a kind needs a name");

            var qualified = new QualifiedName(library, name);
            KindId existing = KindTable.Lookup(in table, qualified);
            if (!existing.Equals(KindId.Unknown) && KindTable.HasSchema(in table, existing))
            {
                // Redefining a kind reinterprets the bytes of every node already carrying it.
                throw new ArgumentException(
                    "this library already declares " + name
                    + "; changing a kind in use is not something this verb does");
            }

            uses.Clear();
            if (each.TryGetProperty("uses", out JsonElement used))
            {
                if (used.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("uses must be an array of relation names");
                }

                foreach (JsonElement one in used.EnumerateArray())
                {
                    if (one.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException("each entry of uses must be a string");
                    }

                    string relation = one.GetString();
                    RelationId id = KindTable.LookupRelation(
                        in table, new QualifiedName(library, relation));
                    if (id.Equals(RelationId.Unknown)) id = Relation(in table, relation);
                    uses.Add(id);
                }
            }

            PayloadFormat payload = Payload(each);
            KindId kind = KindTable.Intern(ref table, qualified);
            KindTable.DefineSchema(
                ref table, kind, Facts(each), payload,
                uses.ToArray());
            made.Add(kind);
        }

        return made;
    }

    // ---- the enumerations, spelled ----------------------------------------------------------

    static StructuralFact Facts(JsonElement each)
    {
        if (!each.TryGetProperty("facts", out JsonElement declared))
        {
            return StructuralFact.None;
        }

        if (declared.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("facts must be an array");
        }

        StructuralFact facts = StructuralFact.None;
        foreach (JsonElement one in declared.EnumerateArray())
        {
            if (one.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("each fact must be a string");
            }

            switch (one.GetString())
            {
                case "interior-opaque": facts |= StructuralFact.InteriorOpaque; break;
                case "child-order-significant": facts |= StructuralFact.ChildOrderSignificant; break;
                case "annotation": facts |= StructuralFact.Annotation; break;
                default:
                    throw new ArgumentException(
                        "unknown fact: " + one.GetString()
                        + "; one of interior-opaque, child-order-significant, annotation");
            }
        }

        return facts;
    }

    /// <summary>
    /// A payload format, spelled `"none"`, `"opaque"`, `"text"`, `{"text": header}`,
    /// `{"one": element}` or `{"many": element, "header": element}`. An element is a scalar name (`"f32"`), or
    /// `{"size": n}` for one a reader can count but not look inside, or `{"size": n, "fields":
    /// [{"name": …, "type": "f32", "offset": 0, "repeat": 3}, …]}`.
    /// </summary>
    static PayloadFormat Payload(JsonElement each)
    {
        if (!each.TryGetProperty("payload", out JsonElement declared)) return PayloadFormat.None;

        if (declared.ValueKind == JsonValueKind.String)
        {
            switch (declared.GetString())
            {
                case "none": return PayloadFormat.None;
                case "opaque": return PayloadFormat.Opaque;
                case "text": return PayloadFormat.Text;
                default:
                    throw new ArgumentException(
                        "unknown payload format: " + declared.GetString()
                        + "; one of none, opaque, text, {\"text\": header}, {\"one\": …} or {\"many\": …}");
            }
        }

        if (declared.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("a payload is a string or an object with one or many");
        }

        if (declared.TryGetProperty("text", out JsonElement after))
        {
            Fields(declared, "text");
            return PayloadFormat.TextAfter(Element(after));
        }

        if (declared.TryGetProperty("one", out JsonElement one))
        {
            Fields(declared, "one");
            return PayloadFormat.One(Element(one));
        }

        if (declared.TryGetProperty("many", out JsonElement many))
        {
            Fields(declared, "many", "header");
            PayloadElement header = declared.TryGetProperty("header", out JsonElement before)
                ? Element(before)
                : default;
            return PayloadFormat.Many(Element(many), header);
        }

        throw new ArgumentException("a payload object needs text, one or many");
    }

    static PayloadElement Element(JsonElement declared)
    {
        if (declared.ValueKind == JsonValueKind.String)
        {
            return PayloadElement.Of(Scalar(declared.GetString()));
        }

        if (declared.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("an element is a scalar name or an object with a size");
        }

        Fields(declared, "size", "fields");
        ushort size = Span(declared, "size");
        if (!declared.TryGetProperty("fields", out JsonElement fields))
        {
            return PayloadElement.Sized(size);
        }

        if (fields.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("fields must be an array");
        }

        var made = new List<PayloadField>();
        foreach (JsonElement field in fields.EnumerateArray())
        {
            Fields(field, "name", "type", "offset", "repeat");
            PayloadField one = PayloadField.Of(
                Text(field, "name"),
                Scalar(Text(field, "type")),
                Span(field, "offset"),
                field.TryGetProperty("repeat", out _) ? Span(field, "repeat") : (ushort)1);
            if (one.Offset + one.Size > size)
            {
                throw new ArgumentException(
                    "field " + one.Name + " ends at byte " + (one.Offset + one.Size)
                    + ", past the element's " + size);
            }
            made.Add(one);
        }

        return PayloadElement.Of(size, made.ToArray());
    }

    static PayloadScalar Scalar(string name)
    {
        if (!PayloadScalars.TryParse(name, out PayloadScalar scalar))
        {
            throw new ArgumentException(
                "unknown scalar " + name + "; one of i8, i16, i32, i64, u8, u16, u32, u64, f16, f32, f64, bool");
        }
        return scalar;
    }

    /// <summary>A kind the table already has under that name, or unknown when it has none.</summary>
    static KindId Seeded(in KindTable table, string requested)
    {
        for (int i = 0; i < table.KindCount; i++)
        {
            QualifiedName candidate = KindTable.NameOf(in table, new KindId(i));
            if (!candidate.IsNone && (candidate.Name == requested || candidate.ToString() == requested))
            {
                return Named(in table, requested);
            }
        }
        return KindId.Unknown;
    }

    static KindId Named(in KindTable table, string requested)
    {
        int found = -1;
        for (int i = 0; i < table.KindCount; i++)
        {
            QualifiedName candidate = KindTable.NameOf(in table, new KindId(i));
            if (candidate.IsNone) continue;
            if (candidate.Name != requested && candidate.ToString() != requested) continue;
            if (found >= 0)
            {
                throw new ArgumentException("ambiguous kind; use library-id/name: " + requested);
            }

            found = i;
        }

        if (found < 0 || !KindTable.HasSchema(in table, new KindId(found)))
        {
            throw new ArgumentException(
                "no kind named " + requested + " carries a schema here; seed one with --kinds");
        }

        return new KindId(found);
    }

    static RelationId Relation(in KindTable table, string requested)
    {
        int found = -1;
        for (int i = 0; i < table.RelationCount; i++)
        {
            QualifiedName candidate = KindTable.RelationNameOf(in table, new RelationId(i));
            if (candidate.IsNone) continue;
            if (candidate.Name != requested && candidate.ToString() != requested) continue;
            if (found >= 0)
            {
                throw new ArgumentException("ambiguous relation; use library-id/name: " + requested);
            }

            found = i;
        }

        if (found < 0)
        {
            throw new ArgumentException(
                "no relation named " + requested + "; declare it in this file's relations first");
        }

        return new RelationId(found);
    }

    static int Reject(string verb, string why)
    {
        Console.Error.WriteLine(verb + " rejected: " + why);
        return 1;
    }

    static int Refuse(string verb, string why)
    {
        Console.Error.WriteLine(verb + " refused: " + why);
        return 3;
    }

    static string Text(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(field + " must be a string");
        }

        return value.GetString();
    }

    /// <summary>A size, offset or repeat, bounded to the sixteen bits a payload element holds it in.</summary>
    static ushort Span(JsonElement item, string field)
    {
        int value = Whole(item, field);
        if (value > ushort.MaxValue)
        {
            throw new ArgumentException(field + " must fit in 16 bits");
        }

        return (ushort)value;
    }

    static int Whole(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out JsonElement value)
            || !value.TryGetInt32(out int number) || number < 0)
        {
            throw new ArgumentException(field + " must be a nonnegative 32-bit integer");
        }

        return number;
    }

    /// <summary>
    /// Every field an object may carry. Anything else is rejected rather than ignored, as in
    /// `edit`: a misspelled field would otherwise declare a schema missing a fact.
    /// </summary>
    static void Fields(JsonElement item, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new ArgumentException("duplicate field: " + property.Name);
            }

            bool found = false;
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == property.Name) { found = true; break; }
            }

            if (!found) throw new ArgumentException("unknown field: " + property.Name);
        }
    }
}
