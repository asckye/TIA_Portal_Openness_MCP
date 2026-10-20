# Current development handoff

Status: Current · Last checked: 2026-10-20, master `b57b5b9a`

The published release is [v3.3.0](../releases/v3.3.0.md); 4.0 is unreleased. Release and acceptance conditions are recorded in [ADR 0013](decisions/0013-release-4-0-after-phases-7-and-8.md) and [ADR 0020](decisions/0020-prereleases-tested-by-users.md): we do no real-machine acceptance ourselves, and 4.0 goes to other users as GitHub pre-releases (`v4.0.0-beta.N`, not marked latest) once P8-04j2 is merged. Use the [refactor plan](refactor-plan.md) as the task-status authority. This handoff records source facts, not new acceptance results.

## Current source

All eight releases use FoundationHost and one worker per MCP session ([ADR 0005](decisions/0005-v20-v21-on-foundation-host.md), [ADR 0007](decisions/0007-one-worker-per-mcp-session.md)). V14 SP1–V19 use PLC workers. V20/V21 default to engine workers until P8-04j2 switches them to the PlcWorker. That path already runs behind `--plc-worker` (`f530bc78`) and passes the same suites and check-all jobs as the engine path (P8-04k2 `71310c81`, P8-04k3a `c7ba9093`, P8-04k3b `9e7e3f57`). The engine MCP host and its isolation switches are retired. All releases share category-based timeout budgets (`3bfd5806`; [ADR 0008](decisions/0008-worker-timeouts-and-faults.md)). The MCP entry is `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`.

All 35 port families (batches B1–B12) are merged and their V20/V21 engine implementations are deleted. The API recheck (`ee2482ac`) determines whole-tool and action availability. The 2026-10-19 official-API audit is fixed in batches: P8-81a `6b8cf3e8` and P8-81b `ac5e9eac` are merged, and P8-81c–g are pending. Shared implementations take the audited ToolMetadata classification on all eight releases, including batch admission (`9e7e3f57`). Consult the [generated catalog](../reference/version-tool-catalog.md), [port plan](phase8-port-plan.md) and [ADR 0012](decisions/0012-old-release-tool-registration.md). Tool counts follow API support ([ADR 0014](decisions/0014-tool-counts-follow-official-api.md)).

The Workbench control tools and Engineering/operator session path are implemented through version control and prefill (`846feba4`, `c7e87507`, `9e6079d9`). The Engineering page shares tools, approvals and audit with the selected MCP session. Old bridge code remains pending its removal task; its presence does not describe the current page's call path.

Standard-package models, planning, package management, staged execution, the write fence, project rebinding and generation ownership records are merged (`92e8064b`, `f4aef0ff`, `bfacd772`, `b0f0eda9`, `6127d939`, `4d3818f2`). The [generation design](design/p8-31-staged-generation.md) defines preview comparison, late plan-hash binding and recovery.

Python migration batches 1, 2, 3 and 7 are merged (`9b81a301`, `2dac44cc`, `d147ca7d`, `f812fd3d`). Remaining Python checks stay active until their migration is accepted. `check-all` is available (`9bcf9c45`, `92b7e11f`) and preserves registered suite gates, preflight, workflow checks and transport/stability checks. Its timing gate is independent of functional success. It has timing-history shards, reserved sensitive lanes, job deadlines and an incremental result cache (`227e4abe`, `e3598019`, [ADR 0019](decisions/0019-incremental-check-all.md)). GitHub CI runs the foundation suite group on four runners, and one gate checks their union against the registered suite gates (`42b88f8e`).

## Contracts and acceptance

MCP writes require Workbench approval by default. Typed V4 inputs and the `schemaVersion` / `ok` / `data` / `error` / `meta` envelope replace 3.x aliases ([ADR 0001](decisions/0001-hard-cut-v4-contract.md)). D1 families remain `current / NOT RUN` until release-specific native acceptance ([ADR 0002](decisions/0002-native-behavior-by-family-acceptance.md)). Read the [real-machine ledger](../reference/real-machine-ledger.md) before making acceptance claims. Pre-release notes list what is not verified on a real TIA Portal ([ADR 0020](decisions/0020-prereleases-tested-by-users.md)).

The BICO `p2051[0]` and Unified library rename observations and native cross-reference restriction remain documented in [known limits](../troubleshooting/openness-limitations.md). Offline output and SDK/XSD checks do not prove native import compatibility.

## Continue development

Read [validation](validation.md), [runtime layout](runtime-layout.md) and the [design specification](design-spec/README.md). Editable tool examples live in `reference/tool-examples`; catalog decisions live in `reference/tool-catalog`. Run `generate-catalogs`, then `generate-tool-usage`, then `generate-tool-counts`; build before capturing contracts or responses. Run `generate-release-migration` for migration-table changes. All generators have byte-checking modes.

Use the [release workflow](release-workflow.md) for packaging. Keep generated hashes under their official writers. No build or offline result authorizes a TIA, PLC, VM or external-service connection.
