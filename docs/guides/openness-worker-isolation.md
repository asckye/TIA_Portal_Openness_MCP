# Opt-in Openness worker isolation

The local 3.1.0 candidate can run its Openness tools in a supervised child process. This mode is **off by default**. Local protocol and fault tests do not establish stability with a running TIA Portal installation.

Add these arguments to the existing MCP server command (HTTP or STDIO):

```text
--isolate-openness --worker-timeout-seconds 120
```

Use the engine matching the installed TIA major version. The configurator does not currently offer an isolation checkbox; add the arguments to the server entry or launch command yourself. These flags apply to the MCP server, not the `gen`, `patch` or other CLI subcommands.

`--worker-timeout-seconds` accepts 10–180 seconds and defaults to 120. The deadline includes queueing, child startup and execution. Choose it with care for long compile/import operations: a deadline is not a safe cancellation point inside TIA.

## Behavior

- The host retains HTTP/STDIO, authentication, discovery and diagnostics. Engineering requests run in a separate net48 child using the same executable. Before dispatch, the host verifies the private protocol, engine major version, executable SHA-256, child PID and tool roster.
- The child inherits the host working directory, preserving relative PublicAPI and tool-file paths. HTTP credentials are not copied into child command-line arguments.
- Each worker accepts at most 16 calls, including its active call, and executes one at a time. Queue rejection or cancellation before dispatch reports that the request was not sent.
- A deadline, unexpected exit, broken pipe, malformed response, wrong response ID or recognized native channel failure invalidates the worker. The host remains available for diagnostics. No failed request is automatically replayed and no replacement worker is automatically started after a fault.
- Cancellation after dispatch reports `nativeOutcomeUnknown=true`. Only the owned client process is terminated. TIA may still complete an operation already received; there is no rollback guarantee. The host never kills the TIA process or its process tree.
- Parent-process identity monitoring makes a child exit when its owning host exits. Exports, batch preview plans and other in-memory state belong to one child. Restarting discards them; export IDs include a random process-session component.
- Implicit project attach/self-heal is disabled in both modes. After reset, engineering calls require a successful `ConnectToProject`, explicit named `Connect` / `AttachToOpenProject`, or `ConnectIsolated`. Read-only bootstrap, state, catalog and export diagnostics remain available.

## Diagnostics and recovery

The following tools are advertised in both full and lite profiles:

Call these host controls directly, or through `CallTool`. Do not place them inside engineering batches or transactions; those execute within the child and cannot inspect or restart its owning host.

| Tool | Behavior |
|---|---|
| `ReadOpennessWorkerStatus` | Read state, PID, generation, active call, admitted calls and last fault without starting or contacting the worker. Without isolation it reports `enabled=false`. |
| `RestartOpennessWorker` | Default `confirmRestart=false` previews only. `true` discards an idle or faulted worker. Active/queued calls make reset fail; it never saves or reconnects a project. The next permitted call starts a fresh worker. |

`ReadNativeInvocationLog` also runs in the host, directly in full mode or through `CallTool` in lite mode, so logs remain accessible after a worker fault. Host controls do not create paged exports; log reads remain bounded to 500 records. Ordinary large tool responses and `GetExport` use the same child.

After a fault:

1. Read worker status and the invocation journal. Treat any dispatched write without a trustworthy result as having an unknown outcome.
2. Check the intended TIA process and project state independently. Do not retry the write automatically.
3. Once active/queued requests have returned, explicitly call `RestartOpennessWorker(confirmRestart=true)`.
4. An unclean worker exit leaves an instance lease marked uncertain. Inspect/save the intended project independently and restart that TIA instance before reconnecting; do not delete its lease to bypass this guard. Then use `ListPortalProcessProjects` and `ConnectToProject(processId, processStartUtc, projectPath)` and read back state before deciding whether any operation is still needed. Old export IDs and preview plans cannot be reused.

Host dispatch records (`worker:<tool>`) and child tool records share a correlation ID. The journal records tool names/stages, cached binding identity (including project path and generation), thread/apartment and object type/path when supplied. It excludes general arguments, credentials and project contents; paths can still be private. `GetState.meta.journalHealth` reports failed journal writes. A missing completion records an interruption; it does not prove whether TIA committed a change. Journal I/O failures are best-effort diagnostics and are not a transactional durability guarantee.

## Verification and remaining work

`Build-Release.ps1` runs supervisor fault injection, real MCP protocol checks and local mixed-load tests for both V20/V21 engines. Protocol checks cover HTTP/STDIO, full/lite, pagination, reset, invalidated exports, responsive status during a hung call, and child exit after abrupt host termination. These tests load actual host methods and use test-only offline/fault children. They do not initialize Openness, attach to TIA or open a project.

The ordinary and isolated stress runs record separate host/child memory and handle samples, call correlation and runtime/script hashes in `manifest/release-build.json`. The separate native lifecycle harness is compiled and checked offline; live execution remains pending. `scripts/checks/Test-NativeMcpSession.py` adds production MCP create/save/close/reopen/identity/disconnect acceptance for a new owned headless instance. It defaults to plan only; `--run-live --confirm-new-portal` is required to execute. Its offline safety checks do not prove that native sequence works.

Exact binding now captures API major, PID, process start time, absolute normalized project file path, name and a new generation per explicit project bind. Project access checks these values and faults on mismatch; batch previews include the full binding identity and HMI event/script/settings tokens are invalidated on explicit rebinding as well as worker restart; `GetState` returns cached identity without adopting another project. Bare-name connection refuses multiple candidate instances. File path normalization is lexical, not a filesystem object-ID proof; a UI close/reopen of the same file in the same TIA process may only be detected through stale-handle errors.

Separate MCP processes running as the **same Windows user** reserve a TIA instance for the whole connection using an OS-held file handle under `%LOCALAPPDATA%/TiaMcp/instance-leases`. Another port/process is refused before `Attach`. Clean detach permits reuse; a crash/kill leaves an uncertainty marker until TIA restarts (its start time changes). This is exclusive ownership, not a shared job queue. It does not coordinate older MCP builds, other Openness applications, different Windows users or GUI actions. Use separate TIA instances for simultaneous MCP work. Resetting a connected worker also leaves an uncertainty marker because reset terminates the child.

`scripts/diagnostics/Collect-TiaCrashEvidence.ps1 -OutputDirectory <new absolute directory>` collects local Windows event evidence, bounded recent invocation logs with hashes, and dump path/size metadata. `-PlanOnly` performs no queries or writes. It never changes dump policy, reads dump contents, attaches to TIA or uploads evidence. Partial collection failures remain explicit in `evidence.json`.

The remaining acceptance gaps are:

- Complete instrumentation of every native getter/operation. Exceptions swallowed without reaching the native failure classifier may not immediately invalidate a child; the host deadline is the remaining boundary.
- Native V20/V21 acceptance for real reads, edits, compilation, long-running operations and fault recovery. Worker isolation cannot prevent TIA server-side crashes.

Native PLC cross-reference queries remain disabled by default. Isolation does not establish their crash root cause or make them safe to enable. It also does not add unsupported HMI faceplate APIs.
