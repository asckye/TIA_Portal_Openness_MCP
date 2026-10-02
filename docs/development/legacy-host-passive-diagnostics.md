# Legacy host passive diagnostics candidate

`Bootstrap` and `RunCapabilitySelfTest` use the distinct
`legacy-host-passive-diagnostics-v1` contract. These are partial candidates,
not equivalent replacements for the upstream response schema or native readiness
checks. `upstreamResponseCompatible=false` and `nativeCertified=false` are explicit.
No production/full-support count should be inferred from registration.

## Scope and fields

The exact release keys are `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`.
`selectedRelease` reports the existing catalog identity and build-target state;
`existing-engine` does not mean installed, accepted or usable. The host remains
`plc-foundation-source-preview`, with `productionAccepted=false` even for 20/21.
`host.nativeSessionConfigured` and `nativeCallsDisabledByConfiguration` report
only the launch configuration; native calls are disabled by default. The
passive tools never call a worker even when that configuration enables it.

`registeredTools` and `registeredToolCount` describe the actual list handed to
MCP, including these two tools. Each entry identifies host-only execution or
the wired foundation worker operation. The roster is bounded at 256 entries,
with names capped at 128 characters. It contains no machine/user identity,
paths, process IDs, environment variables, worker error text or arguments.

`checks` verifies unique registered names, object input schemas with named
properties/closed additional properties/valid required-property references,
and foundation tool operation membership in the worker's source allowlist.
These are structural checks, not a complete JSON Schema validator, sample tool
execution, protocol round-trip to a worker, Siemens reflection, or PLC semantics.
A failed structural check returns an MCP tool error result. Internal exceptions
use a fixed redacted error. No tool schemas or user-supplied strings are echoed.

`probes` explicitly marks installed TIA, installed PublicAPI, SDK compatibility,
group membership, permission, connect readiness, worker availability, connection
state, portal processes, project state, automation context and native acceptance
as `not-probed`. False side-effect fields describe this call only; they do not
assert that another host tool or process has never performed those actions.

## Arguments and intentional incompatibility

`Bootstrap` takes no arguments. `RunCapabilitySelfTest` accepts only
`connectIfNeeded=false`, `includeProjectTree=false`, and
`inspectPortalProcesses=false`, with false defaults and schema enum restrictions.
Any true flag, native/selfConnect option, context-path option
(`expectedPlcSoftwarePath`, `expectedHmiSoftwarePath`), unknown/case-mismatched
argument, or wrong type is rejected with InvalidParams before registry inspection.
Context paths are deliberately absent from the candidate schema, rather than
accepted and silently ignored. No connection or process inspection is performed.

There is no reference from these handlers to the worker, native APIs, group
membership helpers, filesystem, network, launch, attach, repair or settings APIs.
The registry has the ordinary worker reference for other tools, but diagnostic
handlers receive only release/configuration and a roster accessor. They never
invoke another registered tool. This remains separate from the production
20/21 membership-read-only fix.

## Verification

- Managed host suite: `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests --no-restore`.
- Real MCP stdio: `python3 scripts/checks/Test-LegacyPassiveDiagnostics.py --dotnet <dotnet> --host <built-host-dll>`.
- The stdio check covers all eight canonical releases, each with native-session
  disabled and enabled. A configured worker-launch sentinel must remain absent.
  Both diagnostics match tools/list; unsupported native options fail redacted.
- InvokeAsync tests assert zero calls on a forbidden worker, safe explicit
  defaults, cancellation, argument rejection before roster access, bounded
  output, exact registration, malformed structural schemas and missing mappings.

On 2026-10-02, .NET 8.0.425 built the host without errors (two existing catalog
nullability warnings). The managed suite and 400 stdio checks passed. No Siemens
assembly, TIA process or worker was launched by this validation. Windows SDK
rebuild, native acceptance, permissions and installed API readiness remain untested.
