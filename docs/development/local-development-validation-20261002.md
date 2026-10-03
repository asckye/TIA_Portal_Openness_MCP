# Local batch-import hardening and Windows compile-only validation

Date: 2026-10-02, America/Los_Angeles (2026-10-03 UTC).

## Source identity and scope

This is a local development increment in `D:\Project\TIA_Portal_MCP` on
`offline/publicapi-validation`, based on HEAD
`db2039e09be6e21b37cff4cbc719162aa502b212` plus the uncommitted changes described
below. It is not a validation of an unchanged published commit.

The validation source manifest contains 843 tracked/new source and build-input
files from the MCP source/tests, the two full-engine source dependencies, the
document safety check and `.gitignore`. All file hashes remained unchanged
through compilation. SHA256 of the UTF-8 source-manifest JSON:
`2724779178d993c61564c9646598b46155e3644dca5ef37d5e7188211523ec60`.
This is a local evidence identifier, not a replacement release-gate hash.

The original untracked `LOCAL_CODEX_HANDOFF.md` was preserved. The user restored
all eight PublicAPI trees. Static file metadata and hashes were recorded for
39 DLLs in the selected exact-release directories. No PublicAPI DLL was copied
into source/build output or added to Git. The root PublicAPI ignore rule now
covers the earlier six releases as well as V20/V21.

## Changes and independent source review

The independent bounded batch-document-import wiring review found one P2 issue:
the host refused all subsequent requests after an uncertain document batch, but
the raw worker still allowed read-only operations and dry-run previews. Such a
read could reach native process/project APIs after the candidate had required a
whole-session reset.

`WorkerSessionOutcomeState` now enforces a monotonic whole-session refusal for an
uncertain `PlcBatchDocumentImportResult`. The actual worker calls its guard before
project identity validation or reflected facade invocation. Older XML batches
and the single-document candidate retain their previous mutation-only refusal
policy; this increment does not broaden their contracts.

The independent reviewer checked the staged helper, actual worker wiring,
explicit worker source allowlist, managed test inclusion and source-wiring guard.
No additional actionable issue was found in the bounded manifest registration,
None-only native import closures, retained file ownership, preview binding,
inventory evolution, failure stopping, host refusal or disabled production gates.
The reviewed staged files matched the files applied to the repository. This
closes the earlier incomplete **source-wiring review** for this local increment;
the historical automated block remains recorded. It is not independent native
acceptance or a claim that the earlier blocked review ran successfully.

The Windows checks also exposed two portability defects: the Python source guard
decoded UTF-8 C# using the machine's GBK default, and the document tests supplied
hard-coded POSIX paths where the policy compares exact `Path.Combine` results.
The guard now declares UTF-8, and both fake document fixtures use a temporary
root and platform path combination. Negative Win32 path examples and all injected
native-failure, cleanup, collision and inventory scenarios were preserved.

The SCL guide no longer advises routine deletion before external-source
generation. Official [V20 generation documentation](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/generating-blocks-from-source)
and [V21 generation documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/generating-blocks-from-source)
describe replacement of existing blocks, ASCII sources and an offline PLC.
The correction is scoped to these documented releases and retains fresh content
readback/fingerprint verification. It establishes no missing CreateFromFile
duplicate-name, normalization or race semantics.

## Managed and repository checks

| Check | Result |
|---|---|
| Windows LegacyHost managed suite | 4,103 passed, 0 failed, 1 explicit skip |
| Document import source-wiring guard | 8 passed |
| Repository Markdown/entrypoint check, `--no-binaries` | 276 documents, 0 issues including this record |
| Registered tool references | PASS; 486 registered tools |
| `git diff --check` | PASS |
| PublicAPI Git exclusion | All eight exact-release roots ignored |

The managed suite executes fake callbacks and pure policies, plus static MSBuild
item evaluation. It does not start a worker or load Siemens assemblies. The
explicit skip preserves the lack of native batch-import/read-lock acceptance.
Worker guard tests verify rejection before callbacks, permanent refusal within a
session and preservation of the older XML read-inspection policy. The Python
guard verifies that the tested helper is wired into the actual worker.

## Exact-release compile-only results

Toolchain: .NET SDK 10.0.401 / MSBuild 18.9.11. Builds use
`DesignTimeBuild=true`, `SkipCompilerExecution=false`,
`BuildProjectReferences=true`, `UseSharedCompilation=false` and
`SiemensEngineeringCopyLocal=false`. The older three targets restore genuine
`Microsoft.NETFramework.ReferenceAssemblies.net461` 1.0.3; net48 is not used as a
substitute. Each worker build recorded actual Csc commands for both its adapter
and worker. Each engine build recorded actual Csc for its source dependency and
the full engine. No weaver execution was recorded.

| Exact release | Framework | Adapter + Worker | Build-log warnings | Errors |
|---|---|---|---|---|
| 14sp1 | net461 | PASS | 13 | 0 |
| 15.1 | net461 | PASS | 13 | 0 |
| 16 | net461 | PASS | 13 | 0 |
| 17 | net48 | PASS | 13 | 0 |
| 18 | net48 | PASS | 13 | 0 |
| 19 | net48 | PASS | 9 | 0 |
| 20 | net48 | PASS | 5 | 0 |
| 21 | net48 | PASS | 5 | 0 |
| Full V20 engine | net48 | PASS | 23 | 0 |
| Full V21 engine | net48 | PASS | 21 | 0 |

Warnings are per build log and include dependencies, not deduplicated defects.
Generated adapter/worker binaries remain uninstrumented compile-only evidence.
Full-engine binaries from this run are also uninstrumented; none were executed
or copied into delivery `runtime/` directories.

Independent static PE inspection passed all eight targets. Each worker contains
the actual IL bodies of `WorkerSessionOutcomeState.RequireUsable(bool)` and
`MarkUncertain(bool)`, has the required AMD64/net461 or net48 identity and
references exactly its selected adapter. The worker-local adapter copy matches
the built adapter hash. Adapter Siemens identities match both `Release.props`
and the supplied exact-release PublicAPI metadata. The result JSON SHA256 is
`5024149fde50c22f559f5f9d5975eeca4466b8164240321d5582f1311a622d1e`.
The inspector reads metadata/IL bytes without loading, JIT-compiling or executing
the inspected worker, adapter or Siemens assemblies. These checks do not prove
runtime isolation or native behavior.

## Evidence and remaining work

Machine-local validation logs and source/API manifests are kept in the current
Codex workspace's `work/` directory. The user-facing evidence archive contains
logs, hashes and the pure metadata inspector only, never PublicAPI DLLs,
engineering projects or produced engine/worker/adapter binaries.

The 62-tool ledger is unchanged: 55 implementations/candidates comprising
12 scoped source/offline/manual closures and 43 partial implementations; seven
tools remain pending/unimplemented, with seven exactly compatible migrations.
V14 SP1 through V19 production runtime entries remain disabled. External-source
mutation remains apply-blocked until its missing exact-release semantics and
native evidence are established.

No TIA startup/attachment, engineering project opening, PLC operation, Openness
membership repair, firewall/certificate change, native acceptance, weaving,
reflection/JIT acceptance, `Build-Release.ps1`, strict release gate, release packaging,
deployment, commit, push or PR was performed. Existing generated release manifests
and their hashes were not changed. The strict release-gate hashes remain stale
for this local source increment.

Next development can use the restored exact-release references, while the seven
pending migrations and native acceptance remain separate evidence tasks. The
bounded batch source-review gap has been addressed; current compile-only results
must not enable legacy runtime entries or promote feature/acceptance counts.
