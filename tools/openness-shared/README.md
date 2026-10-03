# Shared Openness host utilities

These files are source-linked by their consumers. They have no MCP or Siemens API dependency.

| Source | Consumers | Contract |
|---|---|---|
| `ProcessArguments.cs` | Configurator, MCP router/worker, MCP ecosystem tools, Studio Git | Windows CRT argument quoting; C# 5 compatible |
| `LocalProcess.cs` | MCP ecosystem tools, Studio Git | UTF-8 streams, concurrent output draining, existing command timeouts and output limits; .NET 4.8 and modern .NET |
| `OpennessEnvironment.cs` | Configurator, engine/Foundation/adapters, Studio Doctor/locator | Read-only Windows facts; C# 5 compatible; caller owns search order and runtime admission |
| `NativeCallDiagnostics.cs` | Full engines, version adapters, diagnostic fixtures | Shared weak object lineage, call state, and enumeration adapters; each consumer supplies a partial serializer |

The native diagnostics class keeps its original namespace and type name for generated instrumentation.
Newtonsoft and System.Text.Json serialization remain local to their runtime. Do not add a second
copy of the tracking implementation when adding an adapter.

Studio calls Openness through its native bridge. Sharing these utilities does not introduce an MCP
dependency. V14 SP1–V19 use foundation runtimes; V20/V21 use full engines. The exact-version adapters and both profiles are built and checked by Build-MultiVersion.

Build-Release and Package-Release include this directory in the compiler-input inventory.
Build-Configurator records its two C# 5 inputs separately.
