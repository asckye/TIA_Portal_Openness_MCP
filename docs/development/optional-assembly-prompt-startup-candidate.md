# Optional-assembly prompt startup candidate

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

## Scope

The server's stdio and HTTP registration paths previously called
`WithPromptsFromAssembly()`. ModelContextProtocol 0.3.0-preview.4 scans every
assembly type in that method. An unrelated type whose base class requires an
absent optional engineering assembly can therefore cause
`ReflectionTypeLoadException` before MCP starts.

Both paths now use `McpPromptRegistration.Configure`, which supplies the complete
explicit prompt-type list to `WithPrompts(IEnumerable<Type>, JsonSerializerOptions)`.
The current inventory contains the static `McpPrompts` class and its 30 prompts.
The generic overload cannot accept a static class. The type-list overload is
present in the pinned package's netstandard2.0 reference documentation and its
[official SDK source](https://github.com/modelcontextprotocol/csharp-sdk/blob/15f8e89ac3aedb2bb4fa9232f6093f092b2ce956/src/ModelContextProtocol/McpServerBuilderExtensions.cs).
It inspects the supplied prompt types rather than every type in the assembly.

No exception is swallowed and no tool is silently dropped. The existing stdio
loader-exception diagnostic is relabeled `MCP registration failed` because its
scope includes tools, prompts, and resources. Engineering assembly resolution,
feature availability, tool inventory, permission checks, and failure behavior
are unchanged by this fix. Earlier pending NoFix membership changes are retained.

## Pure regression

Run from the repository root:

```sh
python scripts/checks/Test-PromptRegistrationSources.py
python scripts/checks/Test-DiagnosticMembershipSources.py
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.PromptRegistrationTests
```

The prompt regression uses the actual pinned SDK and linked production prompt
and registration source. Its two self-authored fixture assemblies model an
unrelated optional base-class dependency. Each case uses a separate load context
and temporary directory. In the missing-dependency case the optional DLL is
physically absent, and the loader refuses fallback resolution for that DLL.

Verified in the Linux pure test:

- Old assembly scanning succeeds with the fake optional DLL present and throws
  `ReflectionTypeLoadException` with the expected missing-DLL cause when absent.
- Explicit registration succeeds under both stdio and HTTP stream builder
  configurations with the fake dependency present and absent.
- All 30 real prompt names are registered. Full prompt schemas match the old
  registration baseline when the dependency is present.
- All 30 prompts render through the SDK's `McpServerPrompt.GetAsync` in every case.
- Source guards enforce both production call sites, a complete explicit prompt
  container inventory, and the unchanged fail-closed NoFix membership policy.

This verifies registration and SDK prompt rendering, not live stdio/HTTP protocol
sessions. No Siemens DLL, TIA installation, Windows API, or native process is used.
Exact V20/V21 Windows compilation, Build-Release, and real startup without optional
Startdrive/DCC components remain pending while the Windows computer is offline.
This is a WIP candidate, not a release or a claim of optional-feature availability.
