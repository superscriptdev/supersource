# Supersource reference

Supersource is a file format for structured data and the relationships within it. It gives
applications a common model for storing content, identifying its parts and linking them, so
that tools can inspect and edit the stored structure without a separate parser for each
content type.

A source is a graph of nodes. Each node has a kind, payload bytes, ordered children and typed
references to other nodes. Applications define what those nodes represent and how much
structure to expose. A source could hold a document, a dataset, a scene, a program, or a project
combining several kinds of content. Applications can keep related content in one source or
link separate sources.

Nodes keep their identity for the life of the source. Changing a node's name, payload or
position leaves references to it intact. An editor could use this to keep comments attached to
paragraphs as their text changes; a modelling tool could record which mesh a generated part
came from. These relationships are stored explicitly and can be followed by other tools.

Embedded schemas describe node kinds and their payload formats. A tool can use them to walk,
search, validate, diff and merge unfamiliar content. Domain-specific behaviour still belongs
to the application: knowing that a node contains a record does not tell a reader how to render
a mesh or execute a function.

This repository provides the format implementation and a command-line tool. It uses .NET 10
with no external dependencies or submodules. It supports stream-based I/O, memory-mapped
payloads, incremental saves when edits can be appended, and stored history with structural
diff and three-way merge.

## Getting started

```bash
dotnet build Supersource.slnx
dotnet test  Supersource.slnx
```

Run the CLI project directly with `dotnet run`. It builds the project before invoking the
command. Arguments after `--` are passed to the CLI.

```bash
dotnet run --project src/Supersource.Cli -c Release -- cat tests/fixtures/vec.ss
```

```
source 00000000000000030000000000000003
39 slots, 39 live

module  "math"   n0
  type  "f32"   n1
  intrinsic  "add"   n2
  module  "vec"   n3
    function  "add"   n4
      parameter  "a"   n5
        --has-type--> n1
      parameter  "b"   n6
        --has-type--> n1
      result   n7
        --has-type--> n1
      body   n8
        return   n9
          call   n10
            --calls--> n2
```

The CLI uses the embedded schemas to display this structure. It does not implement the
language that defines these functions.

```bash
dotnet run --project src/Supersource.Cli -c Release -- stat tests/fixtures/vec.ss
```

```
nodes            39 live, 0 deleted
kinds            16 named
relations        6 named

uninterpretable  0  0.0%  no embedded schema
opaque interior  0  0.0%  declared
opaque payload   0  0.0%  declared

payload bytes    728, 0 opaque (0%)
unread sections  0
```

The fixtures exercise reading content whose kind libraries are unavailable to this repository.
See [Fixture provenance](#fixture-provenance) for their origin.

## Terms

**node** — an addressable element of a source. The application chooses its meaning and size.

**source** — a graph of nodes with one identity. The unit opened, versioned and shared.

**file** — the bytes a source is stored as. One source per file.

**kind** — a node's declared category, such as `paragraph`, `mesh` or `function`. Its schema
exposes structure to generic tools; its domain meaning is defined by the kind's author.

**relation** — the meaning of a reference, such as `about`, `calls` or `has-type`.

**payload** — a node's content bytes. Its kind declares their format.

**typed reference** — a reference labelled with a relation. Here, “typed” describes the edge;
it does not imply a programming-language type system.

## The model

A node's payload holds its own content. Children describe containment and order; references
record relationships that can cross the containment tree.

For example, an application could represent an illustrated document like this:

```text
document
  paragraph n1  "The west entrance"
  image     n2  <image bytes>
  comment   n3  "Check this description"
    --about--> n1
```

The paragraph, image and comment are children of the document. The `about` reference records
which node the comment concerns. Keeping `n1` while changing its text preserves that link.
The application decides whether paragraphs, sentences or another unit should have identities.

### Containment and references

Containment is a tree: a contained node has one parent, and children are ordered. The root has
no parent. A child index that names no slot is structural corruption.

References form a graph over that tree. A reference carries a relation and may have a target
or be unbound. An unbound reference is a hole, which can retain the name it was looking for.
Holes are allowed in the storage model, for example while an application is editing a source.
A bound reference can also point to a deleted node; its target identity is retained.

A reference can carry data describing which part of a node it addresses. For example, an
application could store an image as one node and refer to a pixel within its payload. The
relation declares the format of the reference data. If the kind also declares a
`PayloadStride`, an element index can be translated to a byte range without understanding the
element's domain meaning.

`Backrefs` indexes references by target to answer which nodes point at a given node. It is
derived from the stored edges and can be rebuilt from them.

### Identity

A `NodeId` identifies a slot using a 64-bit value biased by one so that `default` means none.
Slots are never reused for different nodes. Deleting a node clears its `Live` flag and retains
its slot, allowing references to distinguish a deleted target from an unbound reference.

A source has a `SourceId`, assigned when it is created. A reference to another source names
that identity and a node within it. It can also carry a content hash to select a specific
version. Source and node identity survive moving, renaming or copying the file. A copied file
therefore represents the same source unless an application explicitly gives it a new identity.

## Kinds

A kind has a qualified name and a schema. The name combines the defining library's `SourceId`
with a local string, so two libraries can both define `table` without collision. The schema
describes the kind using Supersource's shared vocabulary.

| `StructuralFact` | what it tells a reader |
|---|---|
| `Declaration` | nodes of this kind are something a name can resolve to |
| `OpensScope` | this node opens a scope |
| `InteriorOpaque` | the interior is unstructured, and that is declared rather than assumed |
| `ChildOrderSignificant` | sibling order carries meaning, so diff and merge must not reorder freely |

| `PayloadFormat` | payload bytes are |
|---|---|
| `None` | absent |
| `Opaque` | undescribed |
| `Utf8` | text |
| `Int64` · `Float64` · `Bool` | one value |
| `Record` | a structure the kind's own specification describes |

`DisplayShape` is `Block`, `Inline` or `Leaf`, providing a basic layout hint for a generic
editor. Domain-specific rendering requires knowledge of the kind. `PayloadStride` gives a
`Record` an optional array shape, a header size and an element size, so a reader can address
element *n* of a payload it cannot otherwise interpret.

Kinds can be defined by applications, while the schema vocabulary is defined by Supersource.
Readers implement that shared vocabulary to inspect kinds they have not encountered before.

### Embedded schemas

Schemas are stored as nodes in the graph using bootstrap kinds including `schema-set`,
`kind-schema` and `role-decl`. Sources should embed schemas for all the kinds they use;
missing schemas can be reported without preventing the graph from being read.

Schemas describe the structure available to generic tools. Opaque payloads can be preserved
without being understood; records can expose an array stride without describing every field
to the reader. The `every-kind-is-interpretable` rule reports kinds without embedded schemas.

## Resolution

The resolution APIs use stored structure and schema facts to find targets for unbound
references. Applications decide when to invoke them. Reading a file does not require every
reference to resolve.

* **`Scope`** — declarations visible at a node, innermost first, using the schema facts
  `Declaration` and `OpensScope`.
* **`Binder`** — candidates for a name, and binding a hole to one of them.
* **`Linker`** — turns unbound references into bound ones, or reports why not.
* **`RelationCheck`** — findings about edges whose relation the target's kind does not declare.
* **`Workspace`** — several loaded sources, and the references between them.

`Workspace.Resolve` looks for the target source and node among registered sources. A content
hash, when present, selects the required version. Unpinned references can resolve, and the
result reports that they are unpinned. Missing sources, missing versions and ambiguous matches
are explicit outcomes. Fetching another source is an application responsibility.

Workspace registration accepts the caller's snapshot identity and hash without verifying
them against the loaded bytes. The loader is responsible for verifying that association.

## Rules

`SourceValidate` checks structural integrity, including node indices and containment. Source
rules check additional requirements such as complete references and conformance to declared
payload schemas. A structurally valid source can fail these rules.

The stock set: `no-dangling-references`, `no-unfilled-holes`,
`every-declaration-has-a-text-label`, `every-kind-is-interpretable`, `payload-matches-the-schema`,
`edge-data-matches-the-relation`, `schema-declares-only-what-is-used`.

`DerivationRules` in the optional companion library supplies `FullyReDerivable` and
`GeneratorsAreBitExact`. Each appends shared `RuleViolation` diagnostics and takes a caller-chosen
severity. These checks are not part of the core rule set or the CLI's default `rules` command.

`RelationCheck.Check` reports undeclared relations without treating any vocabulary specially.
Callers can pass `permittedRelations` to allow explicitly chosen extension relations alongside
each kind's declared relations. The default permits no additional relations.

Rules can be checked independently of the application that wrote the file. The caller chooses
which rules to enforce and how to present their severity or act on violations.

## History, diff and merge

### The timeline

An edit is an `Operation`: `Insert`, `Delete`, `Move`, `Rename`, `ModifyValue`, `Wrap`,
`Unwrap`, `Reorder`, `Connect`, `Disconnect`, `Splice`. Each names nodes by durable id and
carries enough to invert itself, so undo is reading the log backwards.

The `History` section stores applied operations; it does not persist the undone redo suffix.
Caret positions, gesture grouping and timeline filtering belong to the application. All applied
operations are retained, including operations on generated nodes. Generic readers can inspect
and preserve generated content without running a generator.

`SourceBuild` also permits direct construction and mutation without history. `Log.Do` records
an operation after applying it; it does not provide batch rollback or transaction isolation.

### Diff

`StructuralDiff` takes two states of one source and returns operations. There is no matching
step: node n12 in one is node n12 in the other, because slots never move and are never reused.
The two states must share a `SourceId`, otherwise the slots are unrelated numbers.

Diff operations are ordered so newly inserted nodes exist before other operations reference
them. Moves precede deletion, and sibling order is settled after membership changes. Tests
apply generated diffs to their starting state and compare the result with the target state.

To transform state A into state B, the differ compares node structure and schema facts:

* only in B → `Insert`; only in A → `Delete`
* different parent → `Move`
* different child order → `Reorder`, and a semantic change only if the kind says
  `ChildOrderSignificant`
* different references → `Connect` / `Disconnect`

Payload comparison uses the schema's declared format. `PayloadDiff` trims the common prefix
and suffix and describes the remaining range as a splice. Text ranges align to code-point
boundaries, and declared arrays align to whole elements. A small change can therefore be
represented without replacing an entire payload. Whole-payload replacement remains the
fallback. Kind-specific differ registration is not implemented.

### Merge

Three-way merge compares a common base with “ours” and “theirs”. It reconciles changes by node
and by aspect: payload, containment, child order, references and existence.

When changes conflict, the current policy selects ours and writes a conflict record into the
merged source. A caller can inspect those records and decide how to present or resolve them.

For text and declared arrays, merge can combine changes whose detected ranges do not overlap.
Each side's changes are represented by one range, so several separate edits can produce a range
that also covers unchanged content. Opaque payloads and records without an array stride do not
qualify for this range-based merge. A structural merge also does not establish that the result
is valid according to an application's domain rules.

Child-order comparison handles reordering separately from changes to membership caused by
insertion, movement or deletion.

Both branches can allocate the same slot for different new nodes. For example, their n50 and
our n50 may be unrelated. The merge remaps their new nodes and the references to them. Kinds
and relations are translated by name because each branch has its own table numbering.

## Editing a source

The CLI can edit an existing source using a JSON batch. Each operation is recorded through
`Log.Do`, preserving its node IDs and the inverse data used by the operation history.

```sh
dotnet run --project src/Supersource.Cli -c Release -- edit source.ss edits.json --dry-run
dotnet run --project src/Supersource.Cli -c Release -- edit source.ss edits.json --expect-hash <sha256-from-preview>
```

The edits file is a nonempty JSON array. Operations run in order against the result of earlier
operations in the batch. For example, if `paragraph` is an existing UTF-8 kind and `n0` is a
suitable parent:

```json
[
  {"op":"insert","parent":"n0","kind":"paragraph","text":"First draft","as":"draft"},
  {"op":"set-payload","node":"@draft","text":"Revised draft"}
]
```

`as` gives an inserted node a batch-local handle. Later operations can use `@draft` wherever a
node ID is accepted. The command reports its allocated numeric ID. Handles must be nonempty
and unique within the batch; references to later inserts are not supported.

### Patch fields

All operations require `op`. Node IDs are JSON strings such as `"n4"` or `"4"`. Unknown or
duplicate fields and unsupported CLI options are rejected.

| `op` | required fields | optional fields |
|---|---|---|
| `insert` | `parent`, `kind` | `index`, `as`, `text` or `base64` |
| `set-payload` | `node`, `text` or `base64` | — |
| `rename` | `node`, `text` or `base64` | — |
| `splice` | `node`, `offset`, `remove`, `text` or `base64` | — |
| `move` | `node`, `parent` | `index` |
| `delete` | `node` | — |
| `connect` | `node`, `relation`, `target` | `index`, `text` or `base64` |
| `disconnect` | `node`, `index` | — |

`text` is encoded as UTF-8; for node payloads it requires a kind declared as UTF-8. `base64`
provides exact bytes for other formats. Supply one representation, not both. An empty string
clears the payload; omitted payloads on insertion are empty. `rename` and `set-payload` both
replace the entire payload but record different operation kinds. They do not interpret a
language's naming rules.

Kinds and relations can be specified by an unambiguous local name or by `library-id/name`, as
shown by `node` and `kinds`. Insertions use existing kind schemas. The command does not define
new schemas or content kinds.

Indexes are zero-based. Insert and connect default to appending. A move's index addresses the
destination after removal of the moved node; omission moves it to the end. `disconnect` uses
the outgoing reference index printed in brackets by `node`. A connection adds an edge; use a
disconnect followed by a connect to replace an edge. Preserve its edge data when retargeting it.
A `null` target creates an unbound reference whose optional text is a label.

Splice `offset` and `remove` count bytes. Ranges must fit the current payload. For UTF-8,
boundaries must not split a code point. Format checks also validate the resulting payload.
The command does not adjust annotation byte ranges after an edit.

### Validation and saving

The CLI checks source structure before editing, applies the batch in memory, then checks the
result's structure and the payload/edge formats on touched nodes. Incomplete references are
allowed. Domain-specific validity still requires application checks.

An invalid batch is discarded without saving. `--dry-run` follows the same preparation and
validation path, prints the input file's SHA-256 and operation summary, and leaves the file
unchanged. `--expect-hash` compares that whole-file hash with the current input before applying
the patch. The CLI also checks for changes during preparation before calling
`SourceWrite.Replace` to save in place. These checks detect stale inputs but do not provide
isolation from concurrent writers or guarantee recovery from interruption during a save.

Each batch operation produces its own history entry. Edits preserve source identity; deleted
slots remain allocated and incoming references can become dangling. The CLI rejects deletion
or movement of the root and mutation of embedded schema definitions, including deletion of a
subtree containing them. Generated nodes can be edited; their flags and logged edits are
preserved. Use the application for schema changes, regeneration and any required updates to
derived content. The CLI does not infer these application behaviors.

Exit 1 means the patch or its result was rejected. Exit 3 means a hash mismatch or save refusal.
Exit 2 covers corrupt inputs and I/O errors; a save-time I/O error requires inspecting the file
before retrying. A pre-existing `<file>.writing` sibling is preserved and causes a refusal.

## Shared vocabularies

The optional `Supersource.Vocabularies` project supplies reusable conventions across content
types. It references the core; the core and CLI do not reference it. Existing namespaces and
encoded vocabulary identities are retained. Applications reference the companion project and
explicitly install the vocabularies they use. Domain kinds such as paragraphs, images and
functions are defined outside this repository.

Embedded schemas describe payloads, containment roles, relations and optional display hints.
These facts remain in the file, so an unfamiliar reader can inspect and preserve the graph
without linking its vocabulary library. Shared vocabularies supply no default palette;
applications may choose their own presentation.

**`PropertyKinds`** supplies valued tags: a property references a tag node and stores a primitive
value with a payload-format prefix. The consuming application decides what each tag means and
whether it affects presentation, execution or another behavior. Annotation metadata describes
the node's role; it does not promise that deleting it is semantically harmless.

**`Relate`** supplies the relations `about`, `tagged`, `refers-to`, `embeds` and `imported-from`,
and the kinds `span`, `tag`, `embed` and `original`. Shared relations let applications connect
content defined by independent kind libraries.

A `span` can describe byte ranges and associate them with tags. For example, an editor could
store a paragraph's text in its payload and represent bold formatting with a tagged span.
The application defines the tag's meaning and rendering. Node identity keeps a reference
attached to the paragraph; maintaining byte ranges as its text changes is a separate concern.

**`Derive`** holds provenance and staleness: the kinds `derivation` and `build-target`, and the
relations `input`, `transform`, `produced-by`, `entry-point`, `generated-from`. A derivation is
a node whose content is computed from other nodes by a named transform. Applications can use
this to describe transpilation, build steps or other data processing.

The library exposes provenance and staleness information. Applications supply and run the
transforms. `Tier` records the claimed reproducibility level: `BitExact`, `ContentAddressed` or
`Captured`. Rules can check the applicable reproducibility claims.

### Generated nodes

The core preserves `Derived` and `Generated` flags as metadata. The companion's generation
helpers implement an explicit lifecycle convention; applications choose whether to use them
and when to regenerate or discard outputs. The flags alone do not suppress persistence or
prevent CLI edits.

The generation support uses `generated-from` references to associate outputs with their input
nodes across regeneration. Matching an existing output by its input can preserve the output's
node ID when other inputs are inserted or removed. Matching outputs only by their positions
would not preserve that correspondence.

The input identity is a correspondence key; the output's `NodeId` is allocated from the source's
ordinary counter. Preserving IDs across regeneration depends on the generator using this
correspondence. Independently regenerated sources need not allocate identical slots.

## Scope

The format and generic reading tools are open. Applications and kind libraries built on them
may be open or closed. The embedded schema makes the declared structure accessible without
requiring the application that authored it; it does not supply all domain semantics.

This repository does not provide:

* a programming language, standard library, evaluator or emitters;
* domain kind libraries for prose, tables, style, raster images or geometry;
* importers and exporters for Markdown, JSON, CSV or images;
* editors, views or domain-specific rendering;
* an application runtime or its capability interface.

These are consumers of Supersource. The format library remains independent of them.

## In-memory storage

```csharp
struct Node {                        // 48 bytes, asserted
    NodeId  Parent;                  // containing node, or none
    long    PayloadOffset;           // which blob segment, and where in it
    KindId  Kind;                    // index into the kind table
    int ChildOffset, ChildCount;     // containment, ordered
    int RefOffset,   RefCount;       // typed references out
    int PayloadLength;
    NodeFlags Flags;                 // Live · Derived · Generated
}
```

`Node` uses explicit sequential layout. Tests assert its 48-byte size and field offsets.
Run capacities and per-node revisions are stored separately, keeping that metadata out of
node scans.

All node kinds use the same storage layout. Payload bytes are stored separately, so the
storage code can preserve a node without understanding its content.

`Source` is `Node[]` plus four arenas the runs point into: `Children`, `Refs`, `Blob` and
`Labels`. Growing a run past its capacity moves it to the end of its arena and abandons the old
span, so an arena carries a high-water mark rather than a live count. The current
implementation reclaims abandoned arena space with `SourceCompact`, which rebuilds storage
while preserving node IDs and retained deleted slots. Stable identity requires that IDs are
not reassigned to different nodes; it does not itself require this storage representation.

The blob is segmented so total payload storage can exceed the size of a single `byte[]`.
Payloads are addressed by a 64-bit value naming the segment and offset within it. Each payload
fits in one segment and can be read as a span. A payload larger than the ordinary segment size
gets its own segment; individual payload lengths still use 32-bit integers.

A `Source` is a struct containing references to backing arrays. Assigning it to another
variable shares those arrays; it does not create an independent editable snapshot.

## File format

```
magic "SSSRC\r\n\x1a"  ·  version (u16 major, u16 minor)  ·  section count (u32)
sections, each:  id (u32) · length (u64) · bytes
```

| section | holds |
|---|---|
| `Identity` | the source's `SourceId` and its root |
| `KindNames` | durable kind names, in the order the graph first uses them |
| `RelationNames` | durable relation names, same |
| `Nodes` | every node: kind, parent, payload, children, references |
| `History` | the operations that made this source what it is |
| `Checksum` | a digest of the other sections' content |
| `Journal` | operations appended since the previous saved state |

A reader uses section lengths to skip unknown sections and preserves their bytes when writing
the file back. This lets older readers retain content introduced by newer compatible versions.

Section headers are fixed-width so readers can locate sections without interpreting their
contents. Inside a section, indices and counts use variable-width encodings and relative
addressing to reduce framing overhead.

### Streaming I/O

The reader and writer process sections through buffered streams, avoiding a single byte array
for the entire file. Buffers grow when an individual read requires it. This bounds I/O buffering
by individual reads rather than whole sections; it does not remove the memory needed for node
metadata or owned payloads. Reading from an array uses the same code over a `MemoryStream`.

Both need a stream that can seek. Sections may be stored in any order and are read in dependency
order, names before the nodes that use them, and a section's length is written before its
content, so eight bytes are left blank and filled in once the extent is known.

### Memory-mapped payloads

`SourceRead.FromFile` maps the file and registers it as a blob segment. A node's payload becomes
a place in the file rather than bytes moved out of it, so loading costs the framing and the
content stays where it is until something reads it. A mapped payload has zero capacity, so the
first edit relocates it into a segment the source owns; the file is never written through.

The mapping must remain open while payloads point into it. Call `SourceFile.Close` when finished
with the loaded source. To keep a source after closing its mapping, `SourceCompact.Compact`
copies payloads into memory owned by the compacted source.

### Incremental saves

`SourceWrite.Replace` can append logged edits as a `Journal` section instead of rewriting the
whole source. Each append records operations since the previous save and updates the section
count in the file header. Existing sections remain in place. On read, `Nodes` supplies the
initial graph and journals are applied in file order.

Before saving, `Replace` compares the stored digest with the file's current digest when both
are available. If they differ, it refuses the save because the file has changed externally.

Appending requires an existing file with a digest and known name-table slots. It also requires:

* the log to continue forward from the saved position; undoing past that position requires a
  full rewrite;
* every change since the save to be recorded in the log. Edits through `SourceBuild` can bypass
  logging, so the saved stamp and each operation's before/after stamps must form a complete
  chain to the current source;
* all kinds and relations used by the appended operations to have slots in the file's tables.

If these append requirements are not met, `Replace` writes the complete source to a temporary
file beside the destination and replaces the destination. Save cost therefore depends on
whether the changes qualify for appending.

Journals retain all logged operations needed to replay the saved state, including operations
on generated content. The `History` section also retains these operations. Journal replay must
preserve allocation order so subsequent operations address the intended node IDs.

A full rewrite folds journal changes into the graph and writes the applicable history. It
omits the old journals to avoid applying those changes twice. `roundtrip` reports this folding
when checking a journalled file.

### Integrity

The `Checksum` section contains a SHA-256 digest of the non-journal sections' content, excluding
the checksum itself. `verify` recomputes it to detect changes that structural validation may
miss, such as altered payload bytes. Saves also use the stored digest to check for external
changes.

Each journal carries a digest of its own bytes rather than being folded into the checksum,
because updating a digest of the whole file means reading the whole file. A file's digest is
the checksum with each journal's folded in after it, so an append still moves it.

An ordinary save preserves the absence of a checksum in older files. Such files cannot use
the digest-based external-change check or incremental append. `upgrade` rewrites them in the
current format.

### Versions

The format is at **major 4, minor 0**.

Minor versions add compatible sections. A major version changes layout or interpretation in a
way that older readers cannot safely preserve. The reader rejects unsupported newer major
versions and supports the previous major versions. `upgrade` rewrites a source using the current
format while preserving its content and identities.

Major 2 introduced variable-width indices and counts. Major 3 changed the name tables. Major 4
added journals and changed how readers reconstruct the graph: reading only `Nodes` would omit
all appended edits, so older readers must reject these files.

### Compression

The current format stores payloads without compression. This permits direct memory mapping and
appending journals without recompressing existing sections. Applications can compress a file
for transport or archival storage and decompress it before opening it.

## The command line

```bash
dotnet run --project src/Supersource.Cli -c Release -- <verb> <file> [args]
```

| verb | purpose |
|---|---|
| `cat` | print the graph |
| `node <n>` | inspect one node, its state, parent, payload, indexed references and child IDs |
| `edit <edits.json> [--dry-run] [--expect-hash <sha256>]` | apply a checked batch of content edits |
| `stat` | how much of this source is interpretable |
| `find <kind> [text]` | nodes of a kind, optionally holding some text |
| `refs <n>` | what points at node n |
| `scope <n>` | declarations visible at node n, innermost first |
| `holes` | every hole and dangling reference |
| `kinds` | every kind the file describes, and what it says |
| `sections` | what is in the file, how big, and what went unread |
| `validate` | structural integrity |
| `rules` | check source policies and schema conformance |
| `verify` | check the file against the digest it carries |
| `roundtrip` | read, write back, compare the bytes |
| `upgrade` | rewrite in the current format version |
| `history [--node n] [--actor n]` | what was done, in order |
| `blame <n>` | which entry last touched node n |
| `diff <other>` | the operations turning this source into that one |
| `merge <base> <ours> <theirs> <out>` | three-way merge |
| `deps [libs...]` | cross-source references, and whether they resolve |

Run `dotnet run --project src/Supersource.Cli -c Release -- --help` for full argument syntax. Node IDs accept `n12` or `12`.
`node` shows only the selected node and immediate child IDs; it does not traverse descendants.
It prints the full text payload and direct lists, so a large node can still produce substantial
output. `cat` prints the containment tree.

These commands operate on the graph and embedded schemas without loading domain kind libraries.
`upgrade` rewrites its input in place and accepts no options. `merge` writes the requested output
path, including when conflicts produce exit code 3. `roundtrip` creates and removes a sibling
file named `<file>.roundtrip`; `upgrade` uses `<file>.migrating`. Use a temporary copy if a sibling
with that name already exists.

Exit 0 means the command completed, not necessarily that it found no differences or findings.
Exit 1 indicates a usage error or unknown command. Exit 2 covers unreadable/corrupt inputs,
invalid node IDs and rejected upgrade arguments. Exit 3 indicates failed checks, a refused
operation or a contested merge. Read the command's output to distinguish these outcomes.
`holes` and `deps` can succeed while reporting unresolved references; `rules` can succeed with
nonblocking findings.

For ordinary files, `roundtrip` compares bytes. For journalled files it reports folding and
checks node counts; this is a limited check rather than full semantic-equivalence validation.

## Layout

```
src/Supersource/            the model and the kind system
  Source/                   Node, ids, Source, build, walk, reverse index, compaction
  Kinds/                    KindTable, schemas, the bootstrap, external references
  Format/                   read, write, validate, history, digest
  Resolve/                  scope, binding, linking, workspaces
  Rules.cs                  source rules and the stock set
  Timeline.cs               operations, application and history log
  Compare/                  diff, payload diff, merge, merge records
src/Supersource.Vocabularies/
  PropertyKinds.cs          valued tags
  Relate.cs                 shared relations and annotations
  Derive.cs                 provenance, staleness and generation
  DerivationRules.cs        opt-in derivation checks
src/Supersource.Cli/        the `supersource` binary
tests/Supersource.Tests/            the format's own tests
tests/Supersource.Vocabularies.Tests/ optional vocabulary behavior
tests/Supersource.Cli.Tests/        CLI behaviour and argument handling
tests/Supersource.StrangerTests/    reading without domain kind libraries
tests/fixtures/                   externally authored sources
```

The CLI assembly is named `Supersource.Cli` to distinguish it from the `Supersource` library.

The vocabulary separation changes C# assembly references and removes application-specific
APIs; it does not change the wire encoding. Files saved by earlier writers remain readable.
History previously omitted for generated nodes cannot be recovered from those files.

## Conventions

Group closely related types in the same file; keep substantial independent components separate.
Use file-scoped namespaces (`namespace Supersource;`). The implementation favours direct loops,
public data fields and explicit counts. Avoid LINQ,
reflection, exceptions for ordinary control flow and allocations on hot paths. Source APIs
use `ref Source` for mutation, `in Source` for reading and `out` parameters for results.

Comments should explain purpose and constraints that are not apparent from the code. Keep
longer design explanations in this reference. Some existing comments cite historical design
notes using `§N`; explanations should be understandable without access to those notes.

## Fixture provenance

The five fixtures were authored with code, raster, plan, property and relation libraries outside
this repository. The language kinds live in `superscript`; the content kinds live in Saket Studio.

`Supersource.StrangerTests` references only `Supersource` and checks that these files can be read
without their authoring libraries. `Spec_FixturesFresh` checks that each used kind has an embedded
schema. Regeneration requires the external libraries and their `ss fixtures` command.
