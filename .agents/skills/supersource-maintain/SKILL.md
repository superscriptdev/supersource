---
name: supersource-maintain
description: Use the Supersource CLI to compare source versions, merge branches, check round trips and upgrade file formats while preserving source identity and reporting conflicts.
---

# Maintain Supersource files

Run from the repository containing `Supersource.slnx` with .NET 10:

```sh
dotnet run --project src/Supersource.Cli -c Release -- --help
```

`dotnet run` builds before invoking the CLI. Arguments after `--` go to the CLI. From another
directory, use absolute project and input paths. For repeated commands, add `--no-build` before
`--` only after a successful Release build and while the code remains unchanged. Use `--help`
for syntax and [DOCS.md](../../../DOCS.md) for persistence and merge semantics.

Choose the operation the user requested. Inspection alone does not call for upgrading or
merging. User authorization for an operation is sufficient; do not add a separate confirmation
step for ordinary local work already in scope.

For node, payload or reference edits, use [supersource-edit](../supersource-edit/SKILL.md).

## Compare versions

```sh
dotnet run --project src/Supersource.Cli -c Release -- diff before.ss after.ss
```

Inputs must be states of the same source. Similar names or content do not establish shared
identity. Exit 0 means the comparison ran; it can include differences. This command reports
operations but does not produce an executable patch or edit either input.

## Merge versions

```sh
dotnet run --project src/Supersource.Cli -c Release -- merge base.ss ours.ss theirs.ss merged.ss
dotnet run --project src/Supersource.Cli -c Release -- validate merged.ss
dotnet run --project src/Supersource.Cli -c Release -- rules merged.ss
```

Use the actual common base and confirm the intended ours/theirs ordering from the task.
Write to a distinct output path, keeping the three input files available for comparison. The
CLI writes its output path directly, so use a fresh path unless replacing that output is intended.

A contested merge selects ours and writes conflict records. Exit 3 can mean that an output was
written with conflicts, or that incompatible source identities prevented a merge. Read the
output and check the result file rather than treating every nonzero exit as “nothing happened”.
Report contested decisions; a readable or structurally valid result can still require domain
review. New nodes from theirs can be renumbered, so inspect IDs in the merged output before
following up on them.

## Check read/write preservation

```sh
dotnet run --project src/Supersource.Cli -c Release -- roundtrip source.ss
```

This leaves the original unchanged but writes and removes a sibling named `source.ss.roundtrip`.
Run it on a temporary copy if that sibling already exists or the source directory is read-only.
An ordinary file is checked byte-for-byte. For journalled files, the current CLI reports folding
and checks the node count; that result alone is not a complete semantic-equivalence check.

## Upgrade the storage format

```sh
dotnet run --project src/Supersource.Cli -c Release -- upgrade working-copy.ss
dotnet run --project src/Supersource.Cli -c Release -- validate working-copy.ss
dotnet run --project src/Supersource.Cli -c Release -- verify working-copy.ss
```

The command is `upgrade`, not `migrate`. It rewrites its input in place and accepts no options:
there is no `--dry-run`. For a preview, copy the input to a temporary directory and upgrade the
copy. Keep a recoverable original for an in-place upgrade. The command uses a sibling named
`<file>.migrating`; if that path already exists, use a copy in a fresh directory.

Upgrading changes storage framing and may add a checksum. It does not regenerate content kinds,
run transforms or migrate an application's domain model. Use the application's tools for those
operations. Do not replace a source with a freshly authored graph as a substitute for upgrading;
that can lose node correspondence and history.

## Interpret results

- Exit 0: the command completed; inspect reported differences and findings.
- Exit 1: usage error or unknown command.
- Exit 2: unreadable/corrupt input, invalid node, or rejected upgrade arguments.
- Exit 3: failed validation/verification, a refused operation, or a contested merge. Context matters.

The output is human-readable, not a stable JSON protocol. Report the operation, paths, meaningful
findings and checks performed. For node-level investigation, use
[supersource-inspect](../supersource-inspect/SKILL.md).

The CLI reads embedded schemas without linking `Supersource.Vocabularies` or domain libraries.
Use declared payload formats and optional display hints to describe unfamiliar nodes; opaque
bytes and annotation flags do not establish their application meaning. Applied history includes
operations on generated nodes. Regeneration is an explicit application action.
