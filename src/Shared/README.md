# Shared host utilities

Status: Current · Last checked: 2026-11-04, master `871d54da`

This directory contains source shared by host, worker, adapter and Workbench projects. Consumers select the files they compile; native implementations are in [Adapters/Native](../Adapters/Native). [Host](Host) contains shared MCP declarations and host support. API-independent contracts live in [Adapters.Contracts](../Adapters.Contracts).

All eight releases use FoundationHost with a PLC worker per MCP session and public profile `plc-foundation`. The Workbench project UI uses the MCP operator channel; the retained Studio bridge still calls Openness for its legacy path. [PrepareBuild](../../build-tools/release/PrepareBuild.cs) builds the matching hosts, workers, adapters and desktop. Registered suites run under `merge-check`; `prepare` builds, packages and checks the ZIP before candidate publication.

[ProcessArguments](ProcessArguments.cs) supplies Windows argument quoting, and [LocalProcess](LocalProcess.cs) supplies process execution with output draining and bounds. [OpennessEnvironment](OpennessEnvironment.cs) reads installed-environment facts. [BundleLayout](BundleLayout.cs) resolves bundle roots and shared resources; its consumers decide how to report an invalid or missing root.

[NativeCallDiagnostics](NativeCallDiagnostics.cs), its [journal support](NativeCallDiagnostics.Journal.cs) and [InvocationJournal](InvocationJournal.cs) provide native-call correlation and diagnostic rows. Their existing namespaces and type names are used by instrumentation. These diagnostics are not evidence of successful real-machine acceptance.

`check-bundle-layout` checks tracked resources and delivery rules. `prepare` also checks the staged package. See [architecture and runtime layout](../../docs/standards/design.md), [validation](../../docs/manuals/validation.md) and the [real-machine ledger](../../docs/reference/real-machine-ledger.md).
