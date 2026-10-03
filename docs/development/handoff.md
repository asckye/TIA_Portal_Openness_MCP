# Current development handoff

## Current state

Published [v3.2.0](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/v3.2.0) from `299f947d4b54a92873e9321a39f6b3dc39279e11`. Both pre-release CI workflows and the independent published-asset verification passed. The release ZIP includes the original beginner guide; the subsequent documentation cleanup is maintained on master. See [publication evidence](../../manifest/publication-v3.2.0.json) and the [current beginner guide (Chinese)](../getting-started/beginners.zh-CN.md).

The v3.2.0 release build includes eight MCP runtimes, the configurator and direct
Openness Studio. All eight exact SDK targets have compiled; the full V20/V21 and
multi-version functional build gates have completed. Publication and independent
GitHub asset verification are separate steps: consult the actual
[GitHub Release](https://github.com/asckye/TIA_Portal_Openness_MCP/releases) for their
current outcome. New native acceptance remains **NOT RUN**.

Only `master` is maintained. Exact release keys are `14sp1`, `15.1`, `16`, `17`,
`18`, `19`, `20`, `21`; original V14/V15 are excluded. V14 SP1–V19 use the PLC
foundation host and a matching typed worker; V20/V21 retain their full engines.
Studio connects directly through eight Openness adapters and has Chinese/English
Glass views. Selecting a version does not convert an existing project.

Current evidence is maintained in the [version matrix](../reference/version-tools.md),
[functional gap matrix](../../reference/version-feature-matrix.json),
[full build record](../../manifest/release-build.json),
[multi-version build record](../../manifest/multi-version-build.json), and
[tool-example coverage](../../manifest/tool-usage-coverage.json). Compile, mock,
transport and SDK-signature results do not establish engineering semantics.

## Architecture and examples

Use the [version framework](unified-version-framework.md) for profile differences,
session contracts and remaining integration boundaries. Shared environment facts,
version identity, diagnostics, example retrieval and dependency planning belong
in API-independent code; native assemblies remain compiled per exact release.
Equal tool names do not imply compatible schemas or response envelopes.

`GetToolUsage` is the single [tool and programming example library](official-tool-usage.md).
It supplies actual schemas, operation-specific calls, input origins, expected
results, language files and call sequences. Edit `reference/tool-examples` and
regenerate its embedded data together; do not create separate issue-specific
guide systems. The existing `GetRecipe` and `GetAuthoringGuide` compatibility
entries read the same library.

GitHub integrations retain their pinned source and licenses. Current reusable
components and provenance are documented in [the ecosystem reference](../reference/ecosystem-tools.md)
and `NOTICE.md`. New upstream code needs an actual integration point, compatible
licensing and evidence for the selected API; another project's version label
does not supply compatibility evidence.

## Remaining work

Prioritize real-project import/generation/readback/compile/explicit-save acceptance
for each release and both Studio VCI families. Foundation source import and
block-generation routes now have implementations; five names remain from the
historical 62-tool migration ledger. They are a subset of the broader API gaps,
not a total missing-tool count. The [roadmap](roadmap.md) names the remaining work.

Preserve these unresolved observations in [Openness limitations](../troubleshooting/openness-limitations.md):

- The reported `p2051[0]` BICO read crash has not received native reacceptance;
  the simplified read path does not establish its root cause.
- The Unified library script rename crash remains an unresolved native incident.
- Native PLC cross-reference remains restricted; neither offline evidence nor
  process isolation establishes that the unsafe native path is fixed.

Other shared XML builders still emit V21 candidates where their examples say so;
only the current UDT/GlobalDB declaration builders select all eight formats.
Foundation binding snapshots remain a separate, unwired lifecycle integration
boundary. Do not describe PID-only attachment as verified process-start identity.

## Build and release

After engine or test changes, run the full build with the authorized V20/V21
PublicAPI directories, then build/test every other target:

```powershell
pwsh -NoProfile -File ./scripts/build/Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Python <python.exe> -Test
```

The command runs the full build unless `-SkipFullEngines` follows a fresh full
build. Set `TIA_MCP_TEST_PUBLIC_API_ROOT` to include the eight official interface
XSD checks. Do not edit compiler inputs during a build. Use [validation](validation.md)
for check scope and [the release workflow](release-workflow.md) for public delivery.
The public pipeline verifies source/runtime hashes and ships all eight runtimes.

Stage explicit paths; do not use `git add -A`. Preserve user handoff/design files,
local SDKs and engineering projects. Keep binaries and private evidence out of Git.
No AI attribution is added to commits. A build is not authorization for a live TIA
or PLC operation; live testing needs an explicit current target and scope.

## Documentation maintenance

Keep current behavior here and in the linked reference pages. Old candidate notes,
local checkpoints, machine addresses/PIDs and duplicate handoff histories have
been removed; Git history preserves their original evidence. Machine-readable
SDK audits and migration ledgers remain available, with historical labels kept
separate from the current build manifests. Never reuse an old machine identity
or earlier test authorization as a current target.
