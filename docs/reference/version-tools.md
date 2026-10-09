# Tools by TIA release

The current source supports exact release keys `14sp1`, `15.1`, `16`, `17`, `18`,
`19`, `20`, and `21`. Original V14 and V15 are excluded. Selecting another release
does not translate or upgrade a project.

## MCP profiles

| Version | Advertised catalog | Implementation |
|---|---:|---|
| V14 SP1 | 157 | Foundation host and V14 SP1 worker |
| V15.1 | 158 | Foundation host and V15.1 worker |
| V16 | 160 | Foundation host and V16 worker |
| V17 | 160 | Foundation host and V17 worker |
| V18 | 160 | Foundation host and V18 worker |
| V19 | 170 | Foundation host and V19 worker |
| V20 | 495 | Foundation host and V20 engine worker |
| V21 | 506 | Foundation host and V21 engine worker |

V14 SP1–V19 advertise their full registered Foundation catalog, including all 28 F01
discovery, bridge, preview and batch tools (maintainer decision, 2026-10-08).
`FindTools` and `CallTool` reach exactly the selected release's registered catalog;
version gates, target approvals and batch project identity checks remain enforced.
Old releases have no lite profile; the bounded registry requires a lite design or
a reviewed bound change before reaching 257 tools. V20/V21 use the 81-tool lite profile.
The eight release catalogs contain 1,966 version/tool combinations and 507 distinct V4 names.

Newly enabled F01/F02/F03 behavior is `current / NOT RUN` until VM acceptance.
F02 offers 12 tools on old releases: V20/V21 schema validation, SIMATIC ML decoding,
SIMATIC SD inspection and the V21 alias/alarm LAD builder remain off. Render/Compare
accept exported files; native block-path mode returns `UNSUPPORTED_CAPABILITY` before work.
F03 offers three generic library/template tools on V14 SP1–V18 and adds eight Unified
offline design tools on V19. Nine Classic package/preflight tools remain off below V20.
Offline output remains a candidate; registration does not establish native import compatibility.


The 4.0 contract uses typed argument objects matching the selected tool's
`inputSchema`. Migrated calls return the V4 `schemaVersion` / `ok` / `data` / `error` / `meta`
envelope. Direct calls, `CallTool`, and batch entries preserve that shape. Names in
the 3.x catalog are not accepted as aliases. Same-named tools may still have
release-specific parameters or behavior; inspect the connected host's schema.

Use [`GetToolUsage`](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md) for the selected
release's schema, inputs, examples, results, programming files, and ordered call
sequences. To list sequences, use `exampleKind: "sequence"`; to select one, use
`exampleId: "sequence/<topic>"`. The former authoring-guide topics select the
corresponding `language`, `query`, or `exampleId` record.

## Release differences

- V14 SP1 has no ordinary watch-table listing; it is available from V15.1.
- The Foundation catalog adds hardware search and exact device creation in V19.
  The V18 signatures alone did not establish equivalent implementation behavior.
- V20/V21 advertise the wider PLC, HMI, hardware, library, online, and optional
  engineering families. The [Openness coverage review](openness-coverage.md)
  tracks per-release implementation gaps and API evidence.
- V21 adds eleven whole-tool routes beyond V20, including communication
  connections, safety activation tests/functions, PLC block write protection,
  drive safety acceptance tests, SiVArc screen layout, and Classic HMI graphics.
  Shared tools can also have action-level version exclusions.

The 506-name catalog includes 114 names available in all eight releases, 381 shared
by a subset, and 11 exclusive to V21. Those counts describe registration, not native
acceptance or independent implementations. See the [generated release catalog](version-tool-catalog.md)
for the most recently built package's exact names.

## Workbench control

All eight releases expose `ShowWorkbenchPage`, `ShowWorkbenchBlock`,
`ShowWorkbenchCall`, `ShowWorkbenchLadder`, `ShowWorkbenchAtlas`,
`GetWorkbenchState`, `GetWorkbenchSelection`, and `PrefillWorkbenchForm`.
The six display/prefill tools use operation `UI`; state and selection use `READ`.
They belong to the `Workbench` domain and are available without TIA, including
V20/V21 full and lite profiles. UI tools cannot run in batches. A running local
Workbench is required; the host never launches it. Prefill awaits a person's
confirmation, and only Settings can change the UI control switch.

Until P8-21, `session.source = workbench-bridge` identifies the Workbench's own
session. Preserve exact project/software/block identities when locating targets.
See the [control channel](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md#工作台控制通道p8-20)
and [examples](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md).

## Studio

Studio is a direct Openness client with eight release-specific adapters and a
pre-connection release selector. PLC browsing, import/export, and compilation share
one workflow over the selected API. V14 SP1/V15.1 have no VCI; V16–V19 use the
WorkspaceMapping APIs; V20/V21 use MappedObject APIs. The adapter identity follows
the loaded PublicAPI assembly, not only the product folder.

## Build and package

The release package contains all eight MCP runtimes, Studio, and its eight adapters.
`dotnet run --project build-tools/release -- release` builds the public package after multi-version validation;
`Package-MultiVersion.py` creates a local development archive. SDK and PublicAPI
directories are local build inputs and are not included in the repository or public
package. See the [release workflow](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/release-workflow.md) and
[validation guide](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/validation.md).

`version-tool-catalog.md` is generated from built catalogs by the release audit. It
records the last built package and is regenerated during release validation; it is
not edited by hand or used as the current 4.0 source of names.
