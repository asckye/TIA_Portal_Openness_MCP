# Automation Engineering Workbench desktop

Status: Current · Last checked: 2026-11-04, master `871d54da`

The WPF Workbench runs on .NET 10 and supports English and Chinese. The root `TiaOpenness.exe` launcher starts the desktop from `runtime/studio`; the bundle supplies .NET 10 in `runtime/dotnet`. The launcher also checks for .NET Framework 4.8, which the PLC workers and retained Studio bridge require.

Upstream: `asckye/tia-openness-studio`, commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`, MIT, copyright 2026 asckye. See [LICENSE](../../third_party/tia-openness-studio/LICENSE) and [provenance](../../third_party/tia-openness-studio/UPSTREAM.json). For installation and a desktop walkthrough, see the [beginner guide](../../docs/getting-started/beginners.zh-CN.md).

## Sessions and interface

[MainViewModel](Gui/ViewModels/MainViewModel.cs) uses [McpProjectClient](Gui/Services/McpProjectClient.cs) and [McpSessionClient](Gui/Services/McpSessionClient.cs) for project operations. The Workbench attaches to a host session through the operator pipe; the host shares tools, approvals and audit with MCP clients. It can also start its own operator host when needed. Multiple running TIA processes are presented for explicit PID selection.

All eight releases use FoundationHost with a PLC worker per MCP session. The public profile is `plc-foundation`; all releases have lite and full rosters. Native calls currently use STA for V14 SP1–V19 and MTA for V20/V21. The retained Framework bridge and `StudioOpenness.V*` assemblies are separate legacy components; the current project UI must not be described as a direct bridge UI. `TiaSharedAdapterPaths` still selects the optional Studio adapter path, and release preparation sets it to `false`.

[MainWindow.xaml](Gui/MainWindow.xaml) and [Themes](Gui/Themes) implement the EplanMcp-based shell and pages: a 232-pixel sidebar with a 64-pixel collapsed form, project, environment, logs, MCP and clients, version control, AI calls, settings and approvals. [WorkbenchFonts](Gui/Themes/WorkbenchFonts.cs) uses system UI and monospace fallback chains. Light, dark and high-contrast palettes are supported. The approvals page has a list and details, host-computed risk, equal-weight write decisions and a reject default for destructive requests. Turning write approval off currently uses a confirmation panel without text entry. APR-05 is adding a required confirmation word for that setting; it is not implemented yet and does not apply to ordinary approval decisions.

## Build and check

From a clean repository, use the [release manual](../../docs/manuals/release.md) to supply the eight licensed SDKs and pinned decoder Python:

```powershell
dotnet run --project build-tools/release -- prepare -Version <version> -BuildOnly -PublicApiRoot <eight-version-sdk-root> -Python <absolute-python.exe>
```

Match `<version>` to `Version.props`. This mode is available on any branch and builds the desktop into `runtime/studio`; it does not create a publishable candidate. To build only the GUI for local development:

```powershell
dotnet build src/Studio/Gui/TiaOpenness.Gui.csproj -c Debug
```

The host runs `merge-check` for affected builds, registered suites and source checks, and `merge-check -All` before a release candidate. Full `prepare` builds, packages, checks the ZIP and uploads a private candidate draft. After maintainer confirmation, `publish-prepared` publishes that draft. There is no nightly. The sandbox runs affected Debug projects and test classes only; current suite registration and thresholds are in [validation](../../docs/manuals/validation.md).

GUI and Core tests cover synthetic UI, protocol and client behavior. These checks do not demonstrate native TIA behavior; see the [real-machine ledger](../../docs/reference/real-machine-ledger.md) for pending acceptance and the [architecture](../../docs/standards/design.md) for process ownership, failure handling and retained bridge boundaries.
