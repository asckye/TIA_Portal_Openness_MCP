# Multi-version validation (unreleased)

All eight exact PublicAPI adapter targets and Studio adapter targets compiled. Six
foundation runtime directories and the direct-Openness Studio desktop were assembled.
V20/V21 completed the required full Build-Release workflow. No native TIA process,
project, device, VM or PLC was operated in this change.

| Validation | Result |
|---|---|
| Offline core suite, V21 and V20 symbols | 3,071 passed each |
| MCP SDK version policy | 10 passed |
| Foundation contracts and fake native callbacks | 5,564 passed; one explicit native/read-lock scope skip |
| Configurator | 157 passed |
| Studio client / mock bridge | 45 passed |
| Studio WPF, including exact release selection and Chinese switch | 608 passed |
| Exact SDK input checks | 20 passed |
| Worker adapter selection and instrumentation | 34 passed, including eight corruption-test invocations |
| Foundation actual transport | Six STDIO releases; two independent HTTP sessions; Chinese roundtrip and dependency-order execution |
| Full-engine actual transport | 28 HTTP, 18 HMI and 72 resource checks per release; dependency planner invoked over STDIO/HTTP |
| Full-engine stability | 17,936 tool calls across ordinary/isolated processes, both transports and both profiles |
| AI-facing tool usage | All 496 unique names across eight actual MCP catalogs; parameter keys/types/enums and preview flags checked; 89 complete official reference documents retrieved and hash-checked |
| Existing ecosystem functionality | 31 checks per release; companion Python and PDF report exercised |

The isolated working copy initially lacked the companion Python environment; using the
existing project environment via TIA_MCP_PLC_TOOLS_PYTHON resolved the failed PDF check,
and the full workflow then passed without reducing checks or substituting hash records.

Build records: [full engines](../../manifest/release-build.json),
[configurator](../../manifest/configurator-build.json),
[multi-version runtimes and source inputs](../../manifest/multi-version-build.json).
The [all-tool usage coverage record](../../manifest/tool-usage-coverage.json) separates
curated MCP examples, schema templates and related official references. Example
engineering operations were not executed.
New transport coverage is also wired into GitHub Actions without requiring Siemens SDKs.

The current handoff replaces stale branch/VM/route assertions. README EN/ZH, Studio,
runtime, configuration, API coverage and provenance entries now describe current scope.
Dated audits are labeled historical; original release evidence remains in Git history.
The local user handoff, SDKs, design assets and upstream licenses are preserved.

This is a development build, not a new public release. New native acceptance remains
pending per release, especially import/export/compile roundtrips and both VCI API families.

The first GitHub transport run exposed a raw Windows path-string assertion after
successful worker calls. The check now verifies the same directory after host
path canonicalization, while preserving the exact release and argument count.
All six STDIO releases and both isolated HTTP sessions passed the local rerun;
the supplementary result is recorded in the multi-version manifest. Runtime
sources and binaries were unchanged by this test-only correction.

The official reference generator now explicitly orders path components without case
sensitivity. Python pathlib otherwise sorts differently on Windows and Linux; five
document positions differed in the first CI check. Source-only regeneration now
matches the shipped catalog byte-for-byte. Embedded references and engine source
were unchanged by this generator correction.
