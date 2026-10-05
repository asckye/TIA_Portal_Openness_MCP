# PLC XML builders

Document id: `plc-builders`

The builders create XML without connecting to TIA. Their presence in a version's tool list does not mean every XML format they produce belongs to that version. Read `GetToolUsage(toolName="...")` on the selected engine for the current parameters and examples.

## Select the output format

| Tool | Output | Target format |
|---|---|---|
| `BuildPlcUdtXml` | PLC data type / `SW.Types.PlcStruct` | All eight releases, selected by `outputReleaseKey` |
| `BuildPlcGlobalDbXml` | Global DB / `SW.Blocks.GlobalDB` | All eight releases, selected by `outputReleaseKey` |
| `BuildPlcTagTableXml` | PLC tag table | V21 candidate XML |
| `BuildStructuredTextXml` | SCL XML fragment | V21 candidate XML |
| `BuildFlgNetCallXml` | One LAD network calling an FC | V21 candidate XML |
| `ComposePlcFcBlockXml` | SCL FC | V21 candidate XML |
| `ComposePlcFbBlockXml` | SCL FB, without an instance DB | V21 candidate XML |
| `ComposePlcLadFcBlockXml` | LAD FC containing FC-call networks | V21 candidate XML |

Use the exact release key: `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, or `21`. Foundation engines require an explicit `outputReleaseKey`; V20/V21 engines default to `21`, so pass the intended target explicitly. Changing an XML version marker is not a format conversion.

The declaration builders use Interface/v2 for V14 SP1, v3 for V15.1, v4 for V16/V17, and v5 for V18–V21. Interface-fragment XSD checks do not establish that an entire document, CPU or user-defined datatype can be imported into a real project.

## Prepare, import and check

1. Read the selected tool's example with `GetToolUsage`. Builder input is a JSON **string** parameter, such as `udtJson` or `globalDbJson`; do not pass its members as top-level tool arguments.
2. Build XML for the intended release and inspect the returned content. Build-only tools neither save a file nor import it.
3. Save the returned XML on the computer running TIA/MCP. Resolve the PLC and group paths from the actual project tree.
4. Use the current engine's `ImportType`, `ImportBlock` or `ImportPlcTagTable` example for the relevant artifact. Check the actual imported object and path.
5. Compile the PLC and examine the error count and nested diagnostics. Save the project after reviewing the result.

V20/V21 additionally expose `PlcBuildAndImport`. Its `dryRun=true` produces a plan; `dryRun=false` executes the import. Read its own example and output-format limits before using it. It is absent from the V14 SP1–V19 foundation catalog.

The structured-text builder covers a small operation vocabulary, not a general SCL parser. For expressions, loops, state machines and timers, use complete SCL source examples through the [SCL workflow](scl.md). The LAD builders only build FC-call networks; [general LAD examples](lad.md) use a different route.

## PLC data-type folders

V20/V21 expose `CreatePlcTypeGroup` for folders under **PLC data types**. It creates folders, not UDT definitions or library types. Get its exact preview/application parameters from `GetToolUsage(toolName="CreatePlcTypeGroup")`.

Use the actual PLC software path and a relative folder path such as `Common/Motors`. Missing parents are created and existing folders reused. Inspect `createdPaths`, `createdCount` and `alreadyExisted`. If a creation fails midway, the reported parent folders may already exist; read them back before repeating the operation. The tool does not automatically compile or save.

See [version support](../../reference/version-tools.md), [template files](templates.md) and the [central example catalog](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/catalog.json). New native import acceptance remains separate from offline checks.
