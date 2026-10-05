# Shared Openness host utilities

These files are source-linked by their consumers. They have no MCP or Siemens API dependency.

| Source | Consumers | Contract |
|---|---|---|
| `ProcessArguments.cs` | Configurator, MCP router/worker, MCP ecosystem tools, Studio Git | Windows CRT argument quoting; C# 5 compatible |
| `LocalProcess.cs` | MCP ecosystem tools, Studio Git | UTF-8 streams, concurrent output draining, existing command timeouts and output limits; .NET 4.8 and modern .NET |
| `OpennessEnvironment.cs` | Configurator, engine/Foundation/adapters, Studio Doctor/locator | Read-only Windows facts; C# 5 compatible; caller owns search order and runtime admission |
| `BundleLayout.cs` | Logic (engine resource, CLI and router lookup), Studio Client, Core and GUI | BCL resource table and bounded root lookup; net48, net10.0 and net10.0-windows; excluded from the woven engine EXE |
| `NativeCallDiagnostics.cs` | Full engines, version adapters, diagnostic fixtures | Shared weak object lineage, call state, and enumeration adapters |
| `NativeCallDiagnostics.Journal.cs` | Full engines, version adapters, diagnostic fixtures | Native span rows written through the shared invocation journal |
| `InvocationJournal.cs` | Full engines, version adapters, diagnostic fixtures | Library-free JSON line writer with the bytes of the engine's former System.Text.Json rows; per-process file sink, or a callback sink and correlation source set by the host |

The native diagnostics class keeps its original namespace and type name for generated instrumentation.
Each host keeps only a thin `InvocationJournal` facade for its own JSON types (the engine keeps
`JsonObject` for `Health()` and the binding snapshot). Do not add a second copy of the tracking or
journal implementation when adding an adapter.

Studio calls Openness through its native bridge. Sharing these utilities does not introduce an MCP
dependency. V14 SP1–V19 use foundation runtimes; V20/V21 use full engines. The exact-version adapters and both profiles are built and checked by Build-MultiVersion.

Build-Release and Package-Release include this directory in the compiler-input inventory.
Build-Configurator records the source-linked desktop inputs separately.

`BundleLayout` accepts an explicit root first, preserving its spelling. A nonempty
invalid override returns null so the caller can apply its existing error policy.
Automatic lookup requires `manifest/package-manifest.json` at the root derived
from a known output directory: `runtime/<TiaVersionCatalog.RuntimeDirectory>`,
`runtime/studio`, `runtime/studio/bridge`, or the engine `bin`/`bin-v20` and Studio
Gui/Bridge Release/Debug outputs. The GUI's copied `bridge` output is also supported.
It uses `DirectoryInfo.Parent.FullName` and segment-wise `Path.Combine`; it does
not walk arbitrary ancestors, inspect `.git`, normalize explicit roots, or search
another root for a missing resource. Null leaves the original caller probing
available through 4.0 (D-G7-3). Environment variables and user-facing exceptions
remain the caller's responsibility; no new override name is introduced.

The table contains tracked delivery resources, including the template and guide
directories. Runtime executables are build outputs, not entries in this table.
`Check-BundleLayout.py` verifies Git membership and the enforced resource list in
`Validate-Bundle.ps1`. Build-Configurator records this shared desktop input;
Build-Release and Build-MultiVersion already inventory the whole shared directory.
