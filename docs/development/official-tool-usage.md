# Unified tool and programming examples

`GetToolUsage` is the single example library for every registered tool in all eight
release profiles. It returns the actual schema, operations, input origins, return
contract and examples. Supply arguments as a typed JSON object matching the selected
tool's `inputSchema`; do not wrap it in a JSON string. Migrated calls return the V4
`schemaVersion` / `ok` / `data` / `error` / `meta` envelope. Foundation and full-engine business data
remain release-specific.

```json
{"name":"GetToolUsage","arguments":{"toolName":"ManagePlcDocuments","operation":"import"}}
{"name":"GetToolUsage","arguments":{"language":"scl"}}
{"name":"GetToolUsage","arguments":{"exampleId":"scl-add"}}
{"name":"GetToolUsage","arguments":{"exampleId":"sequence/connect-project"}}
```

An empty call lists tools, languages, examples and reference documents. `query`
searches official text; `documentId`, `offset` and `limit` read complete source in
pages. Tool and document indexes have separate continuation offsets. When a large
response returns an export ID, assemble its `GetExportContent` pages before parsing JSON.

## Contents

- Each tool has a profile-specific parameterized call, its exact live input schema,
  input origins and discovery tools filtered to this engine. Operation records
  supply conditional inputs (for example a source file for `createFromFile`).
  `bindings` identifies unresolved target-dependent values, including JSON members;
  sample names and paths must also be resolved against the intended project.
  Optional input examples remain visible in `parameterSources`. The inline examples
  in discovery and errors now read the same data.
- All migrated direct calls, `CallTool` calls and batch entries use the same V4
  envelope shape. `GetToolUsage` records the profile-specific business fields inside
  `data`; operation variants still depend on the selected target, license, firmware
  and installed update.
- Programming examples include source files, encoding, release/update requirements,
  references and corrections. Complete sources, fragments and exported-module edits
  are distinguished. Existing templates are referenced instead of copied.
- Call sequences include ordered requests and expected results. The former recipe
  listing behavior is `GetToolUsage` with `exampleKind: "sequence"`; selecting one
  uses `exampleId: "sequence/<topic>"`. Former authoring-guide topics use the
  matching `language`, `query`, or `exampleId` selector. `InitializeEnvironment`
  points to examples without requiring a separate guide or preflight call.

Languages/formats: external SCL, SIMATIC SD SCL, LAD, FBD, mixed networks, DB, UDT,
multilingual S7RES, STL, GRAPH text fragments, Unified JavaScript and Classic VBS.
`language=csharp` lists the complete embedded official C# documents. External SCL
and SIMATIC SD use separate examples. Foundation profiles provide ASCII external
source import and block generation followed by explicit compilation/readback; use
`sequence/plc-scl-block-foundation` for their preview/confirmation contracts. V14 SP1
has a void generation API, so its inventory observations are not a native generated-object list.

Return-value variants, document import outcomes, event/global-module scripts,
compiler diagnostics and other tool details use the same data and retrieval path.

## Sources and maintenance

The editable library is `reference/tool-examples`: `languages/catalog.json` and
its assets, `calls.json`, `sequences.json`, and `metadata.json`. Call records are
separate for `plc-foundation` and `full-engine`; operation entries are argument
overrides on the tool's base example. Release format tokens expand at retrieval
(`.ap15_1` for V15.1, `.ap20` for V20, etc.). Run
`scripts/generate/Generate-ToolUsage.py` to embed it; `--check` detects drift.
Retrieval needs no checkout/network and executes no example.

The retained official corpus contains 89 complete documents and 381 indexed code
blocks/methods, including setup and dependency context:

- [Siemens AI extensions](https://github.com/siemens/tia-portal-ai-extensions),
  commit `b5c7041648dc10f9225ef082306ade6c335b8771`, MIT: 36 Markdown files in 32 groups.
- [Siemens code snippets](https://github.com/siemens/tia-portal-openness-code-snippets),
  commit `4a8cc79d0666633e524e52f3335d99ff993f8830`: 41 C# files, eight project files,
  README, license and two build files. Source licensing is retained in the
  [upstream license](../../reference/siemens-code-snippets/LICENSE.md).
  Fixtures/build properties select V21 while the README says V20; neither proves
  compatibility with older releases.

The corpus is not every example ever published by Siemens. Related API patterns,
project examples and missing direct official examples remain distinct. Templates
cover the registered surface; they are not all completed target-specific scenarios
or native-verified calls. Dynamic property bags deliberately require the object's
read/self-description: an invented property name cannot be made valid by an example.

The eight registered release profiles are V14 SP1, V15.1, V16, V17, V18, V19, V20
and V21. Coverage refers to their actual registered tools; it does not add modern
tools to older APIs. UDT and GlobalDB declaration builders explicitly select all eight
output releases, including the corresponding Interface v2/v3/v4/v5 namespace and
version-dependent object attributes. Other shared offline builders still emit V21
candidate XML on older hosts; their examples say so explicitly. Changing a file
extension or an XML version header is not a format conversion.

## Verification

Actual MCP checks retrieve every tool/operation, compare schemas and example keys,
check input origins/results, reconstruct source pages and verify file hashes.
They require a call record for every registered tool/operation, validate sequence
signatures and compatibility entry points, and execute the exact returned call
examples for eight XML/logic builders and dependency planning on every release.
Full engines additionally execute the Unified button-action example. Tests inspect
generated declarations, assignment tokens, call targets and dependency order.
Language-file DB/UDT examples are checked separately. A .NET Framework
harness retrieves all languages to catch differences from the .NET 10 unit suite.
When `TIA_MCP_TEST_PUBLIC_API_ROOT` points to the eight supplied SDK directories,
the foundation tests additionally validate generated UDT/GlobalDB interface fragments
against each release's official XSD. This does not validate a whole document or native import.

Full/lite and STDIO/HTTP are checked. Foundation fake-worker tests verify retrieval
does not invoke the worker. The eight-version audit writes the
[coverage record](../../manifest/tool-usage-coverage.json). Native PLC/HMI
import/compile acceptance of the new sources remains NOT RUN.

## Adding or changing an example

Use the selected release's real schema before editing a call. Put dynamic project,
PLC, group and path values in the record's bindings/input origins; paths refer to
the computer running TIA/MCP. Keep import, block generation, compilation and Save
as separate sequence steps when the tool contract requires them. A compiled block
is not automatically executed on a PLC, and import/compile success does not imply
the project was saved.

Choose a complete source, a fragment or an exported-module edit honestly. Update
the same record's operation-specific result interpretation, version/encoding
constraints and official source links. Run the generator and the functional
retrieval checks for every affected release; do not add a second guide system
for one API incident or language. Current build/test scope is in [validation](validation.md).
