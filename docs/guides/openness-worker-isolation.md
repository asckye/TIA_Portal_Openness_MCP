# Openness workers and recovery

Every MCP session has a version-matched worker. All eight releases start `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>`, and the host starts the release's PLC worker, `runtime/v<key>/worker/TiaMcp.PlcWorker.<key>.exe`. The package no longer contains the V20/V21 engine. The host owns MCP transport, authentication, approvals and diagnostics. Workers serialize native calls on their owner thread: STA for V14 SP1–V19, MTA for V20/V21.

`--isolate-openness` and `--no-isolate-openness` have been removed. Use the Workbench-generated FoundationHost command. Worker separation is part of the architecture and has no on/off switch.

## Timeouts and cancellation

The shared budget is 120 seconds by default, 300 seconds for the first attach in a worker generation, and 30 minutes for compile, transfer, import/export, archive and library updates. `--worker-timeout-config` reads category budgets; `TIA_MCP_WORKER_TIMEOUTS` overrides the file, and `--worker-timeout-seconds` sets the default. Previews and reads use the default. Cancellation before dispatch returns `CANCELLED`. A dispatched call continues to its actual result. When its wait budget ends, MCP returns `in-progress` with `OPERATION_IN_PROGRESS`, `ok=false` and the `activeRequestId`. Read `GetCallResult(requestId)` to retrieve the final envelope.

A running call keeps its worker and native execution lane. Other native calls receive `PRECONDITION_FAILED` with `condition=worker-call-running`, naming the tool, request and start time. `GetSessionState` reports `running` and `lateResults`, and worker status reports `Busy`. `DisconnectPortal` and `RestartOpennessWorker` refuse while a native call is unanswered. Session disposal waits for the original invocation to finish its binding updates, audit and approval completion. Worker exit or a malformed reply remains a real transport fault. Requests are never replayed automatically.

## Inspect an unknown result

An answered `OUTCOME_UNKNOWN` affects that call. Read `GetCallResult(requestId)` and inspect its target before repeating it. `GetSessionState.data.sessionState.unsettled` lists its request ID, tool and UTC timestamp. Later independent calls run through normal admission and approval. `SaveProject` and `SaveProjectCopy` previews and executions carry `UNSETTLED_CALLS` while entries remain.

The host carries the legacy `meta.requiresSessionReset` field until its planned removal. Admission and the unsettled list use the final call outcome. Result mapping retains legacy flags as unknown evidence for writes until Part 8 reclassifies each producer. Reads ignore those flags. The Workbench displays the number of calls with an unknown outcome.

A genuine transport fault returns `PRECONDITION_FAILED` with `condition=worker-faulted` until automatic recovery is implemented in NO-LOCK Part 4. Follow the `RestartOpennessWorker` instruction and bind the intended target explicitly. Lease records and reconnect-after-disconnect change in Part 3. Keep diagnostics and inspect TIA before repeating a write.

The session retains its last 16 completed call results in completion order and every running result in memory. Unknown IDs return `NOT_FOUND`. After eviction, summaries survive only for unread writes and unread unsettled IDs. These summaries contain tool, outcome and time, plus a notice that the full payload is unavailable. Reading a summary releases its guard and removes the summary. Later lookups return `NOT_FOUND`. Read full results remain queryable while they are among the latest 16 completed results. Inner unknown batch-write IDs resolve to the enclosing result when retained. Reading the outer result or summary acknowledges all its inner unknown writes. An identical write returns `same-call-unsettled` while it is running or its result remains unread. Reading its completed result releases that guard. Previews and different arguments continue through the usual checks. Cancellation after the in-progress response leaves the original call running, and its progress notifications stop when the response detaches. Invalid progress is logged once per call and further progress is dropped while the worker continues waiting for the real reply. Reply-level faults and process exit follow the explicit recovery path.

The accepted design and implementation stages are recorded in [ADR 0034](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/standards/decisions/0034-no-session-lock.md).
