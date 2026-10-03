# Production V20/V21 document-import safety patch

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

## Source-only result

The existing full-engine single/batch document import paths were narrowed in place. Public method signatures, response shapes and import-option defaults remain. No Siemens runtime, TIA project, PLC, native import, commit or push was used.

- A missing requested batch group now throws before any import instead of retargeting root. Only an empty/whitespace group path selects root. Missing PLC/directory and selection errors also surface instead of being swallowed.
- Batch selects top-level `.s7dcl` files before mutation, in deterministic ordinal filename order. This is reproducible order, not dependency resolution.
- Batch stops on the first native exception, null result, non-Success state (including PartialSuccess), missing imported-block association or association-enumeration error. There is no fallback, automatic retry or rollback.
- Previously completed items with readable response metadata remain in `Items`; a metadata readback error can leave that list incomplete while native document-set counts remain available. Failed/unknown native results retain available reported names and native messages in diagnostics, without treating those names as successful items. Diagnostic read failures are explicitly incomplete.
- Batch metadata now separates scanned, selected, attempted, succeeded document sets and not-attempted selected files. `mayHaveChanged` is true after any attempted native call. A stopped batch is not reported as success, even when earlier files succeeded. `importedBlocks` remains the returned completed-item count, not the total possible mutations. `totalBlocks` remains the legacy pre-scan value; use `selectedFiles` for actual execution selection.
- Batch native counts/diagnostics are snapshotted before metadata readback or progress notifications; failures in those reporting steps preserve that evidence and mark the response unsuccessful.
- Single-import non-Success/error text no longer claims nothing was imported. Generic recovery hints that could instruct a retry are removed from document-import error wrappers.

## Explicit Override feature retained

The legacy single-import number-restoration feature remains after native Success, but only when the Override flag is set. It previously captured and restored numbers under other options too. This feature performs extra Number/AutoNumber writes and is not rollback, attribute-preservation proof or lossless roundtrip. Its existing best-effort warning-only behavior is retained; broader removal or a separate explicit preservation parameter needs a distinct compatibility decision. The narrow LegacyHost candidate should not copy it.

## SDK evidence and limits

Parent-supplied exact V20/V21 SDK XML evidence reports None=0 as “Throw if exists”, Override=1 as “Override existing”, SkipInactiveCultures=2 and ActivateInactiveCultures=4. This establishes documented collision intent, not matching/timing/rollback or zero-mutation guarantees. DocumentResultState includes Success, PartialSuccess and Failure. ImportedPlcBlocks is a read-only association. Evidence artifact SHA-256: `3f11b56aae6452b6834de6090c81704e09749353e8446e98ed521f170beecbb8`.

These changes do not establish content completeness, native transactionality, import identity from basenames, byte stability, collision inventory, concurrent session safety, runtime update compatibility or a no-overwrite preview/apply protocol. Existing `preservePath` is still an unused compatibility parameter. Success is the native state plus readable association, not native acceptance or content verification.

## Validation

- `python scripts/checks/Test-DocumentImportSafetySources.py`: 7 static production wiring guards passed.
- `python scripts/checks/Test-ImportSelectionSources.py`: 6 existing unrelated import-selection guards passed.
- `git diff --check`: passed.

These guards inspect source and do not execute importer behavior. A full production build and Build-Release were not run here; no claim of SDK compilation or Siemens native acceptance is made. Before any commit/release, run the repository-required build gates in an appropriately provisioned environment. No runtime/project action is authorized by this note.
