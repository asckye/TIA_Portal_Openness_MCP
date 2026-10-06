# Tools by TIA release

The current source supports exact release keys `14sp1`, `15.1`, `16`, `17`, `18`,
`19`, `20`, and `21`. Original V14 and V15 are excluded. Selecting another release
does not translate or upgrade a project.

## MCP profiles

| Version | Advertised catalog | Implementation |
|---|---:|---|
| V14 SP1 | 59 | Foundation host and V14 SP1 worker |
| V15.1 | 60 | Foundation host and V15.1 worker |
| V16 | 62 | Foundation host and V16 worker |
| V17 | 62 | Foundation host and V17 worker |
| V18 | 62 | Foundation host and V18 worker |
| V19 | 64 | Foundation host and V19 worker |
| V20 | 477 | Full engine |
| V21 | 488 | Full engine |

V14 SP1–V19 advertise their implemented Foundation subset. They do not offer the
full engine's lite profile or dispatch bridge. V20/V21 use the 60-tool lite profile;
`FindTools` and `CallTool` can reach the full registered catalog. The eight release
catalogs contain 1,334 version/tool combinations and 496 distinct V4 names.

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

The 496-name catalog includes 49 names available in all eight releases, 436 shared
by a subset, and 11 exclusive to V21. Those counts describe registration, not native
acceptance or independent implementations. See the [generated release catalog](version-tool-catalog.md)
for the most recently built package's exact names.

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
