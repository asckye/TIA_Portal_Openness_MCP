# Diagnostic membership read-only fix (2026-10-02)

## Narrow production change

`Bootstrap` and `RunCapabilitySelfTest` now call the existing
`Siemens.Openness.IsUserInGroupNoFix()` instead of the repairing async helper.
The production diagnostic diff is two lines in `ModelContextProtocol/Tools/McpServer.cs`.
Production `Program.cs` startup also uses the pure helper, with its existing
fail-closed host-start condition and nonmember exit code 2 unchanged.

- A nonmember returns false without calling `AddUserToGroupAsync`.
- `Bootstrap` retains its existing false-on-exception behavior and recommends
  `EnsureOpennessUserGroup`; the recommendation does not execute that tool.
- The self-test retains its existing failure detail on exceptions.
- Public async task signatures and optional connection/project checks are unchanged.
  These methods now have no awaits; a compiler may issue CS1998 (not a runtime error).
- The explicit `EnsureOpennessUserGroup` repair tool is unchanged.

## Verification

Run from repository root:

```sh
python scripts/checks/Test-DiagnosticMembershipSources.py
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.DiagnosticMembershipTests/TiaMcpServer.DiagnosticMembershipTests.csproj -c Release
```

Both passed on 2026-10-02. The source guard checks both diagnostic call sites,
startup nonmember failure and repair guidance, existing error reporting, default/opt-in connection gating, the unchanged explicit
repair path, and the exact pure-wrapper body. The isolated .NET 8 test compiles
actual production `Openness.cs` against a fake `Siemens.Collaboration.Net` API;
nonmember/member/unknown cases yield three read callbacks and zero repair callbacks.
The unknown case rethrows the original exception. No Windows principal or actual
group-membership API, elevation, group modification, or Siemens/TIA runtime is used.
This does not compile or execute the whole production diagnostic methods.

V20/V21 Siemens SDK compilation, release build/hash validation, and authorized
Windows/live verification remain pending. No commit, push, deployment, or live
membership change was performed.

## Important remaining scope boundaries

Startup previously attempted to repair membership before running its selected
command/host. It now checks membership only. A nonmember immediately takes the
existing diagnostic/exit-code-2 branch, so no host is started and readiness is not
falsely reported. Unknown membership still reaches the fatal catch/rethrow.
Explicit CLI `doctor --fix` remains the repair route before startup;
`EnsureOpennessUserGroup` remains an explicit action for running hosts.
The startup fix only removes implicit group repair; it is not a claim that
starting the application has zero side effects.

`RunCapabilitySelfTest(connectIfNeeded: true)` still calls `ConnectPortal`.
Source inspection found no group-repair invocation in that path, but it can
attach to a process or create a TIA instance when none exists. Its default is
false. This patch does not change or certify that connection behavior.

CLI doctor and the diagnostic Doctor tool separately select pure or repair paths
based on explicit repair options; they are outside the diagnostic/startup call-site patch.
