# Master consolidation validation — 2026-10-02

The maintainer authorized merging the current development work into `master`, retaining only that branch, and running the complete local build workflow. The source baseline is `41dcc91ed4e88968cc1bf01e37fa3ff384baf366`; this consolidation changes CI, branch policy, documentation, and generated validation records. Exact engine compiler inputs and output hashes are recorded in [release-build.json](../../manifest/release-build.json).

## Actual local validation

`scripts/build/Build-Release.ps1` completed against the local V20 and V21 PublicAPI directories, with .NET SDK 10.0.401, Python 3.12, and the default 50 stability rounds. No validation thresholds were relaxed and no source hashes were manually substituted.

- Offline suite: 3035 passed; V20 identity: 3035 passed; zero failures and zero skipped tests.
- SDK version-policy suite: 10 passed.
- Both complete engines built, including native-call instrumentation and JIT checks. Compiler warnings remain.
- HTTP, HMI, resource discovery, worker supervision/isolation, external exports, migration, ecosystem tools, and delivery checks completed through the existing workflow.
- Local stability: 17936 actual tool calls across V20/V21, STDIO/HTTP, full/lite, and ordinary/isolated modes; HTTP concurrency 8.
- Configurator build/tests and strict bundle validation completed through `Prepare-Delivery.ps1`; their exact inputs and results are recorded in [configurator-build.json](../../manifest/configurator-build.json) and [delivery.json](../../manifest/delivery.json).

| Engine | Official API checks | Ecosystem assembly checks | Ordinary stability calls | Isolated stability calls |
|---|---:|---:|---:|---:|
| V20 | 2840 | 31 | 4484 | 4484 |
| V21 | 3126 | 31 | 4484 | 4484 |

The first build attempt stopped at the ecosystem PDF test while its required Python environment was still being installed. After the project installer completed, all 31 ecosystem assembly checks passed independently, then the entire build workflow was rerun successfully. This needed no engine-code change. Dependencies were installed only into the ignored `TiaMcp_Output/ecosystem-python` environment.

Studio's earlier integration validation passed 36 client tests and 505 WPF tests. The `offline-checks` workflow now also builds Studio and executes both existing suites on Windows with .NET 10, so subsequent mainline changes exercise the integrated UI through CI.

## Branch history and remaining scope

The verified pre-cleanup Git bundle and exact historical branch heads are recorded in [version history](../reference/version-branches.md). Push uses the normal fast-forward path from the old master; branch cleanup follows successful CI. Published release tags and assets retain their existing contents. Updating local build/delivery records is not a new published release.

These checks did not start or attach to TIA, open an engineering project, operate a PLC, or establish native project acceptance. V14 SP1–V19 production routes stay disabled. The legacy migration ledger retains its partial/pending/exact-compatibility distinctions. `LOCAL_CODEX_HANDOFF.md` remains an untracked local handoff, unchanged.
