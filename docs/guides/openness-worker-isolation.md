# Openness workers and recovery

Every MCP session has a version-matched worker. All eight releases start `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`; V14 SP1–V19 use a PLC worker and V20/V21 use an engine worker. The host owns MCP transport, authentication, approvals and diagnostics. Workers serialize native calls on their owner thread: STA for V14 SP1–V19, MTA for V20/V21.

`--isolate-openness` and `--no-isolate-openness` have been removed. Use the Workbench-generated FoundationHost command. Worker separation is part of the architecture and has no on/off switch.

## Timeouts and cancellation

The shared budget is 120 seconds by default, 300 seconds for the first attach in a worker generation, and 30 minutes for compile, transfer, import/export, archive and library updates. `--worker-timeout-config` reads category budgets; `TIA_MCP_WORKER_TIMEOUTS` overrides the file, and `--worker-timeout-seconds` sets the default. Previews and reads use the default. Cancellation before dispatch returns `CANCELLED`; after dispatch the host waits for the actual result.

A timeout, worker exit, malformed reply or loss of the TIA process invalidates the worker generation. A dispatched write may have an unknown outcome. Ending a worker does not roll back or cancel an operation TIA has received. Requests are never replayed automatically.

## Inspect and recover

Use `GetToolUsage` for the selected release's status and recovery tools. `GetOpennessWorkerStatus` and `RestartOpennessWorker` are registered on all eight releases; inspect their release-specific schema and result. Controls affect only the calling session. The recovery route through `DisconnectPortal` applies on every release.

1. Inspect TIA, the actual project result and the native invocation log before repeating a write.
2. Call `DisconnectPortal`. An idle worker detaches without saving or closing the project. A busy, timed-out or lost worker is ended and the reply requires restarting TIA.
3. If the TIA lease is uncertain, inspect and save as needed, then restart that TIA instance. Do not remove its lease file to bypass the refusal.
4. Start a new MCP session, connect to the intended TIA process and explicitly attach the exact project file. Read back session state before continuing.

Alternatively, explicitly restart the worker with `RestartOpennessWorker`. An idle worker detaches first; a faulted worker preserves the uncertain lease and reports `requiresTiaRestart`. Old export handles and preview plans are invalid after reset.

Each live HTTP session owns a separate worker. Separate sessions must use separate TIA instances for concurrent engineering work. This lease does not coordinate arbitrary third-party Openness applications or GUI edits.

## Diagnostics and acceptance

The host remains available for diagnostics when TIA or its Openness environment is unavailable. `InitializeEnvironment` reports repair steps; TIA-dependent operations are refused before native dispatch. Read the selected release's schema rather than assuming every diagnostic tool exists on every release.

[Native-call diagnostics](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/native-call-diagnostics.md) explain the journal. Review private paths before sharing logs. Offline worker and transport fixtures establish their tested protocol behavior; current native acceptance is recorded in [capabilities](../reference/capabilities.md).
