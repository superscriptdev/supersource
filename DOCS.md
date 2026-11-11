# Supersource reference

Supersource is a file format for graphs, and a filesystem for what is stored in them. A file
holds one source: nodes with an identity, a kind, a payload, ordered children and typed
references to other nodes, together with the schemas that say how each kind's bytes read. It
stores state. It keeps no history and does no version control; an application that wants
either builds it on top.

What a node means belongs to whoever defines its kind. A source could hold a document, a
dataset, a scene, a program, or a project combining several kinds of content, and the format
treats them all the same way: it can store, walk, check, compare and merge any of them, and
it never needs the software that defined a kind to do so.

Node identity lasts for the life of the source. Changing a node's payload, kind or position
leaves every reference to it intact, so an editor can keep a comment attached to a paragraph
while its text changes, and a build tool can record which input a generated part came from.

This repository provides the format implementation and a command-line tool, in .NET 10 with
no external dependencies. It supports stream-based I/O, memory-mapped payloads, incremental
saves when edits can be appended, structural diff and three-way merge.

## Getting started

```bash
dotnet build Supersource.slnx
dotnet test  Supersource.slnx
dotnet run --project src/Supersource.Cli -c Release -- cat tests/fixtures/vec.ss
```

```
source 00000000000000030000000000000003
39 slots, 16 live

module  "math"   n0
  f32  "f32"   n1
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
            use   n11
              --references--> n5
            use   n12
              --references--> n6
    doc  "sums two numbers"   n13
      --about--> n4
```

The CLI prints this from the embedded schemas alone; it knows nothing else about these kinds. The
23 ids that are not live belonged to nodes this source deleted; their ids are never given out
again.

```bash
dotnet run --project src/Supersource.Cli -c Release -- stat tests/fixtures/vec.ss
```

```
nodes            16 live, 23 deleted
kinds            12 named
relations        5 named

uninterpretable  0  0.0%  no embedded schema
opaque interior  0  0.0%  declared
opaque payload   0  0.0%  declared

payload bytes    62, 0 opaque (0%)
unread sections  0
```

The fixtures were authored by kind libraries outside this repository; see
[Fixture provenance](#fixture-provenance).

## Terms

[VOCABULARY.md](VOCABULARY.md) defines every word this reference uses, one meaning each.

## The model

A node's payload holds its own content. Its children are what it contains, in order. Its
references record relationships that can cross the containment tree.

```text
document
  paragraph n1  "The west entrance"
  image     n2  <image bytes>
  comment   n3  "Check this description"
    --about--> n1
```

The paragraph, image and comment are children of the document. The `about` reference records
which node the comment concerns. Keeping `n1` while changing its text preserves that link. The
application decides whether paragraphs, sentences or some other unit get identities of their own.

### Containment and references

Containment is a tree: a contained node has one parent, and children are ordered. The root has
no parent. A child index that names no node is corruption.

References form a graph over the tree. A reference carries a relation and a target, or no
target: an **unbound** reference, whose edge data is its label: what it is waiting for, such as
a name. A bound reference whose target was deleted is **dangling**. Both are valid states of a
source, and a reader can say which a reference is in (`SourceWalk.StateOf` → `RefState`: bound,
unbound, dangling, or out of range, which only a damaged file holds).

A reference can carry **edge data**: bytes that say which part of its target it addresses, such
as a pixel within an image or a range within a text. The relation's schema declares their format.

`Backrefs` indexes references by target, to answer what points at a node. It is derived from the
stored edges and can be rebuilt from them.

### Identity

A `NodeId` names a node for the life of its source: a 64-bit value biased by one, so that
`default` means none. **Deletion means gone.** A deleted node and its subtree leave the source;
the source keeps its **next id** and never reuses one, so an id names one node or none, ever.
References into a deleted node are left as they were, and dangle.

A source has a `SourceId`, minted when it is created. It survives moving, renaming and copying
the file, so a copied file is the same source until an application gives it a new identity
(`SourceBuild.MintIdentity`). A reference into another source names that identity and a node in
it; see [References into other sources](#references-into-other-sources).

## Kinds and schemas

A kind has a qualified name: the `SourceId` of the library that defines it and a local name, so
two libraries can both define `table` without colliding. The defining library is a source like
any other; declaring kinds into it is what makes it a **kind library**.

A kind's **schema** says what a reader without the kind's software can know about its nodes: its
structural facts, how its payload reads, and which relations its nodes use. A relation's schema
says how its edge data reads. Schemas describe; they do not constrain. Nothing in a schema says
which children a node may have.

| `StructuralFact` | what it tells a reader |
|---|---|
| `InteriorOpaque` | the interior is bytes to keep, not structure to walk, and that is declared |
| `ChildOrderSignificant` | sibling order means something, so diff and merge keep it |
| `Annotation` | nodes of this kind annotate their parent |

Which kinds are declarations and which open a scope is a language's knowledge of its own kinds,
not the format's. Files written before major 5 carried those as fact bits 0 and 1; a reader drops
them.

### Payload formats

A payload format says how a kind's payload bytes, or a relation's edge data, read:

| form | the bytes are |
|---|---|
| `none` | absent |
| `opaque` | present, and undescribed |
| `text` | UTF-8 |
| `text after` *header* | a fixed header, then UTF-8 |
| `one` *T* | exactly one element |
| `many` *T* [`after` *header*] | an optional fixed header, then any number of elements |

An element *T* is either only sized (a reader can count elements but not look inside them), or
described by **fields**: named scalars at offsets, each with an optional fixed repeat. Scalars are
`i8` `i16` `i32` `i64` `u8` `u16` `u32` `u64` `f16` `f32` `f64` `bool`, little-endian. A header is
an element of the same shape. So a vertex buffer can be `many {position f32×3 @0, visible bool @12}
16`, and a reader that has never heard of vertices can still address the fourth position.

A format is stated once, in the schema; a node stores only its bytes. When a format fixes the
length — `none`, or `one` *T* — the file does not store it either.

### Embedded schemas

Every file carries the schemas of the kinds and relations it names, in its `Schemas` section, so
it reads alone: a reader with none of the libraries that defined its kinds can still walk, check,
diff and merge it. A kind library's file also carries the schemas of the kinds it declares itself
— those whose qualified name's library is the source's own identity — whether or not any node
uses them yet.

A kind with no schema is **uninterpretable**: its nodes still read, round-trip and can be edited,
but a reader knows nothing about their bytes. The `every-kind-is-interpretable` rule reports them.

## References into other sources

A reference into another source is two nodes the application places, the way a filesystem holds
links among files:

* a **`dependency`** holds another source's address once: its `SourceId`, the content hash of the
  version it is pinned to, and a locator — a hint where to fetch it. Its payload is `text after
  {source u8×16 @0, hash u8×32 @16} 48`, so the CLI shows it as the hex every tool prints, then
  the locator.
* an **`external-ref`** stands for one node in a dependency. Its payload is that node's id; its
  first reference, `of`, names the dependency.

Ordinary local edges point at external references, so a reference across sources is drawn
exactly like one within a source. However many references go into one version of a source, its
address is stored once, and repinning to a new version is one edit of the dependency's payload.
Two versions of one source are two dependencies. A foreign file that is not a source is a
dependency with no `SourceId`; one with no hash either is told apart by its locator.

`ExternalKinds` encodes and decodes both, finds or makes the dependency for an address
(`DependencyFor`), declares and retargets references. `Externals` queries them: what a source
depends on, which references are unpinned, and whether fetched bytes match a pin.

### Content hashes and pins

A source's **content hash** is a SHA-256 of its *state*: every live node by id, with its kind's
name, payload, children and references, then the schemas of the kinds and relations in use,
named rather than numbered (`SourceHash`). Two files that read as the same graph have the same
hash however they were saved — appended to or written in full, in this major or an older one. A
**pin** is a content hash in a dependency: it names one exact version. The checksum is a
different thing, a digest of a file's bytes; see [Integrity](#integrity).

### Resolving across sources

`Workspace` holds several loaded sources and resolves references between them. `Workspace.Resolve`
finds the target source and node among those registered; a content hash, when present, selects
the version. Unpinned references can resolve, and the result says so. A missing source, a
missing version, a missing node and two registrations of one identity are explicit outcomes.
Fetching another source is the application's job; `Workspace.Add` takes the identity and hash the
loader supplies without checking them against the bytes.

A registration holds a copy of the `Source` struct, which carries array references and counts.
Editing a source replaces those arrays, so a registration made before an edit keeps answering
from the graph as it stood: a node added afterwards resolves as `NodeMissing`. `Reach` routes the
home source around this, taking the live source from its caller. For every other registered
source, `Workspace.IsStale` reports whether a registration has fallen behind and
`Workspace.Update` refreshes it in place, keeping the identity and hash the loader supplied.

## Rules

`SourceValidate` checks structural integrity: node indices, containment, run bounds. Rules check
what a structurally valid source may still fail. The caller chooses which to run and how severe
each is (`RuleSet`); reading a file runs none of them.

| rule | reports |
|---|---|
| `no-dangling-references` | bound references whose target is not live |
| `no-unbound-references` | references with no target |
| `every-kind-is-interpretable` | nodes whose kind has no schema |
| `payload-matches-the-schema` | payloads whose bytes do not read as their kind's format says |
| `edge-data-matches-the-relation` | edge data that does not read as its relation's format says |

`RelationCheck.Check` reports references whose relation the owning kind's schema does not list,
alongside unbound and dangling references. Callers can pass `permittedRelations` to allow chosen
extension relations; by default it permits none.

## Edits, diff and merge

### Operations and the log

An edit is an `Operation`: `Insert`, `Delete`, `Move`, `ModifyValue`, `Splice`, `Wrap`, `Unwrap`,
`Reorder`, `Connect`, `Disconnect`, `Retype`. Each names nodes by id and carries what it needs to
be undone: a delete captures the subtree it removed (`Removed`), and undoing it restores that
subtree at the same ids. `Log.Do` applies an operation and records it; `SourceBuild` mutates
directly, without recording. A retype changes what a node is and keeps who it is: its id, its
payload, its children and everything pointing at it.

**A file holds a state, not the edits that produced it.** The log is session state, from when a
source was opened or created. A save that can append writes the entries since the previous save
as a journal; a full write folds them into the graph. Either way, reopening a file starts with an
empty log. Grouping edits, naming states and deciding what undo means belong to the application.

One operation carries at most 2 GB of new bytes, and its capture of what it replaced is limited
the same way: a payload larger than that is built, or changed, by several splices, whose offsets
are 64-bit. An operation that destroys more than 2 GB of one payload — replacing it, splicing that
much out, deleting a subtree that holds it — applies, saves and journals like any other, but is
`Uninvertible`: `Log.Undo` refuses it rather than restore something it could not keep.

`Log.Clone` copies the log with its undone suffix and cursor, which forking a source for draft
editing needs.

### Diff

`StructuralDiff` takes two states of one source and returns operations. There is no matching
step: node n12 in one is node n12 in the other, because ids are never reused. The two states must
share a `SourceId`, otherwise their ids are unrelated numbers.

* only in B → `Insert` at its id; only in A → `Delete`
* a different parent → `Move`; a different kind → `Retype`
* a different child order → `Reorder`, a semantic change only when the kind says
  `ChildOrderSignificant`
* different references → `Connect` / `Disconnect`

Inserted nodes exist before anything refers to them, moves precede deletion, and sibling order is
settled after membership changes. Tests apply each diff to its starting state and compare the
result with the target.

Payloads are compared by their declared format. `PayloadDiff` trims the common prefix and suffix
and describes what is left as a splice: text aligns to code points, elements to whole elements,
so a small change is a small operation. Whole-payload replacement is the fallback.

### Merge

`Merge.Between` reconciles a common state with "ours" and "theirs", by node and by facet: payload,
containment, child order, references and existence. It returns the merged source and every
decision it made (`MergeNote`: the node, the facet, how it was decided, and why). On a conflict
it keeps ours. Where both sides changed disjoint ranges of a text or an element array, it keeps
both. The merged source holds the merged state and nothing about how it was reached; presenting
and resolving the decisions is the caller's.

Both sides can give the same new id to different nodes. The merge gives their new nodes fresh ids
and remaps the references to them. Kinds and relations are matched by name, since each side has
its own table numbering. A structural merge does not establish that the result is valid by an
application's own rules.

## Creating a source

```sh
dotnet run --project src/Supersource.Cli -- new prose.ss --root prose --identity mint
dotnet run --project src/Supersource.Cli -- declare prose.ss prose.json
dotnet run --project src/Supersource.Cli -- new doc.ss --root article --kinds prose.ss --label "On Formats"
```

`new` mints an identity, adds a root and writes the schemas the source uses. `--root` names a kind
seeded from the source given by `--kinds`, or else is a plain name that becomes a kind of the new
source's own, with a text payload: a source needs no other library's kind to start from.
`--identity` takes 32 hex digits instead of minting, for a library its consumers pin. `--label`
sets the root's payload and needs a root whose payload is text.

`declare` adds kinds and relations to a source, which makes that source a kind library: its own
identity becomes the library half of every name it declares. The file maps one to one onto
`KindTable.DefineSchema` and `DefineRelation`:

```json
{
  "relations": [{ "name": "documents", "payload": "none" }],
  "kinds": [
    { "name": "article", "facts": ["child-order-significant"],
      "payload": "text", "uses": ["documents"] },
    { "name": "mesh", "payload": { "many": { "size": 16, "fields": [
        { "name": "position", "type": "f32", "offset": 0, "repeat": 3 },
        { "name": "visible", "type": "bool", "offset": 12 } ] } } }
  ]
}
```

`facts` are `interior-opaque`, `child-order-significant` and `annotation`. A payload is `none`,
`opaque`, `text`, `{"text": header}`, `{"one": element}` or `{"many": element, "header": element}`;
an element is a scalar name such as `"f32"`, `{"size": n}`, or `{"size": n, "fields": […]}`.
Redeclaring a kind the library already has is refused: reinterpreting the bytes of every node of
that kind is a migration, not a declaration. A library needs no node for the kinds it declares.

## Editing a source

The CLI edits a source with a JSON batch. Each operation goes through `Log.Do`, so the batch keeps
node ids and the inverse data undo needs, and it saves by appending when it can.

```sh
dotnet run --project src/Supersource.Cli -c Release -- edit source.ss edits.json --dry-run
dotnet run --project src/Supersource.Cli -c Release -- edit source.ss edits.json --expect-hash <sha256-from-preview>
```

The edits file is a nonempty JSON array, applied in order, each operation against the result of
the ones before it:

```json
[
  {"op":"insert","parent":"n0","kind":"paragraph","text":"First draft","as":"draft"},
  {"op":"set-payload","node":"@draft","text":"Revised draft"}
]
```

`as` gives an inserted node a handle for the rest of the batch: `@draft` works wherever a node id
does, and the command reports the id it was given. Handles are nonempty and unique within a batch.

### Patch fields

Every operation needs `op`. Node ids are strings such as `"n4"` or `"4"`. Unknown or duplicate
fields are refused.

| `op` | required | optional |
|---|---|---|
| `insert` | `parent`, `kind` | `index`, `as`, `text` or `base64` |
| `set-payload` | `node`, `text` or `base64` | — |
| `splice` | `node`, `offset`, `remove`, `text` or `base64` | — |
| `move` | `node`, `parent` | `index` |
| `retype` | `node`, `kind` | — |
| `delete` | `node` | — |
| `connect` | `node`, `relation`, `target` | `index`, `text` or `base64` |
| `disconnect` | `node`, `index` | — |
| `extern` | `parent`, `source`, `node` | `locator`, `hash`, `index`, `as` |

`text` is UTF-8 and needs a kind whose payload is text; `base64` gives exact bytes for any other
format. An empty string clears a payload. Kinds and relations are named by an unambiguous local
name or by `library-id/name`, as `node` and `kinds` print them; `--kinds <source.ss>` seeds names
from another source, so a batch can use a kind the document has never held. Indexes are
zero-based; insert and connect append by default; a move's index counts after the moved node is
taken out. `disconnect` takes the reference index `node` prints in brackets. A `null` target
makes an unbound reference whose optional text is its label. Splice offsets count bytes, and for
text may not split a code point. Offsets are 64-bit.

`extern` adds an external reference: its dependency under the root, when the source has none for
that address yet, and then the reference and its `of` edge. Ordinary `connect` edges then point at
the handle it registers.

### Validation and saving

The CLI checks the source's structure before editing, applies the batch in memory, then checks
the result's structure and the payload and edge formats of the nodes it touched. Incomplete
references are allowed. An invalid batch is discarded without saving.

`--dry-run` goes through the same checks, prints the input's SHA-256 and the operations, and
leaves the file alone. `--expect-hash` refuses the edit if the file's hash has changed since. The
CLI also rechecks the file before saving in place with `SourceWrite.Replace`. These checks detect
a stale input; they do not isolate concurrent writers.

The root cannot be deleted or moved. Exit 1 means the batch or its result was refused; exit 3 a
hash mismatch or a refused save; exit 2 a corrupt input or an I/O error. A pre-existing
`<file>.writing` sibling is preserved and refuses the save.

## What this repository is not

The format and its generic tools are open; applications and kind libraries built on them may be
open or closed. This repository does not provide a programming language, name resolution, a
standard library, domain kind libraries, importers or exporters, editors or rendering. Those are
built on Supersource, and the format knows nothing about them: no kind of theirs is the format's,
and no tool here writes a kind of its own into content.

It keeps no history and does no version control. Diff and merge compare and combine states; what
states were worth keeping, and who made them, is for what is built on top.

### APIs with no consumer in this tree

`StructuralDiff`, `Merge`, `RuleSet`, `RelationCheck` and `KindRemap` are CLI verbs; `PayloadDiff`
and `SourceCompact` serve diff, merge and `SourceFile.Clone`; `JournalCodec` serves appended saves.
**`KindRename`** — renaming a kind or relation across a file — has no verb and no caller. It is
kept and supported, but its contract has never met a second caller, so treat it as provisional.

## In-memory storage

```csharp
struct Node {                        // 48 bytes, asserted
    NodeId  Parent;                  // derived from the child runs when a file is read
    long    PayloadOffset;           // which blob segment, and where in it
    long    PayloadLength;           // 64-bit: one payload may be larger than an array
    KindId  Kind;                    // index into the kind table
    int ChildOffset, ChildCount;     // containment, ordered
    int RefOffset,   RefCount;       // typed references out
    NodeFlags Flags;                 // Live: the slot holds a node
}
```

Every kind uses the same layout, and payload bytes are stored apart from it, so storage code
preserves a node without understanding its content. Tests assert the size and field offsets.

`Source` is `Node[]` plus four arenas the runs point into: `Children`, `Refs`, `Blob` and
`EdgeData`. Growing a run past its capacity moves it to the end of its arena and abandons the old
span, so an arena carries a high-water mark rather than a live count; `SourceCompact` rebuilds the
arenas and keeps every id. A deleted node's slot is empty: no kind, no payload, no runs.

The blob is segmented, so total payload storage can exceed one `byte[]`. A payload is addressed by
a 64-bit segment and offset and has a 64-bit length, so one payload has no size limit. An owned
payload larger than one chunk (`Source.ChunkBytes`, 1 GiB by default; not stored) is held in a
segment of its own as equal chunks. `SourceWalk.Payload` returns a payload as one span when it is
one run of memory (`SourceWalk.IsContiguous`); otherwise `ReadPayload` reads it in windows,
`OpenPayload` streams it, `SamePayload` compares two, and `SetPayload` takes a stream. Hashing,
rules, diff and merge read payloads in windows when they must.

A `Source` is a struct of references to arrays. Assigning it shares them; it is not a snapshot.

### Copying a loaded source

`SourceFile.Clone` makes an independent copy: graph, kind table, log and preserved unknown
sections. Node, kind and relation handles keep their meaning in the copy, which a write and a
read do not give, since a reloaded file interns its names in first-use order. The copy owns its
payload bytes and holds no mapping; because `SourceCompact` relocates the arenas, it carries no
saved-digest or append state, and saving it writes a complete file. It keeps the identity it
copied: two files with one identity are two copies of one source, which `Workspace` reports as an
`IdentityCollision` when both are registered.

## File format

```
magic "SSSRC\r\n\x1a"  ·  major (u16) · minor (u16)  ·  section count (u32)
sections, each:  id (u32) · length (u64) · bytes
```

| id | section | holds |
|---|---|---|
| 1 | `Identity` | the source's id (16 bytes), its root (svar), its next id (uvar) |
| 2 | `KindNames` | the kind names the file uses, each library id once, names under it |
| 3 | `RelationNames` | the relation names, the same way |
| 8 | `Schemas` | the schema of every kind and relation the file names, by name-table slot |
| 4 | `Nodes` | the live nodes, in id order, as runs |
| 6 | `Checksum` | a SHA-256 of the other sections except journals |
| 7 | `Journal` | the operations appended since the previous saved state |

Id 5 was a history section; a reader skips it and a writer drops it. A reader uses section lengths
to skip sections it does not know and writes their bytes back unchanged, so an older reader keeps
what a newer compatible writer added. Sections may be stored in any order and are read in
dependency order: names, then schemas, then nodes.

Inside a section, counts and indices are variable-width. `Nodes` writes only live nodes, as runs of
consecutive ids (a gap from the last run, then a length), so deleted ids cost nothing. A node is
`uvar ((kindSlot+1) << 1 | explicitLength)`, the payload length only when its format does not fix
it, the payload, its children as deltas, and its references, each `uvar ((relationSlot+1) << 1 |
explicitLength)`, the target plus one (zero for unbound), and the edge data. No parent and no
flags are stored: a parent is read off its parent's child list, and everything written is live.

`Schemas` is, per kind, its slot, its facts (u16), its payload format and the slots of the
relations it uses; then, per relation with edge data, its slot and format. A format is a form byte
(none 0, opaque 1, text 2, one 3, many 4, text after a header 5) followed by its elements; an
element is a size, a field count, and per field a name, a scalar byte, an offset and a repeat.

### Streaming I/O

The reader and writer work through buffered streams rather than one array for the whole file, so
I/O buffering is bounded by individual reads; node metadata and owned payloads still take their
memory. Both need a seekable stream: a section's length is written before its content, so eight
bytes are left blank and filled in once the extent is known.

### Memory-mapped payloads

`SourceRead.FromFile` maps the file and registers it as a blob segment, so a node's payload is a
place in the file rather than bytes copied out of it, until something reads it. A mapped payload
has no spare capacity, so the first edit moves it into a segment the source owns; the file is never
written through. Keep the mapping open while payloads point into it and close it with
`SourceFile.Close`; `SourceCompact.Compact` copies payloads into memory the copy owns.

### Incremental saves

`SourceWrite.Replace` appends the logged edits since the last save as a `Journal` section when it
can, instead of rewriting the file, and updates the section count in the header. On read, `Nodes`
gives the saved graph and the journals are applied in file order. A journal entry carries only what
replay needs; a delete records the subtree it removed as inserts, so replay rebuilds it.

Before saving, `Replace` compares the digest it read with the file's current one, and refuses if the
file has changed underneath it. Appending also needs: a file already in the current major; a log
that continues forward from the saved position (undoing past it needs a full write); every change
since the save recorded in the log (edits through `SourceBuild` are not); and a kind table that has
not changed, since the name tables and schemas are not rewritten by an append. Otherwise `Replace`
writes the whole source beside the destination and replaces it. A full write folds the journals
into the graph and omits them.

### Integrity

The `Checksum` section holds a SHA-256 of the non-journal sections. `verify` recomputes it, which
catches what structural validation misses, such as altered payload bytes. Each journal carries a
digest of its own bytes, since updating a digest of the whole file would mean reading the whole
file; a file's digest is the checksum with each journal's folded in after it.

The checksum answers whether a file's bytes changed. The [content hash](#content-hashes-and-pins)
answers which state a source holds. They are different on purpose.

### Versions

The format is at **major 5, minor 0**. A minor version adds compatible sections; a major version
changes layout or interpretation in a way an older reader cannot safely preserve, and a reader
refuses a newer major. This build reads majors 1 to 5 and writes 5; `upgrade` rewrites a file in the
current major, keeping its content and identities.

Major 5 moved schemas from nodes into a section, stored each node once (no parent, no flags, no
dead slots, no length a format fixes), put the next id in the identity section, and put each
dependency's address in a node of its own. Reading major 4 converts: schema nodes become the
section, external references become node ids and shared dependencies.

### Compression

Payloads are stored uncompressed, which keeps them mappable and lets journals append without
recompressing anything. Compress a file for transport or archive and decompress it before opening.

## The command line

```bash
dotnet run --project src/Supersource.Cli -c Release -- <verb> <file> [args]
```

| verb | purpose |
|---|---|
| `cat` | print the graph |
| `node <n>` | one node: its state, parent, payload, indexed references and child ids |
| `stat` | counts, payload sizes and how much of the source is interpretable |
| `find <kind> [text]` | nodes of a kind, optionally holding some text |
| `refs <n>` | what points at node n |
| `missing` | unbound and dangling references: what the source refers to and does not have |
| `kinds` | every schema the file carries |
| `sections` | what is in the file, how big, and what went unread |
| `validate` | structural integrity |
| `rules` | the stock rules |
| `verify` | the file against the digest it carries |
| `roundtrip` | read, write back, compare the bytes |
| `upgrade` | rewrite in the current major |
| `diff <before> <after>` | the operations turning one state into the other |
| `merge <base> <ours> <theirs> <out>` | write a three-way merge and print its decisions |
| `deps [libs...]` | external references, and whether they resolve against the given libraries |
| `new <file> --root <kind>` | create a source with an identity and a root |
| `declare <file> <kinds.json>` | add kinds and relations, making the source a kind library |
| `edit <file> <edits.json>` | apply a checked batch |

`--help` prints the full argument syntax. Node ids accept `n12` or `12`. `node` shows the selected
node and its immediate children's ids, not their descendants.

Exit 0 means the command completed, not that it found nothing: `missing` and `deps` succeed while
reporting unresolved references, and `rules` succeeds with findings that do not block. Exit 1 is a
usage error, exit 2 an unreadable or corrupt input, exit 3 a failed check, a refused operation or a
contested merge. `roundtrip` makes and removes a `<file>.roundtrip` sibling and `upgrade` a
`<file>.migrating` one.

## Layers, and the tools each owns

This command line is the bottom of a stack. A layer above contains it: its binary answers every
verb here and adds its own (`Program.Dispatch`, `TryRun` and `VerbLines` are what each layer
composes). **A tool belongs to the layer that owns its concept.** This layer owns nodes, kinds,
schemas, references, dependencies, diff and merge, so it has no verb that knows what a function or
a paragraph is, or what a name refers to.

The same holds for MCP. `Mcp.Serve` speaks the protocol and each layer publishes a tool table over
its own verbs, named for the layer that answers: `source.*` here, and a prefix of its own for each
layer above. `Mcp.Check` refuses a malformed table.

## Layout

```
src/Supersource/            the format
  Source/                   Node, ids, Source, build, walk, reference state, reverse index, compaction
  Kinds/                    KindTable, schemas and payload formats, external references, rename, remap
  Format/                   read, write, schemas section, journals, validation, content hash, older majors
  Resolve/                  workspaces and the relation check
  Rules.cs                  rules and the stock set
  Timeline.cs               operations, application and the log
  Compare/                  diff, payload diff, merge
src/Supersource.Cli/        the `supersource` binary
tests/Supersource.Tests/            the format's own tests
tests/Supersource.Cli.Tests/        the command line
tests/Supersource.StrangerTests/    reading without the libraries that authored a file
tests/fixtures/                     externally authored sources
```

The CLI assembly is named `Supersource.Cli` to tell it apart from the `Supersource` library.

## Conventions

Group closely related types in one file; keep substantial independent components apart. Use
file-scoped namespaces. The implementation favours direct loops, public data fields and explicit
counts, and avoids LINQ, reflection, exceptions for ordinary control flow and allocations on hot
paths. Source APIs take `ref Source` to mutate, `in Source` to read and `out` parameters for
results.

Comments say what they comment: purpose and constraints the code does not show, not history.

## Fixture provenance

The fixtures were authored with code, raster, plan, property and relation libraries outside this
repository. `Supersource.StrangerTests` references only `Supersource` and checks that each file
reads without its authoring libraries, and `Spec_FixturesFresh` that each kind a fixture uses has
an embedded schema. Regenerating them needs those libraries.
