# Supersource

Supersource is a file format for structured data and the relationships within it. It gives
applications a common way to store content, identify its parts and link them, so tools can
inspect the structure without a separate parser for each content type.

A source is a graph of nodes with kinds, payloads, ordered children and references. Applications
choose what those nodes represent: a document, dataset, scene, program or a combination of
content types. Related content can share a source or link across separate sources.

Node identity survives changes to names, content and position. Embedded schemas let generic
tools inspect and compare unfamiliar kinds; applications supply their meaning and behaviour,
such as rendering an image or executing a function.

This repository provides the .NET core library and CLI, including memory-mapped payloads,
incremental saves when edits can be appended, stored history, structural diff and three-way
merge.

The core reads and preserves unfamiliar content using its embedded schema. Optional display
hints help a reader present it; opaque payloads remain valid content even when no reader can
interpret their internals. Applications decide how content is displayed, edited or executed.

`Supersource.Vocabularies` is an optional companion library in this repo. It supplies properties,
tags, spans, embeds and derivation helpers. Reference it and install the vocabularies you use;
core readers and the CLI require none of them.

## Getting started

Requires .NET 10. From this directory:

```sh
dotnet build Supersource.slnx
dotnet test Supersource.slnx
dotnet run --project src/Supersource.Cli -c Release -- cat tests/fixtures/vec.ss
```

Read [DOCS.md](DOCS.md) for the model, application boundaries, APIs, file format and CLI commands.

## AI skills

Repo-local skills are in `.agents/skills/`:

- [supersource-inspect](.agents/skills/supersource-inspect/SKILL.md): find nodes, follow references,
  inspect history and schemas, and diagnose structural or dependency issues.
- [supersource-edit](.agents/skills/supersource-edit/SKILL.md): edit nodes, payloads and references
  through checked JSON batches with preview and history.
- [supersource-maintain](.agents/skills/supersource-maintain/SKILL.md): compare versions, merge,
  check round trips and upgrade the storage format.

Each skill includes command examples and explains the meaning of findings and exit codes.
Agents without automatic skill discovery can read the linked `SKILL.md` directly.

## Repository

- `src/`: core library, optional vocabularies and CLI.
- `tests/`: structural checks and reading without domain kind libraries.
- `tests/fixtures/`: externally authored source files.
