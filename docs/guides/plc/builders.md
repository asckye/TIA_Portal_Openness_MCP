# PLC XML builders

The builders create XML without connecting to TIA. Their presence in a version's tool list does not mean every XML format they produce belongs to that version. Read `GetToolUsage(toolName="...")` on the selected release's service for the current parameters and examples.

## Select the output format

| Tool | Output | Target format |
|---|---|---|
| `BuildPlcUdt` | PLC data type / `SW.Types.PlcStruct` | All eight releases, selected by `outputReleaseKey` |
| `BuildPlcGlobalDb` | Global DB / `SW.Blocks.GlobalDB` | All eight releases, selected by `outputReleaseKey` |
| `BuildPlcTagTable` | PLC tag table | V21 candidate XML |
| `BuildStructuredText` | SCL XML fragment | V21 candidate XML |
| `BuildFlgNetCall` | One LAD network calling an FC | V21 candidate XML |
| `BuildPlcFcBlock` | SCL FC | V21 candidate XML |
| `BuildPlcFbBlock` | SCL FB, without an instance DB | V21 candidate XML |
| `BuildPlcLadFcBlock` | LAD FC containing call or rung networks | V21 candidate XML |

Use the exact release key: `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, or `21`. On V14 SP1–V19 the builders require an explicit `outputReleaseKey`; on V20/V21 they default to `21`, so pass the intended target explicitly. Changing an XML version marker is not a format conversion.

The declaration builders use Interface/v2 for V14 SP1, v3 for V15.1, v4 for V16/V17, and v5 for V18–V21. Interface-fragment XSD checks do not establish that an entire document, CPU or user-defined datatype can be imported into a real project.

The LAD FC implementation also has internal formats for all eight releases: FlgNet/v1 on V14 SP1, v3 on V15.1, v4 on V16–V18 and v5 on V19–V21, with per-release fragment XSD checks. V14 SP1 refuses indexed components. Public registration and `outputReleaseKey` remain V21-only until each earlier release has recorded VM import and compile acceptance. XSD checks do not establish native acceptance.

## Prepare, import and check

1. Read the selected tool's example with `GetToolUsage`. Supply its typed object parameter, such as `udt`, `globalDb` or `ladFcBlock`, directly with exact camelCase fields. Do not encode it as a JSON string or pass its members as top-level tool arguments.
2. Build XML for the intended release and inspect the returned content. Build-only tools neither save a file nor import it.
3. Save the returned XML on the computer running TIA/MCP. Resolve the PLC and group paths from the actual project tree.
4. Use the current release's `ImportPlcType`, `ImportPlcBlock` or `ImportPlcTagTable` example for the relevant artifact. Check the actual imported object and path.
5. Compile the PLC and examine the error count and nested diagnostics. Save the project after reviewing the result.

V20/V21 additionally expose `BuildAndImportPlcArtifact`. Its `dryRun=true` produces a plan; `dryRun=false` executes the import. Read its own example and output-format limits before using it. It is absent from the V14 SP1–V19 foundation catalog.

The structured-text builder covers a small operation vocabulary, not a general SCL parser. For expressions, loops and state machines, use complete SCL source examples through the [SCL workflow](scl.md). `BuildPlcLadFcBlock` also accepts typed `ladFcBlock` rung networks: contacts, global-memory edges, comparisons, OR groups, shared-prefix fan-out, coil/set/reset, MOVE with ENO chaining, TON/TOF/TP, CTU/CTD/CTUD and user FC/FB calls with named pins. Timer/counter instances must be global paths; FB calls require a global instance DB, FC calls refuse an instance. CTU requires reset, CTD load, CTUD down/reset/load; auxiliary series start on the powerrail. Only MOVE/CALL accept `then` (ENO). Global constants and indexed component paths are supported; local symbols must start with a declared input/output. Each network selects exactly one `call` or `rung`, with at most 256 Access/Part/Call elements per rung network. Timer PT uses Time and refuses LT#/LTIME#; Output/InOut pins require symbols. Known instruction names and the MC_ prefix are reserved, without resolving block identity. There is no symbol/type resolution; compile is the authority. Read `GetToolUsage(exampleId="sequence/plc-lad-rung-build-import")` for complete rung and box inputs. The generic operation selector requires a tool action/operation parameter and cannot select the maintained rung/rung-boxes entries. Native import and compile acceptance remain **NOT RUN**; [general LAD examples](lad.md) cover library/system instructions and other routes.

## PLC data-type folders

V20/V21 expose `CreatePlcTypeGroup` for folders under **PLC data types**. It creates folders, not UDT definitions or library types. Get its exact preview/application parameters from `GetToolUsage(toolName="CreatePlcTypeGroup")`.

Use the actual PLC software path and a relative folder path such as `Common/Motors`. Missing parents are created and existing folders reused. Inspect `createdPaths`, `createdCount` and `alreadyExisted`. If a creation fails midway, the reported parent folders may already exist; read them back before repeating the operation. The tool does not automatically compile or save.

See [version support](../../reference/version-tools.md), [template files](templates.md) and the [central example catalog](https://github.com/asckye/automation-engineering-workbench/blob/master/reference/tool-examples/languages/catalog.json). New native import acceptance remains separate from offline checks.
