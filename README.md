# Supersource

Supersource is a file format for graphs, and a filesystem for what is stored in them. It gives
applications a common way to store content, identify its parts and link them, so tools can
inspect, check, compare and merge the structure without a separate parser for each content type.
It stores state; it keeps no history and does no version control.

A source is a graph of nodes with kinds, payloads, ordered children and references. Applications
choose what those nodes represent: a document, dataset, scene, program or a combination of
content types. Related content can share a source or link across separate sources.

Node identity survives changes to names, content and position. Embedded schemas let generic
tools inspect and compare unfamiliar kinds; applications supply their meaning and behaviour,
such as rendering an image or executing a function.

This repository provides the .NET library and CLI, including memory-mapped payloads,
incremental saves when edits can be appended, structural diff and three-way merge.

Every file embeds the schemas of the kinds it names, so a reader with none of the libraries that
defined them can still walk and preserve its content; opaque payloads stay valid content even
when no reader can interpret them. Applications decide how content is displayed, edited or
executed. The format defines no content kinds of its own beyond those for references into other
sources.

## Getting started

Requires .NET 10. From this directory:

```sh
dotnet build Supersource.slnx
dotnet test Supersource.slnx
dotnet run --project src/Supersource.Cli -c Release -- cat tests/fixtures/vec.ss
```

Read [DOCS.md](DOCS.md) for the model, application boundaries, APIs, file format and CLI commands,
and [VOCABULARY.md](VOCABULARY.md) for what each of its words means.

## AI skills

Repo-local skills are in `.agents/skills/`:

- [supersource-inspect](.agents/skills/supersource-inspect/SKILL.md): find nodes, follow references,
  inspect schemas, and diagnose structural or dependency issues.
- [supersource-edit](.agents/skills/supersource-edit/SKILL.md): edit nodes, payloads and references
  through checked JSON batches with a preview.
- [supersource-maintain](.agents/skills/supersource-maintain/SKILL.md): compare versions, merge,
  check round trips and upgrade the storage format.

Each skill includes command examples and explains the meaning of findings and exit codes.
Agents without automatic skill discovery can read the linked `SKILL.md` directly.

## Repository

- `src/`: the library and the CLI.
- `tests/`: structural checks and reading without domain kind libraries.
- `tests/fixtures/`: externally authored source files.
