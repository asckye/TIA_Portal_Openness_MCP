# Production HMI screen import fail-stop fix (2026-10-02)

This is a narrow source-only safety correction, not a new HMI feature or native acceptance. No Siemens assemblies, HMI/TIA process or project were loaded or executed for these checks.

## Scope

`ImportHmiScreen` previously interpreted a native unsupported-setter error as permission to remove XML attributes and invoke the importer again, up to five times. That error does not prove rollback or absence of partial mutation. The caller now invokes its shared import helper once and preserves the original failure with a conservative possible-mutation warning and an instruction to inspect the project before another import. It creates no failure-driven rewritten XML or recovery artifact.

The existing pre-invocation screen-size guard remains unchanged. The shared reflection helper's pre-invocation overload discovery is unchanged, as are its legacy override default and return handling. The technology-object-specific overload and all other HMI families remain unchanged.

Screen directory batches stop at the first failed screen, including a pre-invocation failure. Their existing `Imported` and `Failed` response shape is preserved: prior successful file names, the failed path and original error remain available, and the failed entry says later matching files were not attempted. There is no exact unattempted-file count or new transactional guarantee. A failed import may already have changed the project. Existing success counts and fallback file names are legacy reporting, not verified native object identities or proof of committed changes. Null or empty native returns retain their pre-existing handling; tightening that separate contract is outside this replay fix.

## Verification

`python scripts/checks/Test-HmiImportSafety.py` extracts the actual production single-screen caller, directory caller and unchanged shared helper into a temporary .NET 8 fake-only program. All 20 checks passed. Coverage includes unsupported-setter and unknown native throws with exactly one invocation, no option-less fallback after a throw, unchanged input XML, zero invocations on preflight rejection/missing input, option-less pre-invocation discovery, unchanged legacy return/default behavior, preserved earlier batch successes and failure evidence, and no later batch import after failure. Source guards ensure only one helper call and no error-driven rewrite/replay in the screen caller.

`Test-TechnologyImportSafety.py` also passed all 25 checks; the original offline suite passed 3,034 checks with zero failures or skips. Dead-tool-reference and whitespace checks passed. `Check-Repository.py` reported only five missing delivery-executable entries (V20/V21 runtimes and configurator) in this source-only checkout.

Production Windows/Siemens builds, native HMI import/compile behavior and real-project acceptance are NOT RUN. This check cannot establish TIA rollback, HMI compatibility or production readiness.
