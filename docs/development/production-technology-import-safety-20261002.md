# Production technology-object import safety fix (2026-10-02)

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Source-only change. No Siemens assemblies were executed, no TIA process was connected, and no project/import was opened or mutated. No commit or push was made.

## Defect and bounded fix

`Portal.ImportTechnologyObjectsFromDirectory(..., overwrite = true)` previously ignored its overwrite argument. It called the three-argument single-import entry point, whose shared reflection helper unconditionally passed `ImportOptions.Override`.

The public three-argument single-import entry point and the batch default remain unchanged. A technology-specific private overload now carries the explicit overwrite value to a separate overload of the reflection helper. It resolves exactly `Import(FileInfo, ImportOptions)` and invokes it once, passing `None` for false and `Override` for true. It never tries an option-less native method, including when the explicit method is missing or throws. The existing shared helper and all unrelated import families are unchanged.

Technology import returns are enumerated to retain every readable native object name, rather than fabricating identity from the source filename. If native invocation or post-native result/identity enumeration fails, the error states that the project may have changed and that no retry occurred. Already-read returned names remain in the batch response and are included in the error for the failing file. Null, empty, non-enumerable, and unreadable-identity results are treated as uncertain failure, without a rollback claim. A batch stops after its first failed file and explicitly reports that later files were not attempted. Selected paths are materialized and deterministically ordered before the first import.

This remains the existing batch response contract; it does not add transactional guarantees, an exact not-attempted-file count, or a new identity schema. Names are returned identities, not proof that each object remains committed after an exception.

## Static SDK evidence

Parent-supplied exact metadata inspection across V14 SP1, V15.1, V16, V17, V18, V19, V20, and V21 found the technology-composition `Import` absent in V14 SP1/V15.1 and present in V16–V21 as `IList<TechnologicalInstanceDB> Import(FileInfo path, ImportOptions options)`. `None = 0` is documented as throwing if an object exists; `Override = 1` overrides existing objects. This supports the explicit option and enumerable-return contract without claiming runtime acceptance.

Evidence checksum: `218064feefe8607eca95ec414ecde65b282a946a3ef1790e637831801b47abaa`.

## Verification

Run `python3 scripts/checks/Test-TechnologyImportSafety.py`. The check extracts the actual production helper into a temporary .NET 8 harness with fake collections and a fake enum; it does not load a Siemens SDK. It verifies false/true option mapping, exactly one invocation, rejection of an option-less method, missing-file preflight, all returned identities, native failure, null/empty/string results, unreadable/blank names, partial enumeration evidence, and source wiring. Result: 25 fake-collection checks and the source wiring guards passed.

`git diff --check` passed. A production Windows/Siemens build and real import behavior remain unverified.

## Separate risk left unchanged

The older helper itself does not invoke an alternate overload after native entry. However, `Portal.Software.HmiExchange.cs` has higher-level failure-driven import retries (for example an import after stripping unsupported attributes). Those are separate import families and were not changed by this technology-only fix.
