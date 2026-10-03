# Bounded batch XML exports: source candidate (2026-10-02)

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Implemented facade tools: `ExportBlocks`, `ExportTypes`. This is **not native acceptance**, not a complete eight-release SDK compile result, and not full baseline signature parity. The new scoped contract intentionally requires an exact `softwarePath` and canonical `groupPath` (empty string selects the root), an existing absolute `exportPath`, `recursive=false` by default, and `maxItems=128` (hard maximum 256). No regex selection or software-unit traversal is implied.

## Manual-first evidence and limits

Read the official Siemens V17 Openness system manual, 05/2021, section 6.4.1.13, printed pp. 998–1005: connected portal, open project and offline PLC are prerequisites; consistent blocks and user data types support XML export. Its example invokes `PlcBlock.Export(FileInfo, ExportOptions)` on a composition member, not a fictional batch SDK method. Group/composition traversal reuses the already reviewed native single-item inventory implementation. Existing release evidence and direct official V20 links are recorded under `EXPORT_BLOCK`, `EXPORT_TYPE`, `BLOCKS` and group chapters in [manual checklist](legacy-plc-manual-checklist.json). Historical typed compile evidence for both single-object signatures spans 14 SP1, 15.1, 16–21, but **changed-source exact-SDK compilation must wait for a new Windows snapshot**.

The adapter takes a complete deterministic snapshot of the selected group, or explicit recursive user-group scope, and calls each item's existing `Export(FileInfo, ExportOptions.None)` API. It does not invent a native batch API. Objects remain unchanged: no renumbering, renaming, symbol substitution, compiling, saving, or downloads. XML bytes are only parsed/hashed and atomically published by the existing publisher. Numbers/symbols present in native XML are not rewritten. ExportOptions.None can omit default-valued attributes; no stronger reconstruction promise is made. V14 SP1 SCL keeps the existing interface-only warning and cannot be described as a complete program backup. Protected/system/special blocks and special ownership are not newly certified by this change; native rejection stops the batch.

## Safety and execution

- LocalSession is refused, including preview. Exact software identity is required; aliases are not accepted. Standard target execution uses the existing positive OnlineProvider owner proof and exact Offline state, before publication and immediately before each native export. R/H, missing/ambiguous providers and unknown states fail closed. The project-wide compile gate is unchanged.
- Preview enumerates/validates only: no native export, directory creation, staging or file writes. Over-limit items or groups (1024 group ceiling), ambiguity, invalid output directories or existing destinations reject the entire plan. Inventory is never truncated and presented as complete.
- To execute, send `dryRun=false`, `confirm=true`, exact `expectedProjectFile`, and `expectedInventoryHash` from the preview. Worker project identity checks remain intact. Scope, consistency, output destinations and inventory differences invalidate the hash. Inventory hash is not a content snapshot or lock: external project changes can still happen; consistency and offline state are rechecked at the native boundary.
- Filename is `blocks-<SHA256 of full canonical object path>.xml` or `types-<hash>.xml`, never the raw PLC name. Fixed ASCII names avoid reserved Windows devices, case-only names, separators and traversal. The result maps every source path to its filename. Duplicate output names are refused even in the hypothetical hash-collision case. Existing outputs are never overwritten. Directory ancestry reparse points are refused at planning; existing publication safety/host-filesystem trust limits still apply.
- Per-item states are `planned`, `inconsistent`, `exported`, `failed`, `not-attempted`. Inconsistent items remain explicit, not silently omitted. A first failure stops later exports. Prior published outputs remain; retained staging evidence is exposed only through allowlisted fields. There is no all-or-nothing rollback, retry or deletion of evidence. `Executed` means execution was requested/entered, not that every item succeeded. Inspect Items for actual outcomes.
- A failure returns `RequiresSessionReset=true`. Host contract validation preserves the structured partial result and poisons subsequent transport operations. The worker independently rejects subsequent mutations while staying alive; it does not exit/dispose/kill TIA because of this batch result. Read-only worker diagnostics/preview remain possible on the raw worker. A new explicit inspected session is required; no automatic replay.

## Offline verification

`BatchExportTests` uses fake inventory/export/publication delegates and owned temporary directories. Tests cover deterministic complete inventory, cardinality bounds, duplicate/case/reserved/traversal-like names, preview side-effect freedom, hash mismatch/changed inventory, existing files, per-item offline calls, skipped inconsistency, partial results, no further attempt after failure, retained allowlisted evidence and malformed host outcomes. Linux verifies fail-closed publication with no callback or file effects. Windows-only cases exercise actual safe publisher success and retained native failure evidence; they are explicitly skipped on Linux. No Siemens assembly or worker process is loaded by this suite.

Current Linux aggregate after initial integration: **2046 passed, 0 failed, 2 skipped** (single-export Windows publication and new batch Windows publication groups). Re-run after subsequent changes; this number is evidence for the tested source state, not native acceptance. Production LegacyHost build also passed with zero errors and two existing TiaVersionCatalog nullable warnings. Exact-SDK compile remains deferred.

Additional MCP dispatch verification: `BatchExportDispatchTests` invokes both batch tools through `InvokeAsync`. Missing/blank hashes are blocked before the fake worker, changed hashes and wrong project identity before publication, conflicting response scope is rejected, and explicit partial outcomes remain visible. The production `WorkerOutcomeState.AcceptResult` helper is shared by WorkerClient and these fake transport tests; replay after an unknown partial outcome cannot dispatch or republish. This is offline contract evidence, not a worker/native execution.
