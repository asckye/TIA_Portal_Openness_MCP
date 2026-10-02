# Bounded special-object XML exports (source candidate)

This slice adds `ExportPlcWatchTable` and `ExportTechnologyObject`. It is not
native certification, full backup/restore, or support for every TO type/version.
No TIA process or Siemens API was executed during offline checks.

## Evidence and release boundaries

The local official Openness manuals provide concrete typed method evidence:

- V16 §8.5 watch-table export example (text line 43014), and §8.5.3.3
  “Exporting technology objects” (pp. 894–895, text lines 46799 onward).
- V17 watch-table example (text line 53495), and §6.4.2.3 (p. 1049 onward).
- V18 German watch-table example (text line 58305), and technology-object
  export example (text line 59928).
- V19 watch-table example (text line 72494), and §6.4.3.3 (pp. 1406–1407).

These document `PlcWatchTable.Export(FileInfo, ExportOptions.None)` and
`TechnologicalInstanceDB.Export(FileInfo, ExportOptions)`. Technology objects
must be consistent and the PLC offline. Only supported TO types and versions
can export; the adapter does not compile, repair or upgrade an object.

Release eligibility is explicit, separate from exact method presence:

- Watch native source candidate: V16–V21. V15.1 is signature-verified but
  preview-only until release-specific export semantics are reconciled. V14 SP1
  lacks both the watch type and the navigation property (verified static SDK
  metadata); no reflection fallback.
- TO native source candidate: V16–V20. V14 SP1, V15.1 and V21 are
  signature-verified but preview-only (`semantics-unverified`); this does not
  falsely claim that the Export method is absent. V21's overview lists export,
  but the detailed per-release export chapter was unavailable during this pass.
- `MethodEvidence` reports static SDK signature verification;
  `SemanticsEvidence` independently reports manual source eligibility. Neither
  reports SDK compilation, native runtime success or supported TO type/version.

Additional official sources inspected 2026-10-02:

- [V20 watch export](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-watch-force-table)
- [V21 watch export, German](https://docs.tia.siemens.cloud/r/de-de/v21/tia-portal-openness-api-fur-die-automatisierung-von-engineering-workflows/export/import/daten-eines-plc-gerats-importieren/exportieren/bausteine/beobachtungs-und-forcetabelle-exportieren/importieren)
- [V20 TO export, Spanish](https://docs.tia.siemens.cloud/r/es-es/v20/objetos-tecnologicos/exportacion-de-objetos-tecnologicos): supported types/versions, consistency and offline requirements, None option.
- [V21 TO overview](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/overview-of-functions-for-technology-objects): export listed, detailed semantics remain open.

Read-only desktop PE/XML evidence independently verifies the exact public
instance `Export(FileInfo, ExportOptions)` signature and `IsConsistent` for
watch V15.1–21 and TO all eight releases. TO `IsConsistent` and
`IsKnowHowProtected` are inherited from PlcBlock through InstanceDB/DataBlock;
watch has no know-how-protection member. Evidence JSON SHA-256:
`1baa9855a5b0d0c8dd5e025f258a0cd16c4c2f876ad83d2002e3d8a17b00b47f`.
V20 main DLL SHA-256:
`7b6adb0a65ef6471ef9907433bc2fe024ac8a02bfee1b6861a2d12be8d349d6f`;
V21 Step7 DLL SHA-256:
`dfbbe3863005fa2fbc8cb553a3e7e4612acbaca19124939eb8d29de24c814eba`.
Static metadata does not load or execute the Siemens API.

The existing production helper's generic export dispatch is useful precedent,
but is not substituted for exact release method evidence or SDK compilation.
Each release still requires its genuine matching Siemens SDK compilation.

## Contract

- Exact existing software and URI-escaped object paths from the bounded read
  inventories. Watch tables include root/user groups; force tables excluded.
  TOs are root-only before V19 and include user groups from V19 onward.
  Sub-level TOs, software units and special ownership are outside this slice.
- Preview defaults true and invokes no Export callback, creates no file or
  directory and performs no online/offline transition. It validates destination
  and reports a complete single-object plan or rejects ambiguous/broken reads.
- Canonical absolute `.xml` output in an existing directory; no existing file,
  directory, symlink or reparse-point ancestry. ADS and reserved Windows
  device names are refused. The reviewed sibling-staging
  publisher does non-replacing Windows rename and preserves failure evidence.
- Apply requires confirm=true, exact expected project identity and the current
  expectedPlanHash. The length-prefixed SHA-256 plan binds release, project,
  software/object paths, output, scope, consistency/evidence and export option.
- Existing scoped target-offline guard runs before publication and immediately
  before native export. Unknown target/provider state fails closed. No switch
  to online, snapshots, compilation, download, or change to object fields.
- Watch consistency is checked before preview and again before export. TO
  consistency and inherited know-how protection are checked; protected TOs are
  outside this scope, with no protection changes or guessed watch property.
- Fixed ExportOptions.None: XML is passed through unchanged, including numbers
  and symbols. No defaults/read-only additions, XML rewriting or snapshots.
- First uncertain failure returns failed + RequiresSessionReset with only
  allowlisted evidence, and poisons subsequent session execution. No automatic
  retry, cleanup of retained evidence, worker Exit, or TIA Dispose request.
- Linux publisher always refuses before file creation/native callback. Windows
  positive filesystem checks are explicitly skipped on Linux.

## Integration and validation

The dedicated policy, typed facade, host response/request contract, host tools,
worker allowlist, transport exact-request checks, fail-stop state, adapter source
whitelist, exact release feature constants, and test invocation are registered.
The original foundation project has the same compile gate; genuine production
workers consume the explicit per-release Adapter projects and source whitelist.

The integrated .NET 8 policy/contract/host suite passes 2985 checks on Linux,
with zero failures and four explicit Windows/native skips (one for this slice).
Checks include MCP confirmation/project/hash preflight, exact response identity,
unknown-outcome poisoning without retry, zero-write preview, collision and
canonical-path rejection, symlink ancestry and dangling destinations, and the
Linux publication guard. Native filesystem positive checks remain Windows-only.
This does not compile the typed Siemens facade or establish native acceptance.
The final suite must be rerun after release evidence or source changes.
