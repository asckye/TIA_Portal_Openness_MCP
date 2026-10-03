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

- Each tool has its exact input schema, input origins, representative values and
  discovery tools filtered to this engine. A matching curated call is retained;
  other operations receive an explicitly labeled schema template. Optional inputs
  remain visible in `parameterSources` and `inputSchema`.
- Full-engine return fields come from its actual return type. Result interpretation
  and expected outcomes use the same library. Foundation envelopes use their own
  tool contract descriptions. Existing version policy supplies operation variants
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
its assets, `sequences.json`, and `metadata.json`. Run
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
or native-verified calls.

## Verification

Actual MCP checks retrieve every tool/operation, compare schemas and example keys,
check input origins/results, reconstruct source pages and verify file hashes.
They validate sequence signatures and compatibility entry points, and execute the
DB/UDT example inputs through the real offline XML builders. A .NET Framework
harness retrieves all languages to catch differences from the .NET 8 unit suite.

Full/lite and STDIO/HTTP are checked. Foundation fake-worker tests verify retrieval
does not invoke the worker. The eight-version audit writes the
[coverage record](../../manifest/tool-usage-coverage.json). Native PLC/HMI
import/compile acceptance of the new sources remains NOT RUN.
