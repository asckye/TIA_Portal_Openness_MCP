# Current development handoff

## Current state

The latest published release is [v3.3.0](../releases/v3.3.0.md). The 4.0 source is
unreleased. **Maintainer decision 2026-10-09: 4.0 is not released until phases 7 and 8 are complete;** real-machine
acceptance happens once at the end on all eight releases (including the retest of findings 39-52, the old-release
enablement of ported families and each batch's V20/V21 recheck). Every merge runs all registered suites in a clean
checkout, the release-pipeline harness modes and the CI checks; a full release candidate runs only for changes to the release
chain and once at the end to build the VM test package (maintainer decision 2026-10-10). Phase 6 is merged apart from
P6-42/P6-43. In phase 7 (see the [refactor plan](refactor-plan.md)) P7-01 to P7-08, P7-07a/b/c (V20/V21 findings 37-48 and
52), P7-10 and P7-11a are merged; P7-08 retired the engine's own MCP host and isolation modes. Phase 8: port batches B1
(F01-F03), B2 (F18, F20, F21) and B3 (F08, F09, F11, F17) and the F19 pilot of the [port plan](phase8-port-plan.md) run through
the Foundation adapters and are enabled on V14 SP1-V19 where the API allows; Python batches 1-2 of the
[Python plan](phase8-python-plan.md), the TIA process lease with findings 49-51, human/AI attribution, the Workbench control
channel with its eight host tools and the standard package tools are merged. The discovery bridge and batch tools ship on all
eight releases (maintainer decision U8). Maintainer decision 2026-10-09: an old release lists a ported tool only when at
least one of its actions has API there; P8-03x applies it to the merged batches.
Current 4.0 real-machine acceptance is pending. Offline tests, SDK builds, schemas,
and static call evidence do not establish native TIA behavior.

Workbench approval is enabled by default for MCP `WRITE` and `ONLINE-WRITE` calls.
The V4 contract uses typed arguments and the `schemaVersion` / `ok` / `data` / `error` / `meta`
envelope. Direct calls, `CallTool`, and batch results preserve the same envelope.
There are no 3.x tool aliases in the 4.0 catalog. D1 behavior families remain
`current / NOT RUN` until their release-specific evidence is accepted.

The eight exact release keys are `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`,
and `21`; original V14 and V15 are excluded. V14 SP1–V19 use the Foundation host
and matching PLC worker. V20/V21 use FoundationHost with the full catalog and an engine worker in `runtime/v20/worker` / `runtime/v21/worker`. All eight MCP entries are `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`. The bundle entry is root
`TiaOpenness.exe`; Studio calls Openness directly through its eight release
adapters. Selecting a release does not upgrade a project.

## Current references

- [Version and tool scope](../reference/version-tools.md) and the [functional
  Openness coverage review](../reference/openness-coverage.md) describe advertised
  tools, release gaps, and the boundary between offline evidence and native acceptance.
- [`GetToolUsage`](official-tool-usage.md) returns the selected release's typed
  schema, examples, results, language files, and call sequences. It replaces both
  old guide/recipe entries: use `exampleKind: "sequence"` to list sequences and
  `exampleId: "sequence/<topic>"` for one sequence. Former authoring-guide topics
  use `language`, `query`, or the matching `exampleId` selector.
- [Runtime layout](runtime-layout.md) documents bundle roots, writable data,
  logs, audit records, diagnostics, and Python environment selection.
- [Validation](validation.md) lists the offline suites, SDK builds, and repository
  checks. [The real-machine ledger](../reference/real-machine-ledger.md) records
  accepted evidence by release and behavior family.

Keep the editable example source in `reference/tool-examples` and regenerate its
embedded catalog after edits. Do not add separate issue-specific example systems.
Foundation and full-engine schemas remain release-specific.

## Known limits

- Native acceptance for the 4.0 candidate remains pending; consult the ledger before
  claiming a release or behavior family has passed.
- PLC native cross-reference remains restricted. The reported `p2051[0]` BICO read
  crash and Unified library script rename crash remain unresolved native observations.
- Other shared offline builders still emit V21 candidate XML where their examples
  say so. Only UDT and GlobalDB declaration builders target all eight formats.
- Foundation binding-snapshot lifecycle integration remains separate from its
  current process/project identity checks.

## Build and release

After engine or test changes, use the authorized PublicAPI directories and run:

```powershell
dotnet run --project build-tools/release -- build-multi-version -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

Use [validation](validation.md) for the checks required by the changed paths and
[the release workflow](release-workflow.md) for package creation. Do not edit
generated manifest hashes by hand. A build does not authorize live TIA, PLC, VM,
or network access.
