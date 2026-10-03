# Detached runtime queries: bounded source candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

Date: 2026-10-02. Exact release keys: `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`. Original V14 and V15 are excluded. Current source typed rebuild and native acceptance: **NOT RUN**. No installed TIA execution, attachment, project opening, process launch/stop, registry changes or window automation was performed.

## Three distinct contracts

- `GetState` calls `ReadState()` on the owning thread. Returns cached `ReleaseKey`, `IsAttached`, `ProcessId`, `ProjectFile`, `OwnsProject`, `IsLocalSession`, `IdentityStatus`, `RuntimeConnectionStatus`. Cached attachment is explicitly not live connection proof. No Siemens collection access, rebind, attachment or OS liveness check. The existing connection method records only a PID; therefore identity remains `unverified-cached-pid`, and connection status `not-probed`.
- `ListPortalProcessProjects` calls `ReadPortalProcessProjects()` without a connection prerequisite. Returns this selected API's diagnostic `Processes` with `ProcessId`, `SnapshotAcquisitionTime`, nullable `ProjectPath`, nullable `OsStartTimeUtc`, and `OsIdentityStatus`. Enumeration failures produce a sanitized read error, not empty success. At most 1,024 unique positive PIDs. Snapshot date preserves the API's original DateTime Kind/string representation; no UTC is invented.
- `DiagnosePortalConnectReadiness(processId)` calls `ReadPortalConnectReadiness(int)` for one explicit positive PID without attaching. A missing PID returns `not-found`; a found PID returns `unknown`, never `ready`. Permission is `not-probed`. Native metadata errors remain read errors. No process selection, window inspection, program launch, repair, project open or native permission probe.

These are intentionally narrower safe contracts, **not claimed pinned-V17 contract migrations**. In particular, cached `IsAttached` replaces any claim of live `IsConnected`; list rows are structured rather than formatted strings; diagnostic takes explicit `processId` rather than `includeWindows`. All arguments and result fields use exact case-sensitive keys. Strict host validation rejects missing/extra fields, malformed dates, duplicate IDs, inconsistent attached state and fabricated readiness. Nullable unknowns survive the Newtonsoft worker/JSON host boundary.

## Manual-first evidence and API shape

The official Siemens diagnostic interface documents `TiaPortal.GetProcesses()` as producing static process snapshots that can become outdated. `TiaPortalProcess.AcquisitionTime` is the snapshot acquisition time; it is not process creation time or a stable binding token. The selected process identifier is `TiaPortalProcess.Id`, not `TiaPortalSession.ProcessId`. These objects have different meanings.

Read evidence: local official V15.1 manual (10/2018), diagnostic interface pp85–87; V16 (11/2019), pp87–89; V17 (05/2021), pp92–94; V19 (11/2023), pp99–101. Current exact-release lifecycle/manual review is [separately recorded](ordinary-project-lifecycle-review-20261002.md). Base V14 manual was inspected only as historical context and is **not** evidence for the selected V14 SP1 API. V20/V21 diagnostic-chapter URLs could not be retrieved in this pass; do not mark all-release manual reconciliation closed.

The parent supplied an actual read-only PE/XML scan of all eight installed PublicAPI reference sets. It reports directly declared `TiaPortal.GetProcesses(): IList<TiaPortalProcess>` and `TiaPortalProcess.Id: int`, `ProjectPath: FileInfo`, `AcquisitionTime: DateTime` getters in every selected release. `TiaPortalSession.ProcessId: int` is separate. V14 SP1 through V20 define these in Siemens.Engineering.dll; V21 uses Siemens.Engineering.Base.dll. Metadata report SHA-256: `a844173f7c4c1712875654c44ed453fd973115b0704476d56e267d621a60516e`; XML report SHA-256: `3a4cf4a78aef7691b381820da34eba9d5a875e1022740d6ea9cfa5e57067a6c3`. This is reported metadata evidence, not a local reinspection or successful build of this new source. Existing exact release/assembly gates are unchanged.

## Independent OS observation

`System.Diagnostics.Process.GetProcessById(pid).StartTime.ToUniversalTime()` is this wrapper's independent OS observation strategy, not Siemens API identity. It returns `observed-not-bound` only after a same-handle liveness/start-time recheck. Access-denied, exit races and unavailable permissions return null start time and `unknown`, without raw exception text. No process is launched or terminated. This observation is never passed to Attach and does not prove that the native metadata snapshot and OS observation refer to the same incarnation: PID reuse can occur between them. Existing Connect remains explicitly PID-only. A stable stale-PID-resistant connection protocol would require a separate change.

## Validation boundary

`RuntimeQueryTests` uses pure DTO/policy tests and actual MCP tool dispatch against an in-memory worker. It checks closed schemas, positive PID rejection before dispatch, exact result keys, worker JSON conversion, null unknowns, local/UTC/unspecified acquisition representation, independently UTC OS timestamps, malformed results, and unproven readiness rejection. These are managed contract tests, not a running TIA or real OS process validation. Current-source eight-SDK compilation remains pending.

Latest managed validation: 1,571 passed, 0 failed, one expected Windows-only publication-filesystem skip. The explicit source-wiring check passes all 35 worker operations and eight release-symbol gates. Default managed suite also checks every foundation source/policy is included in the adapter inventory and detects simulated omission of each operation-bearing module. These are lexical/source gates, not typed SDK compilation.
