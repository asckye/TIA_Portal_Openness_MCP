# Multi-version validation (unreleased)

All eight exact PublicAPI adapter targets and Studio adapter targets compiled. Six
foundation runtime directories and the direct-Openness Studio desktop were assembled.
V20/V21 completed the required full Build-Release workflow. No native TIA process,
project, device, VM or PLC was operated in this change.

| Validation | Result |
|---|---|
| Offline core suite, V21 and V20 symbols | 3,055 passed each |
| MCP SDK version policy | 10 passed |
| Foundation contracts and fake native callbacks | 4,103 passed; one explicit native/read-lock scope skip |
| Configurator | 157 passed |
| Studio client / mock bridge | 45 passed |
| Studio WPF, including exact release selection and Chinese switch | 608 passed |
| Exact SDK input checks | 20 passed |
| Worker adapter selection and instrumentation | 34 passed, including eight corruption-test invocations |
| Foundation actual transport | Six STDIO releases; two independent HTTP sessions; Chinese roundtrip and dependency-order execution |
| Full-engine actual transport | 28 HTTP, 18 HMI and 44 resource checks per release; dependency planner invoked over STDIO/HTTP |
| Full-engine stability | 17,936 tool calls across ordinary/isolated processes, both transports and both profiles |
| Existing ecosystem functionality | 31 checks per release; companion Python and PDF report exercised |

The isolated working copy initially lacked the companion Python environment; using the
existing project environment via TIA_MCP_PLC_TOOLS_PYTHON resolved the failed PDF check,
and the full workflow then passed without reducing checks or substituting hash records.

Build records: [full engines](../../manifest/release-build.json),
[configurator](../../manifest/configurator-build.json),
[multi-version runtimes and source inputs](../../manifest/multi-version-build.json).
New transport coverage is also wired into GitHub Actions without requiring Siemens SDKs.

The current handoff replaces stale branch/VM/route assertions. README EN/ZH, Studio,
runtime, configuration, API coverage and provenance entries now describe current scope.
Dated audits are labeled historical; original release evidence remains in Git history.
The local user handoff, SDKs, design assets and upstream licenses are preserved.

This is a development build, not a new public release. New native acceptance remains
pending per release, especially import/export/compile roundtrips and both VCI API families.
