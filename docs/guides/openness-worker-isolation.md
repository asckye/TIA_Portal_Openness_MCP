# Openness workers and recovery

Every MCP session has a version-matched worker. All eight releases start `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`, and the host starts the release's PLC worker, `runtime/v<key>/worker/TiaMcp.PlcWorker.<key>.exe`. The package no longer contains the V20/V21 engine. The host owns MCP transport, authentication, approvals and diagnostics. Workers serialize native calls on their owner thread: STA for V14 SP1–V19, MTA for V20/V21.

`--isolate-openness` and `--no-isolate-openness` have been removed. Use the Workbench-generated FoundationHost command. Worker separation is part of the architecture and has no on/off switch.

## Timeouts and cancellation

The shared budget is 120 seconds by default, 300 seconds for the first attach in a worker generation, and 30 minutes for compile, transfer, import/export, archive and library updates. `--worker-timeout-config` reads category budgets; `TIA_MCP_WORKER_TIMEOUTS` overrides the file, and `--worker-timeout-seconds` sets the default. Previews and reads use the default. Cancellation before dispatch returns `CANCELLED`; after dispatch the host waits for the actual result.

A timeout, worker exit, malformed reply or loss of the TIA process invalidates the worker generation. A dispatched write may have an unknown outcome. Ending a worker does not roll back or cancel an operation TIA has received. Requests are never replayed automatically.

## Inspect an unknown result

An answered `OUTCOME_UNKNOWN` affects that call. Inspect its target before repeating it. `GetSessionState.data.sessionState.unsettled` lists its request ID, tool and UTC timestamp. Later independent calls run through normal admission and approval. `SaveProject` and `SaveProjectCopy` previews and executions carry `UNSETTLED_CALLS` while entries remain.

The host carries the legacy `meta.requiresSessionReset` field until its planned removal. Admission and the unsettled list use the final call outcome. Result mapping retains legacy flags as unknown evidence for writes until Part 8 reclassifies each producer. Reads ignore those flags. The Workbench displays the number of calls with an unknown outcome.

A genuine transport fault returns `PRECONDITION_FAILED` with `condition=worker-faulted` until automatic recovery is implemented in NO-LOCK Part 4. Follow the `RestartOpennessWorker` instruction and bind the intended target explicitly. The existing lease and disconnect rules remain through Part 1 and change in Parts 2 and 3. Keep diagnostics and inspect TIA before repeating a write.

The accepted design and implementation stages are recorded in [ADR 0034](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/standards/decisions/0034-no-session-lock.md).
