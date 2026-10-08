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
The root launcher checks for .NET Framework 4.8 or later before starting the Workbench; if it is
missing, install the Microsoft .NET Framework 4.8 Runtime. The environment page reports this
prerequisite in Chinese and English. The bundled .NET 10 runtime does not replace .NET Framework.
The latest published package is v3.3.0; the 4.0 source is unreleased.

Upstream: `asckye/tia-openness-studio`, commit `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`, MIT,
copyright 2026 asckye. See [LICENSE](../../third_party/tia-openness-studio/LICENSE) and [file provenance](../../third_party/tia-openness-studio/UPSTREAM.json).
The complete original tree and Git history remain under `../../third_party/tia-openness-studio/upstream/` as reference archives.

For a first installation and a step-by-step desktop walkthrough, see the [beginner guide (Chinese)](../../docs/getting-started/beginners.zh-CN.md).

## Build and run

Build on Windows with .NET 10 SDK, .NET Framework 4.8 and the authorized PublicAPI directories:

```powershell
dotnet run --project build-tools/release -- build-studio -PublicApiRoot 'D:\Project\TIA_Portal_MCP' -Test
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

## Primer interface

The native WPF view uses one [Primer vocabulary](Gui/Themes/Primer.xaml), light/dark palettes,
Segoe UI with bundled Noto Sans SC, and bundled JetBrains Mono for values and code.
The minimum window size is 1200 x 780. Underline tabs sit below the 44-pixel header;
Engineering adds Overview, Program blocks and Version control sub-tabs. The body has
an expanding main column and a 300-pixel side column. The 28-pixel status bar shows the
MCP state, TIA version/address/project, and a clickable pending-approval badge.
Engine & MCP settings opens the settings drawer with English/中文 and Auto/Light/Dark
choices. Language and theme changes keep the same ViewModel, session, selection and commands.
Ctrl+S saves the project; Ctrl+1 selects MCP & clients, Ctrl+2 selects Engineering, and
Ctrl+3–8 select the remaining pages. The release picker lives in the installation card
and project options. Save both configurations is in the clients container footer.

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

Set `TIA_PRIMER_SCREENSHOTS` to a local output directory before running the WPF tests below to
capture the English and Chinese fixtures in both themes. These are actual WPF renderings at
1200 x 780 with synthetic data, without connecting to TIA. `TIA_WORKBENCH_RENDER_DIR`
enables the full page, drawer, toast and confirmation matrix in both languages and themes.

## Bridge channel

The desktop and Framework bridge use `TiaMcp.WorkerChannel` protocol 2 with an
explicit `Studio` profile. Studio selects the exact release before starting the
bridge, then validates the bridge and deployed-adapter hashes and a fresh 32-byte
nonce during hello. The bridge loads the adapter before hello but does not create an
Openness session until a related request arrives. If no release is selected, the
desktop chooses the newest installed version; a diagnostic or mock session without
an installation uses release `21`.

Requests, results, and progress use the shared Contracts `BridgeJson` codec and one
`System.Text.Json` options factory. DTO properties remain PascalCase; wire method
and progress property names retain their established lowercase spelling, and enums
remain names. The RPC routes `session.state` and `doctor.run` continue to dispatch to
the Studio methods `GetState` and `Doctor`; these are Studio RPC/API names, not MCP
tool names. `RpcResponse` distinguishes a missing `result` from explicit
`result:null`; errors omit `result`, and success omits `error`. The desktop preserves
the prior date-token conversion behavior when decoding RPC DTOs.

RPC errors keep `error.data.outcome`, `evidence`, and the original `rpc` code,
message, and data. A rejection before backend entry is `RejectedBeforeNative`, a
read failure is `ReadFailed`, and a failure after entering a potentially
state-changing method is conservatively `Unknown`; this classification does not
prove that a native call occurred. A handled RPC error is shown to the user while
the Studio bridge session remains usable, including `Unknown`. Protocol faults,
timeout, and cancellation after dispatch invalidate the session. The client does
not retry, replay, or automatically restart a failed bridge. Undispatched
cancellation does not consume an id. The default call budget is ten minutes, and
disposal waits up to five seconds before ending an owned bridge process.

Progress retains `{operation,current,total,message}` in `params.payload`, together
with protocol 2 request id, sequence, and percentage. It is bound to the originating
request, validated before notifying the UI, and does not extend the call budget.
Binding epochs track managed `session.connect`, `session.disconnect`, `project.open`,
and `project.close` commands only. They do not call `GetState`, read Siemens objects,
or detect an external project rebind. Siemens calls remain sequential on the bridge
STA thread.

The UI displays handled errors through the existing message paths: Workbench activity
uses `Exception.Message` in status and logs, version-control diffs use it as the
caption, bridge stderr retains the original method/code/message, and the unhandled
exception dialog and crash log keep their existing text. Result-level export,
compile, inspection, VCI and progress messages remain in their DTOs.

The Core golden tests cover all 29 Contracts DTOs, defaults, nulls, enum names and
date/time cases; the RPC method tests compare requests, responses, backend arguments,
call order and progress. Client tests also cover identity rejection, all Studio
error cases, cancellation before and after dispatch, timeout, concurrent calls,
child exit, handled errors followed by another call, late progress, and disposal.
Run the offline bridge smoke after `dotnet run --project build-tools/release -- build-studio` with local SDK files:

```powershell
python tests/Studio/Test-BridgeSmoke.py --bridge src/Studio/Gui/bin/Release/net10.0-windows/bridge/TiaOpenness.Bridge.exe --public-api-root <local-sdk-root>
```

It checks hello identity, `session.state`, `ping`, `doctor.run`, and clean EOF for
V14 SP1, V16 and V21. These checks do not connect to TIA or replace native
acceptance.

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

The configuration page is the compiled `src/Studio/Gui/Configuration/ConfigurationView.xaml`
UserControl. Its code-behind retains the configuration/service logic and loads the Studio palettes
and its own compiled English/Chinese dictionaries. `src/Studio/Launcher` contains only the
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

Live 4.0 TIA project acceptance remains pending. `dotnet run --project build-tools/release -- build-multi-version` deploys the desktop to `runtime/studio`; the formal release pipeline includes Studio and all eight adapters through `Package-Release.py`. `Package-MultiVersion.py` remains available for local development archives. Full audit findings are in
[the shared version framework](../../docs/development/unified-version-framework.md).
