# Vocabulary

The words Supersource uses, one meaning each. A word that needs a qualifier to be understood is
two words, and the second sense gets an entry of its own.

Supersource is a storage format for structured data: a filesystem for graphs. These are the
words for what it stores, how a stored thing is identified, and how it is laid out on disk.
Words for what is built on top — a language, a document, an editor, version control — belong
to those layers. The ones most often mistaken for format words are listed at the end, so their
absence reads as a decision rather than a gap.

## The model

**source** — A graph of nodes with one identity. The unit that is opened, saved and shared,
stored as exactly one file.

**file** — The bytes a source is stored as. Moving, renaming or copying the file does not
change which source it holds.

**node** — One element of a source: a kind, a payload, ordered children and ordered
references, named by its node id. What a node stands for — a paragraph, a mesh, a function, a
table row — is the application's to say.

**root** — The node a source starts from. Every other node is beneath it.

**child, parent** — Containment. A node has at most one parent and its children are in order.
Containment is a tree, and it says what is part of what.

**reference** — An edge from one node to another, carrying a relation. References cross the
tree and make it a graph; they say what is about what. A reference is an entry in its node's
ordered list and has no identity of its own, so anything that has to be pointed at is a node.

**relation** — What a reference means, such as `about` or `calls`. Named the way a kind is.

**edge data** — Bytes a reference carries, such as which pixel of an image it addresses. Its
relation declares their format.

**bound, unbound** — A bound reference has a target. An unbound one has none yet, and its edge
data usually says what it is waiting for, such as a name. Storing one is valid.

**label** — An unbound reference's edge data, read as what it is waiting for. A bound reference
has no label; its edge data says which part of its target it addresses.

**dangling** — A bound reference whose target was deleted. Also valid: deleting a node never
touches the references pointing at it, and what a dangling reference means is the
application's to decide.

**payload** — A node's content bytes. Its kind's schema says how to read them. A payload has
no size limit; one larger than an array is read and written in windows.

**payload storage** — Where a source keeps all its payloads in memory: a pool of segments, with
each node holding an address and a length into it. The pool has no size limit either.

**segment** — One block of payload storage. A file opened from disk can serve as a segment
itself, so its payloads are read where they lie instead of being copied into memory.

## Identity

**node id** — A 64-bit number naming a node within its source. Ids are handed out in
increasing order, never twice, and never renumbered, because other sources refer to nodes by
id.

**next id** — The one number a source keeps about the nodes it no longer has: the id it will
hand out next. An id below it with no node was deleted; an id at or above it never existed.

**deletion** — Removing a node and everything beneath it. Nothing of it is kept in the file.
The delete operation holds what it removed for as long as somebody keeps the operation, which
is what lets a session take a deletion back under the same ids.

**source id** — A 128-bit identity minted when a source is created. A copied file holds the
same source, and giving a copy an identity of its own is a deliberate act.

**content hash** — The SHA-256 of a source's state: its live nodes by id, with their kinds'
names, payloads, children and references, and the schemas in use. A source id says *which*
source; a content hash says *which version* of it, however the file happens to be saved.

## Kinds and schemas

**kind** — What a node is: a qualified name and a schema. A node can change kind without
changing identity. Identity is which thing; kind is what it is now.

**qualified name** — The name of a kind or relation: the source id of the library that
declares it, plus a local name. Two libraries can both define `table` without colliding.

**kind library** — A source that declares kinds and relations. Its source id is the library
half of their qualified names.

**schema** — What a kind declares about its nodes, so a reader without the kind's software can
walk them, check them and write them back unchanged: its structural facts, its payload format
and the relations its nodes use.

**embedded schema** — Every file carries the schema of every kind and relation it names, so it
can be read with nothing else installed, and a kind library's file also carries the kinds it
declares. This is what makes a stranger's file readable.

**uninterpretable** — A node whose kind has no schema in the file. It is still read and written
back unchanged; nothing can say what it holds.

**structural fact** — A yes or no a schema states about its nodes' structure: whether the
interior is opaque, whether the order of the children means anything, whether a node
annotates its parent.

**payload format** — How a kind's payload bytes, or a relation's edge data, read without its
software. One of six: **none**, **opaque**, **text**, **text after a header**, **one** element, or
**many** elements after an optional header. Stated once per kind; a node stores only its bytes.

**opaque** — Bytes with no declared reading. Preserved exactly and never interpreted.

**text** — UTF-8.

**element** — The unit a *one* or *many* payload is made of. Always a fixed size, so element *n*
of a *many* payload is found without reading the others. An element is either just sized, which
lets a reader find it but not look inside, or described by fields.

**field** — A named scalar at an offset inside an element or a header, optionally repeated a fixed
number of times: `position f32×3 @0`. Fields are what let a stranger read a vertex, a row or a
pixel. There are no nested elements, variable-length fields or strings inside an element;
those make a type system, which is a language's.

**scalar** — `i8` to `i64`, `u8` to `u64`, `f16`, `f32`, `f64` or `bool`, little-endian.

**header** — Fixed-size bytes before the elements of a *many* payload or the text of a *text
after a header* one, such as an image's width and height, or an address before a name. Described
the same way an element is.

## On disk

**section** — A framed block of a file: an id, a length and the bytes. A reader skips a section
it does not know and writes it back unchanged, which is how an older reader keeps what a newer
writer added.

**identity section** — The source id, the root and the next id.

**name tables** — The kind names and the relation names a file uses, each listed once. A node
refers to its kind by position in the table, so it stores a small number rather than a name.

**nodes section** — Every node: its kind, its payload, its children and its references. Each
thing is written once — a parent is read off its children, and a payload's length is written only
when its format lets it vary.

**schema section** — The embedded schemas.

**checksum** — A SHA-256 over the file's sections. It tells a tool whether the bytes changed
since they were written, and it is how a save refuses to overwrite a file somebody else changed
in the meantime. It is about bytes; the content hash is about state.

**journal** — Operations appended to the end of a file instead of rewriting it, so a small edit
to a large file costs a small write. Replayed onto the nodes when the file is read and folded
into them by the next full write. A journal records operations forward and keeps nothing they
replaced.

**full write, append** — The two ways a save happens: write every section afresh, or append a
journal. A save appends when it can and writes in full otherwise.

**format version** — A major and a minor number. A minor version adds sections an older reader
can skip. A major version changes what every reader has to understand, and `upgrade` rewrites a
file in the current one.

**retired section** — A section id that is no longer written. Readers skip it, the next save
drops it, and the id is never given to anything else. Section 5 once held a log of every edit
ever made.

## Change

**operation** — One change to a source: insert, delete, move, set payload, splice, connect,
disconnect, reorder, wrap, unwrap or retype. It names nodes by id. Applying it captures what it
replaced, so it can be inverted. One operation carries at most 2 GB of new bytes; a larger payload
is built by several. An operation that destroys more than that of one payload has no inverse.

**inverse** — The operation that takes another one back. The format can compute it because it
is the only thing that knows what an edit destroyed.

**log** — The operations applied since a source was opened. Session state, never stored: a file
holds a state, not the edits that produced it. The format keeps it because only the format knows
what an edit destroyed; how edits group into one undo, and what undo means, is the application's.

**splice** — Replacing a range of a payload's bytes, so a one-character edit to a long text
does not replace the whole payload.

**wrap, unwrap** — Moving a run of siblings into a new node at their position, and moving them
back out.

**retype** — Changing a node's kind while keeping its id, so everything pointing at it still
does.

## Beyond one source

**dependency** — A node holding the address of another source this one refers into, once
however many references go there: its source id, its content hash when pinned, and its locator.
Moving to a new version of that source is one edit of this node. A foreign file that is not a
source is a dependency too.

**external reference** — A node standing for one node in a dependency. Its payload is the node id
there, and its first reference, `of`, names the dependency. Ordinary references point at it, so a
reference into another source is drawn exactly like a local one.

**locator** — Where to fetch the target. A hint only; the source id and the content hash say
what the target is.

**pinned** — A dependency that carries a content hash, and so names one exact version. To
**repin** is to move a dependency to the content hash of the version there now.

**workspace** — The loaded sources an external reference is resolved against. Like a mount
table: the format resolves, and the application decides what to load.

## Tools

Tools compute over the format and live beside it. They never write kinds of their own into
somebody's content.

**validate** — Checks a source's structure: every index in range, one parent per node, a tree
under the root.

**rules** — Further checks a caller chooses to apply: no dangling references, no unbound ones,
payloads and edge data matching their formats, and every kind interpretable.

**missing** — What a source refers to and does not have: its unbound and dangling references.
A valid state, which the `missing` verb lists.

**diff** — The operations that turn one state of a source into another. There is no matching
step: node 12 in one state is node 12 in the other.

**merge** — Combining two states that share a base, node by node. What it decided, conflicts
included, is returned to the caller and never written into the content.

## Not the format's words

These belong to what is built on Supersource. A file can hold any of them as content, and the
format has no opinion about what they mean.

- **history, commit, branch, version control** — which states are worth returning to is an
  application's to keep. The format supplies the parts: identity, content hashes, diff and
  merge.
- **derived, generated** — an application says what a node was made from with a reference.
- **display shape, colour** — how a kind looks is an application's.
- **actor, author** — who did something.
- **document, program, scene, paragraph, function** — kinds, which libraries define.
- **scope, declaration, binding, hole** — what a name refers to is a language's question. The
  format says only whether a reference is bound, unbound or dangling.
- **vocabularies** — shared kinds such as `tag`, `span` and `about` belong to a kind library
  built on the format, not to the format.

