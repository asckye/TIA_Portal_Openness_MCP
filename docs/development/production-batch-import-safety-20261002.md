# V20/V21 production batch import safety correction

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Status: source correction and isolated offline tests only. Production Windows SDK build and native acceptance are pending. No native import, compile, save, download, or live-project operation was executed for this change. Existing source provenance and licensing in `NOTICE.md` and `LICENSE` remain unchanged.

## Corrected behavior

- `Portal.ImportBlocksFromDirectory` now passes `overwrite ? ImportOptions.Override : ImportOptions.None` directly to the native block importer. The previous filename-based `Find` check and swallowed lookup failure could fall through to `Override` even with `overwrite=false`. Filenames remain the regex-filter input, not a substitute for native XML identity checks. `overwrite=true` remains the default; `false` does not rename.
- `ImportPlcProgramFromDirectory` no longer silently chooses the shallowest file for duplicate case-insensitive kind/name candidates. It rejects the entire selected batch before any import or compile, also returning failure diagnostics during dry-run. Different kinds retain separate namespaces. The selected files are deterministically ordered before conflict analysis.
- Conflict diagnostics contain source-root-relative paths, at most 16 conflict groups and 8 paths per group, plus full candidate counts and truncation indicators. Display values replace control characters with spaces and retain at most 256 characters, followed by a truncation suffix when needed. This bounds the new conflict diagnostics; existing discovery lists and ordinary import error responses retain their prior response contract.
- Tool descriptions and result metadata state that ordering is types-first lexical scheduling, **not dependency resolution**. Existing phases remain types, tag tables, technology objects, blocks. Blocks retain GlobalDB / FC+FB / InstanceDB / OB / other subtype priorities. Ordinal tie-breakers make lexical ordering deterministic. Neither UDT dependency graphs nor FB dependencies are resolved.

## Scope and existing limits

The signatures, defaults, filename regex behavior, response types, native call targets, and existing session prerequisites are unchanged. Both production V20/V21 projects include these sources through their SDK default compile items. The block-directory wrapper delegates to `Portal.ImportBlocksFromDirectory`; the mixed-program method invokes `Portal.ImportType`, `ImportPlcTagTable`, `ImportTechnologyObject`, `ImportBlock`, and optionally `CompileSoftware`. CLI/probe callers reuse these same methods. Portal project/software/group checks remain in place. No legacy adapter, worker protocol, version-policy gate, or batch-export code is changed.

This is a fail-closed correction for duplicate **classified candidates**, not an XML validator. The existing classifier reads only the first supported root object in each XML, falls back to filename when its Name is absent, and skips unknown/malformed XML. Multi-object XML and XML changing between scan/import are not covered by a full identity preflight. The mixed-program API still has no overwrite parameter, and its existing native import methods may overwrite. Non-conflicting imports can partially modify a project before a later failure; no batch transaction or rollback is added. The narrower `ImportBlocksFromDirectory` function is not given mixed-program duplicate preflight by this change.

## Official API evidence

Siemens' [V20 importing configuration data](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/overview/importing-configuration-data) and [V21 importing configuration data](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/overview/importing-configuration-data), checked 2026-10-02, describe `None` as non-overwriting import that interrupts with an exception on an existing object, while `Override` permits overwriting. The [V21 block import page](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/importing-block) documents the block-composition XML import call.

## Verification

Passed:

- `python scripts/checks/Test-ImportSelectionSources.py`: 6 source-wiring tests covering native option selection, early rejection before all native actions and dry-run, relative bounded diagnostics, preserved filter/defaults, truthful deterministic ordering, and both production project inclusion paths.
- `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.ImportSelectionTests -c Release`: 10 pure C# policy checks, no Siemens references or native calls. Covers empty input, kind namespaces, case-insensitive XML identity, all sampled duplicate paths, deterministic permutation, compound-key delimiter safety, filename/identity separation, bounded full-count diagnostics, and ordering wording.
- Targeted `git diff --check` for modified production files.

Not run: `scripts/build/Build-Release.ps1`, V20/V21 Siemens SDK builds, native MCP invocation, native read-back/compile validation. These remain release gates; offline source tests do not establish runtime acceptance.

On an authorized Windows test project, verify both V20 and V21: XML filename differs from existing XML block name; `overwrite=false` refuses and read-back remains unchanged; `overwrite=true` follows explicit override; same-kind/same-name duplicate candidates in different directories reject before any write, while a type and a block sharing a name remain separate namespaces; case-only duplicates reject; narrowed regex selects one valid candidate; dry-run duplicates make no native calls; unique inputs preserve phase/subtype lexical order; dependent UDTs are not advertised as automatically resolved.
