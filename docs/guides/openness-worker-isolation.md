# Optional Openness worker isolation

V20/V21 full engines can run engineering tools in a supervised child process. Release builds currently default to isolation off until real-machine acceptance passes. Enable it by adding the following argument to the existing HTTP or STDIO MCP command:

```text
--isolate-openness --worker-timeout-seconds 120
```

Use `--no-isolate-openness` to explicitly force isolation off. The build default is centralized in `TiaMcpWorkerIsolationDefault` and appears in the release record as `workerIsolation.enabledByDefault`. The configurator has no isolation checkbox, and generated client commands include neither switch unless the user chooses one. Edit the server command or client entry to override the default. These flags apply to MCP host startup, not `gen`, `patch` or other explicit CLI subcommands. Foundation engines use their own worker architecture and should not be configured by copying these full-engine flags.

## Startup without a ready TIA installation

The MCP host can remain available when the TIA installation is missing, its major version does not match the engine, Openness API initialization fails, or the user is not in the Siemens Openness group. `InitializeEnvironment` and `GetEnvironmentDiagnostics` report the cause and repair steps. TIA-dependent tools return `RESOURCE_UNAVAILABLE` for `tia-openness-environment` with V4 metadata `rejected-before-operation` / `not-started`. Read-only diagnostics remain available. The Workbench environment page reports a running engine with an unavailable Openness environment, while the call panel labels worker-dispatched calls as **Engine worker**.

TIA-dependent tools return `RESOURCE_UNAVAILABLE` when Openness is not ready (`tia-openness-environment`, `rejected-before-operation` / `not-started`). Bootstrap and the environment doctor provide the repair steps. `GetOpennessWorkerStatus` reports `enabledByDefault`, the effective isolation mode, and environment readiness.

## What it changes

The host keeps the MCP connection, authentication and diagnostics available while engineering calls execute one at a time in the child. A timeout or native channel failure invalidates that child; failed operations are not automatically replayed. The timeout accepts 10–180 seconds and includes queueing, startup and execution.

Terminating the client worker does not cancel or roll back an operation already received by TIA. A dispatched write without a trustworthy result has an unknown outcome. Isolation also cannot prevent a crash inside the TIA server itself.

## Status and recovery

Read the current examples with `GetToolUsage(toolName="GetOpennessWorkerStatus")` and `GetToolUsage(toolName="RestartOpennessWorker")`.

| Tool | Use |
|---|---|
| `GetOpennessWorkerStatus` | Read worker state, PID, active calls and last fault without starting it |
| `GetNativeInvocationLog` | Read bounded invocation diagnostics from the host |
| `RestartOpennessWorker` | Preview or explicitly reset an idle/faulted worker |

Call host controls directly or via `CallTool`, not inside an engineering batch. After a fault:

1. Read the status and invocation log.
2. Inspect the intended TIA instance and actual project result before repeating an operation.
3. Once active calls have ended, follow the reset example. Reset discards old export IDs and preview plans.
4. If an instance lease is marked uncertain after an unclean exit, inspect and save the project as needed, then restart that TIA instance before reconnecting. Do not remove the lease to bypass the state check.
5. List actual TIA processes and bind the intended PID, start time and full project path using the current connection example. Read back state before continuing.

If isolation is disabled, there is no child to reset. Clear the failed binding through `DisconnectPortal`, or restart the MCP host when that cannot complete, then explicitly bind the correct project. Reopening TIA alone does not clear an old MCP binding.

Separate MCP processes under the same Windows user reserve an instance for a connection. Use different TIA instances for concurrent engineering sessions. This does not coordinate arbitrary third-party Openness applications or GUI edits.

## Diagnostics and validation

[Native-call diagnostics](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/native-call-diagnostics.md) describe the journal and local evidence collector. Logs may contain private project paths; inspect them before sharing.

Release checks exercise protocol behavior, worker failures and reset paths with offline test children. They do not establish live TIA stability, native cancellation or rollback. Current native acceptance is recorded in [capabilities](../reference/capabilities.md).
