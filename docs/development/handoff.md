# Current development handoff

## 1. Current state

The only maintained branch is `master`. The current development target is V14 SP1,
V15.1 and V16–V21. The configurator selects exact release keys. V14 SP1–V19 use
the PLC foundation host and one typed worker per release; V20/V21 retain their
full engines. These profiles intentionally have different tool contracts and scope.
Studio uses direct Openness through eight adapters, with Chinese/English Glass views.

Use [the version matrix](../reference/version-tools.md), `manifest/version-tools.json`,
`manifest/version-api-audit.json`, `manifest/multi-version-build.json` and
`manifest/release-build.json` for current numbers and evidence. Native acceptance
for the new routes is NOT RUN. Compile/transport/mock results do not establish it.

## 2. Shared code and GitHub reuse

All registered tools expose [AI-readable usage and official examples](official-tool-usage.md)
through `GetToolUsage`, including foundation and full/lite profiles. Its embedded
corpus is generated from pinned Siemens references. Regenerate with
`scripts/generate/Generate-ToolUsage.py`; CI rejects stale content, and the
multi-version build checks actual per-tool retrieval in `manifest/tool-usage-coverage.json`.
Templates and topic references are not native acceptance or official MCP wrappers.

The library now also resolves `operation`, `language` and `exampleId`. Edit call
sequences, programming assets, input origins and result interpretation together in
`reference/tool-examples`; do not create separate issue-specific guide systems.
The older `GetAuthoringGuide` and `GetRecipe` entries reuse this data. Initialization
points to examples instead of imposing a repeated guide/preflight workflow. Preserve
functional tool checks. `calls.json` now holds separate foundation/full-engine
calls for the entire registered surface and action overrides; the old inline C#
example table reads this same data. Keep dynamic target bindings explicit and do
not claim native acceptance from schema validation. Coverage distinguishes parameterized calls,
complete sources, fragments and exported-module edits; native acceptance is separate.
The transport audit executes allowlisted in-memory examples on all eight releases.
Object property paths use `{property,name?}` steps; device/item name paths are a
different grammar. UDT/GlobalDB builders select an explicit exact output release;
other shared builders still emit V21 XML. Preserve output-format notes and exact
project extensions such as `.ap15_1`. Foundation external-source import/generation
now have a dedicated profile sequence in the same library, not a separate guide system.

Shared release identities, environment facts and dependency planning live under
`tools/openness-shared` and the linked version catalog. Native API-bound assemblies
are compiled separately. Preserve the foundation worker contracts and full-engine
contracts while sharing independent algorithms; do not merge them by tool name alone.

The Eido MIT dependency planner is pinned with its license and modifications under
`tools/third-party/eido-import-planner`. Existing Siemens guidance, TiaGitAddIn.Core,
PLC Tools and other integrations remain in place. Check source availability, license,
actual integration points and the version-specific API before adding upstream code.

## 3. Remaining work

A [p2051[0] BICO read crash](startdrive-bico-read-regression.md) was reported. The exact
read path now avoids detailed property/bit traversal; native acceptance and the actual
crash cause remain unconfirmed. This report supersedes any blanket BICO-read claim.

The historical 62-tool migration ledger is not the full official API gap count.
Its external-source import/generation routes now have typed implementations; five
other pending names and broader API families remain. The current functional matrix
in `reference/version-feature-matrix.json` distinguishes actual routes, SDK evidence,
partial scope, unsupported versions and native acceptance.
Foundation compilation now checks top-level, ungrouped and nested project devices
instead of refusing every execution. It requires positive Offline observations
for the selected PLC and all observable project providers. HMI/passive devices without
a provider are listed in `offlineStateNotExposedByDevices`, not asserted Offline; the
official all-device offline prerequisite still applies. Compiler
diagnostics use the documented typed fields and recursive message collection.
VCI uses legacy APIs on V16–V19 and modern APIs on V20/V21; V14 SP1/V15.1 have no VCI.
Real project import/export, compile and VCI roundtrips still need per-version acceptance.

## 4. Build and commit rules

After engine/test changes, run `scripts/build/Build-Release.ps1` against the authorized
V20/V21 PublicAPI directories. For all targets, use:

```powershell
pwsh -NoProfile -File ./scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

This runs the full build unless `-SkipFullEngines` is explicitly chosen after a fresh
full build. It then builds workers/Studio, runs functional tests, audits the official
XML and writes runtime/source hashes. Do not edit compiler inputs during a build.
Set `TIA_MCP_TEST_PUBLIC_API_ROOT` to the SDK root to include eight-release UDT/GlobalDB
interface-XSD tests; native import/CPU semantics remain separate. Run repository links,
dead-tool references and strict bundle validation before commit.
Use explicit `git add` paths, never `git add -A`; preserve local SDKs, handoff files
and user design references. Keep binaries out of Git. Do not add AI sign-off lines.

After committing reviewed source/manifests, `Package-MultiVersion.py --output <new.zip>`
checks source/runtime hashes and creates a local all-version development archive.
`Release.ps1` and `Package-Release.py` remain the separately authorized public-release
workflow. A build or commit does not authorize tags, uploads, deployment or PLC actions.

## 5. Native testing and history

Live operations require an explicit current target and scope. Historical VM addresses,
PIDs, test project state and permissions are not current facts. Never reuse an old PID
from a document as authorization to attach or mutate. No new TIA session, project or
PLC was operated during this multi-version implementation.

Historical releases and empirical API notes remain in [handoff history](handoff-history.md),
[the dated machine checklist](handoff-checklist.md), and [the previous handoff snapshot](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6f3a5e1f2d4554f2adfefce883843c4bb8f5d6d2/docs/development/handoff.md).
The old V21 lexical coverage statistics are historical evidence, not a current coverage
percentage. PublicAPI DLLs/XML and user design handoffs must not be redistributed.
