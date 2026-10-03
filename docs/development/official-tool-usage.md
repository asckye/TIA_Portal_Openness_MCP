# Unified tool and programming examples

`GetToolUsage` is the single example library for every registered tool in all eight
release profiles. It returns the actual schema, operations, input origins, return
contract and examples. Foundation and full-engine contracts remain independent.

```json
{"name":"GetToolUsage","arguments":{"toolName":"ManagePlcDocuments","operation":"import"}}
{"name":"GetToolUsage","arguments":{"language":"scl"}}
{"name":"GetToolUsage","arguments":{"exampleId":"scl-add"}}
{"name":"GetToolUsage","arguments":{"exampleId":"sequence/connect-project"}}
```

An empty call lists tools, languages, examples and reference documents. `query`
searches official text; `documentId`, `offset` and `limit` read complete source in
pages. Tool and document indexes have separate continuation offsets. When a large
response returns an export ID, assemble its `GetExport` pages before parsing JSON.

## Contents

- Each tool has a profile-specific parameterized call, its exact live input schema,
  input origins and discovery tools filtered to this engine. Operation records
  supply conditional inputs (for example a source file for `createFromFile`).
  `bindings` identifies unresolved target-dependent values, including JSON members;
  sample names and paths must also be resolved against the intended project.
  Optional input examples remain visible in `parameterSources`. The inline examples
  in discovery and errors now read the same data.
- Full-engine return fields come from its actual return type. Result interpretation
  and expected outcomes use the same library. Foundation envelopes have separate
  field/interpretation records, including PascalCase raw results and preview versus
  executed compilation. Existing version policy supplies operation variants
  for different service families or object kinds; the selected target, license,
  firmware and installed update still determine native support.
- Programming examples include source files, encoding, release/update requirements,
  references and corrections. Complete sources, fragments and exported-module edits
  are distinguished. Existing templates are referenced instead of copied.
- Call sequences include ordered requests and expected results. `GetRecipe` reads
  these same records. `GetAuthoringGuide` resolves to this library, preserving its
  legacy topics. Initialization and Bootstrap point to examples without repeating
  mandatory guide/preflight workflows. Actual tool validation remains unchanged.

Languages/formats: external SCL, SIMATIC SD SCL, LAD, FBD, mixed networks, DB, UDT,
multilingual S7RES, STL, GRAPH text fragments, Unified JavaScript and Classic VBS.
`language=csharp` lists the complete embedded official C# documents. External SCL
and SIMATIC SD use separate examples. Foundation profiles currently plan external
source imports but do not expose native SCL generation.

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
tools to older APIs. Some shared offline XML generators still emit V21 candidate
XML on older hosts; those examples say so explicitly. Changing a file extension or
an XML version header is not a format conversion.

## Verification

Actual MCP checks retrieve every tool/operation, compare schemas and example keys,
check input origins/results, reconstruct source pages and verify file hashes.
They require a call record for every registered tool/operation, validate sequence
signatures and compatibility entry points, and execute the exact returned call
examples for eight XML/logic builders and dependency planning on every release.
Full engines additionally execute the Unified button-action example. Tests inspect
generated declarations, assignment tokens, call targets and dependency order.
Language-file DB/UDT examples are checked separately. A .NET Framework
harness retrieves all languages to catch differences from the .NET 8 unit suite.

Full/lite and STDIO/HTTP are checked. Foundation fake-worker tests verify retrieval
does not invoke the worker. The eight-version audit writes the
[coverage record](../../manifest/tool-usage-coverage.json). Native PLC/HMI
import/compile acceptance of the new sources remains NOT RUN.
