# AI-facing tool usage and official examples

Every registered tool in all eight release profiles now routes to `GetToolUsage`.
The tool is in the V20/V21 lite roster and the complete foundation rosters. Its
tool-specific result uses the current engine's actual input schema, a curated MCP
example where available or an explicitly labeled schema template, prerequisites,
failure handling, source-release limits and related official references. Same-name
foundation and full-engine tools retain their different contracts.

The embedded reference corpus contains all 36 Markdown documents from the pinned
32 Siemens AI extension guide groups, and all 41 C# source files, eight project
files, README, license and two root build files from the pinned Siemens snippet
repository: 89 complete documents and 381 indexed code blocks/public methods.
This includes setup, imports, dependencies and caveats, not isolated code fragments.
Reference code is never compiled or executed by the MCP host.

- [Siemens AI extensions](https://github.com/siemens/tia-portal-ai-extensions),
  commit `b5c7041648dc10f9225ef082306ade6c335b8771`, MIT.
- [Siemens code snippets](https://github.com/siemens/tia-portal-openness-code-snippets),
  commit `4a8cc79d0666633e524e52f3335d99ff993f8830`; source code is MIT under
  section 2 of the retained [upstream license](../../reference/siemens-code-snippets/LICENSE.md).
  `Directory.Build.props` selects V21 and fixtures use `.zap21`; its README still
  says V20. The catalog exposes this discrepancy instead of treating the README
  as proof of old-release compatibility.

The scope is the entire pinned reference corpus, not every example ever published
in every Siemens manual. Related API patterns are distinguished from official
implementations of MCP wrappers. Project-defined tools and API areas without a
direct vendored example say so explicitly. Additional verified manual links for
HMI, SiVArc, OPC UA and VCI are filtered to the selected release. There are no
fabricated manual counterpart URLs or claims that a generic topic covers every
action of a tool.

## Calling the catalog

```json
{"name":"GetToolUsage","arguments":{"toolName":"ManageStartdriveParameter"}}
```

Read an exact `officialReference.documents` ID through `documentId`. Continue
with `nextOffset` until null. Use `query` to search all embedded source text.
Empty selectors list registered tools and reference documents; tool-name and
document listings have separate continuation offsets. If the normal MCP response
guard returns an `exportId`, concatenate `GetExport` character pages before parsing
the complete JSON. The reference tool needs neither network access nor a source
checkout and does not call the worker, attach to TIA or inspect a project.

Resolve placeholder identities and paths before calling the target tool. A schema
template checks names and types, not engineering semantics. Official examples
may contain writes, online operations and automatically starting setup routines;
their presence in the reference library does not request or authorize execution.

The six pre-existing curated examples for `CompileDevice`, `ManageHardwareObject`,
`ManagePlcProtection`, `ReadPlcSimAdvancedTags`, `WritePlcSimAdvancedTags` and
`RunPlcSimAdvancedTestScenario` now encode JSON-string arguments as strings rather
than JSON arrays/objects. The simulation write example starts with its preview.

For the reported BICO crash, read [the incident and exact-read change](startdrive-bico-read-regression.md).
`dryRun=true` can still read native parameters. No example or preflight establishes
that `p2051[0]` is safe on the affected device; the target and native crash cause
remain unconfirmed.

## Maintenance and verification

`scripts/generate/Generate-ToolUsage.py` deterministically builds the embedded
catalog from pinned reference files and tool mappings. `--check` rejects stale
content in CI and the full release build. Changes to the generated JSON are
included in both build source-hash records.

Actual MCP transport checks retrieve usage for every advertised tool, compare
schemas, validate example keys/types/enums and preview flags, verify exact release
identity, and reconstruct every reference document to check its SHA-256. Full/lite
and STDIO/HTTP discovery are tested. Foundation tests additionally use a fake worker
to prove that guidance does not call it even with native access enabled.

`scripts/diagnostics/Audit-ToolUsage.py` requires all eight actual tool inventories
to match the coverage results, and writes [the coverage record](../../manifest/tool-usage-coverage.json).
Missing, obsolete or inaccessible tool mappings fail this check. These checks
execute guidance and metadata retrieval, never the sample engineering operations.
Native acceptance remains separate.
