# TIA Openness Studio

The English/Chinese WPF desktop now hosts both **Engineering** and **MCP & clients** in
one .NET 10 main window. The root `TiaOpenness.exe` is the Studio launcher (`TiaOpenness.Launcher` assembly)
for this desktop, not a second configuration application. Configuration code is compiled
into the GUI and the page remains alive while the engineering view is shown. Version,
language and appearance are shared; a running service or active engineering bridge locks
the selected release. Closing the main window still handles its owned MCP service.

Engineering operations continue to call Siemens Openness through the .NET Framework 4.8
bridge. The MCP engines retain their existing process boundaries. The unified desktop
requires the complete bundle, whose `runtime/dotnet` supplies .NET 10, including on an AI-only host.
This source change has not replaced the published v3.2.0 ZIP.

Upstream: `asckye/tia-openness-studio`, commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`, MIT,
copyright 2026 asckye. See [LICENSE](../../third_party/tia-openness-studio/LICENSE) and [file provenance](../../third_party/tia-openness-studio/upstream.json).
The complete original tree and Git history remain under `../../third_party/tia-openness-studio/upstream/` as reference archives.

For a first installation and a step-by-step desktop walkthrough, see the [beginner guide (Chinese)](../../docs/getting-started/beginners.zh-CN.md).

## Build and run

Build on Windows with .NET 10 SDK, .NET Framework 4.8 and the authorized PublicAPI directories:

```powershell
pwsh ./scripts/build/Build-Studio.ps1 -PublicApiRoot 'D:\Project\TIA_Portal_MCP' -Test
& ./src/Studio/Gui/bin/Release/net10.0-windows/TiaOpenness.exe --lang zh --openness-version 21
```

`-NuGetConfig` may select a local restore configuration. Each version compiles the same native
source against its exact SDK into a separate adapter DLL. The build places them under the app's
`bridge/adapters/v<release-key>` directories for all eight releases. No Siemens assembly is copied into
the output; the native bridge resolves the installed version at runtime.

Choose **Connect** to attach to a running instance or start TIA when none is available. The
Headless option controls a newly started TIA instance. Open, Save and Close now operate through
the native session. The bridge runs calls sequentially on its STA thread. An explicit
`--openness-version 14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20` or `21` selects the release.
The title-bar release picker selects the same identity before connecting; omission selects the newest
supported installation. Restart Studio to change the native version after a bridge session starts.

For the synthetic workflow, no PublicAPI or TIA installation is needed:

```powershell
dotnet build src/Studio/Gui/TiaOpenness.Gui.csproj -c Release
& ./src/Studio/Gui/bin/Release/net10.0-windows/TiaOpenness.exe --mock --lang zh
```

The GUI build also builds and deploys the API-independent bridge. Native adapters are built only
by the explicit SDK build above. Runtime compiler downloads and automatic adapter compilation
are removed. Doctor remains available without loading a native TIA session.

## Glass Layers interface

The native WPF view uses the [Glass styles](Gui/Themes/Glass.xaml) and bundled [OFL fonts](Gui/Fonts). The minimum
window size is 1200 x 780; the center column expands with the window. One title-bar menu
provides Project, View, Tools and Help. View selects English or Chinese and Light, Dark or Auto. Language and theme changes keep the same ViewModel,
project session, selection and commands. Ctrl+S saves the project; Ctrl+1 and Ctrl+2 switch
between Engineering and MCP & clients, also available from View. The caption shows the current
page beside the app title and updates with the language; the taskbar title remains the app name.
The release picker and window buttons remain in the title bar, with free space for dragging.
Help exposes updates, client instructions and About from either page. Save both configurations
is a visible button in the AI clients card header on the configuration page.

Cards, logs, local diffs, summaries, menus and application message dialogs follow the selected
palette. System file/folder pickers keep the Windows shell appearance.

Project opens the existing connection/project options; Export / Import opens format and output
options; Inspect opens the existing inspection rules. Program blocks retain filtering, selection,
export/import and compile actions. VCI keeps mapping and synchronization previews separate from
execution. The direction selector chooses the existing push/pull commands; this is not Git push.
Git diff includes staged, unstaged and untracked local changes.

Compile and inspection cards display results already recorded by the existing operation log.
Clearing that log returns the cards to Not run. Object names, paths, code and native diagnostic
messages keep their original text when the interface language changes. The MCP page configures clients and service lifecycle; it is not a generic tool invoker.

Set `TIA_GLASS_SCREENSHOTS` to a local output directory before running the WPF tests below to
capture the English and Chinese fixtures in both themes. These are actual WPF renderings at
1200 x 780 with synthetic data, without connecting to TIA. The reference package remains local
to the design handoff; see [design QA](../../docs/development/design-qa.md) for comparison notes.

## Workflows and boundaries

| Workflow | Implementation |
|---|---|
| Project / PLC / HMI browser | Native session and shared desktop contracts |
| XML export | Native block/type export; existing output files are reported instead of silently replaced |
| Text export | Native external-source generation for supported textual blocks/types; no claim of lossless graphical conversion |
| XML import | Native import with the operator's overwrite choice |
| Source import | `.scl`, `.db`, `.udt`; requires overwrite confirmation because native generation may replace blocks; temporary source has a unique name |
| Compile | Native compiler result with counts and messages |
| Inspection | Shared metadata rules; unused-block inference is skipped without complete reference evidence |
| VCI | Unavailable on V14 SP1/V15.1; shared workflow over legacy WorkspaceMapping APIs on V16–V19 and MappedObject APIs on V20/V21 |
| Git diff | Staged, working-tree and untracked local file changes |
| Mock | The same typed operations and dispatcher with synthetic PLC/HMI fixtures |

VCI mapping exports once at the workspace root. It does not retry a failed write with another
directory. Import/mapping/synchronization batches stop after the first failed native operation
and retain their partial results. An interrupted bridge call is not automatically replayed.
These operations do not provide rollback. An unsuccessful source generation retains its uniquely
named source for inspection. The legacy `RequireBlockComment` inspection rule checks HeaderAuthor.

The configuration page is the compiled `src/TiaOpenness.Gui/Configuration/ConfigurationView.xaml`
UserControl. Its code-behind retains the configuration/service logic and loads the Studio palettes
and its own compiled English/Chinese dictionaries. `src/TiaOpenness.Launcher` contains only the
Framework compatibility launcher; configuration tests run in the .NET 10 desktop host.

## Validation

```powershell
dotnet run --project tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj -c Release
dotnet test tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj -c Release
dotnet test tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj -c Release
```

The client suite runs both in-process mock workflows and the real bridge executable with its
mock backend, including project lifecycle, browsing, export/import, compile, inspection, VCI,
progress and error recovery. It also runs Doctor through the ordinary bridge without a TIA
session. WPF tests render and inspect the actual controls. Native adapters compile against the
official eight-release SDKs locally; CI runs the bridge/client/WPF checks without Siemens DLLs.

Live TIA project acceptance remains pending. `Build-MultiVersion.ps1` deploys the desktop to `runtime/studio`; the v3.2.0 formal release pipeline includes Studio and all eight adapters through `Package-Release.py`. `Package-MultiVersion.py` remains available for local development archives. Full audit findings are in
[the shared version framework](../../docs/development/unified-version-framework.md).
