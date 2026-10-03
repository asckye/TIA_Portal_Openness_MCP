# Studio integration and official API audit — 2026-10-03

> Historical implementation snapshot. Current version routing, direct Studio integration,
> tool counts and validation are maintained in [the release matrix](../reference/version-tools.md).
> Previous build/runtime restrictions and MCP-backed desktop descriptions below are superseded where that matrix says so.


> Superseded for Studio transport by the maintainer's direct-Openness decision on 2026-10-02 (local time). See [native Studio and duplicate review](studio-native-and-duplicates-20261002.md). The following is the historical MCP integration audit; its API inventory and original evidence retain their original scope.

Base: `offline/publicapi-validation`, `db2039e09be6e21b37cff4cbc719162aa502b212`, including the previous uncommitted local validation fixes.
This report records source review, exact local PublicAPI XML inventory and offline functional validation.
It is not native TIA acceptance or a statement that every official member has a working MCP wrapper.

## What was integrated

[Studio](../../tools/tia-openness-studio/README.md) is now a buildable WPF desktop using the existing
HTTP MCP engine for V20/V21. The source import is pinned to `87099c576fbc06e6b6ac523ddbf763fe0aa2ce02`.
It includes the UI, localization/themes, DTOs, typed client, synthetic backend, inspection and Git-diff
helpers, and upstream tests. The native adapter/compiler/MCP-server duplication is replaced in the
active build by the current engine's discovery/preflight/dispatch. The complete upstream source and
non-shallow Git history remain in reference archives; file hashes and refs are in `upstream.json`.

Normal desktop workflows cover PLC browser, XML/SIMATIC SD export, explicitly overwriting XML import
with readback, compile diagnostics, inspection, save, V21 workspace create/map/status/sync/diff.
The tools panel exposes the existing engine for advanced operations, preserving its edition and
version behavior. This does not inherit the standalone Studio claim of native V15.1–V21 support.

## Fresh official XML inventory

The scan used all eight user-restored SDK directories and the repository's source-only
`Audit-OpennessCoverage.ps1`. It read XML and C# text; it did not load Siemens assemblies.
The following baseline was captured before this increment's engine dispatch changes. The scanner
compares each SDK against the **same 353-file full-engine source union**, including conditional
branches, identifiers in comments and same-name members. In particular, legacy rows are inventory
comparisons, not proof that the legacy adapters implement these members. Member reference counts
must not be divided into a runtime compatibility percentage.

| SDK | Domain members | Lexically referenced | Owner only | Unreferenced | Types | Untouched types |
|---|---:|---:|---:|---:|---:|---:|
| V14SP1 | 847 | 663 | 86 | 98 | 262 | 51 |
| V15.1 | 1209 | 994 | 97 | 118 | 379 | 59 |
| V16 | 1476 | 1198 | 134 | 144 | 476 | 70 |
| V17 | 1797 | 1461 | 167 | 169 | 578 | 88 |
| V18 | 2215 | 1812 | 210 | 193 | 703 | 107 |
| V19 | 3714 | 2022 | 406 | 1286 | 1043 | 111 |
| V20 | 4016 | 2234 | 442 | 1340 | 1113 | 115 |
| V21 | 4480 | 2382 | 590 | 1508 | 1215 | 115 |

Summary inputs are retained in [evidence/api-audit-20261003](evidence/api-audit-20261003).
Full per-member/type/namespace CSVs are included in the local audit evidence deliverable.
The V21 untouched types by assembly are: `Siemens.Engineering.Base`: 81, `Siemens.Engineering.DCC`: 5, `Siemens.Engineering.Startdrive`: 5, `Siemens.Engineering.Step7`: 22, `Siemens.Engineering.WinCC`: 2.
Many are compositions, metadata or option-specific APIs, so 115 untouched types does not mean
115 independent missing product features. Dynamic coverage entries remain claims with their recorded
evidence; reflection is constrained by resolvability, parameter binding and policy.

## Concrete findings and changes

| Finding | Result |
|---|---|
| External-source import tried multiple reflection methods, names and overwrite enum values after native errors/null results. | Replaced with the exact V20/V21 `PlcExternalSourceComposition.CreateFromFile(string,string)` call, once. |
| External-source generation tried alternative overloads after exceptions and guessed enum defaults. | Uses the documented parameterless `GenerateBlocksFromSource()` once. Native failure is reported as uncertain; no automatic alternate call. |
| VCI mapping retried the same object at the root after a subdirectory export exception. | Chooses the already-established flat root layout before exporting; one export per object. |
| Desktop integration would otherwise need to parse localized VCI strings. | Existing VCI responses now also include structured workspace/object/count metadata; existing text is retained. |
| Hardware names and PLC software names differ. | Optional `GetDevices(includePlcSoftware:true)` provides software names through the existing typed traversal. Ambiguous names remain engine errors. |
| Studio Doctor could accidentally call the current engine's default `fix:true`. | The desktop explicitly sends `fix:false`. |
| Studio's overwrite selector differs from the engine's single-XML importer. | Explicit overwrite succeeds through the real tool contract; No is rejected before dispatch instead of silently overwriting. |
| Old coverage prose claimed generic reflection reaches any public member. | Corrected the claim and clarified that lexical hits are not executable-path evidence. |
| Git preview omitted untracked files when VCI supplied an extensionless path, or when tracked changes were also present. | Resolve actual untracked filenames with Git and include their diffs; tested in real temporary repositories, including Unicode names. |
| Studio inspection's `RequireBlockComment` actually checks author metadata. | Documented the real rule; kept the existing behavior and added a regex execution timeout. |

The official local XML contains these signatures for **both V20 and V21**, and the
[Siemens source-generation topic](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/generating-blocks-from-source)
documents offline/ASCII prerequisites and replacement of existing blocks. The dispatch correction
does not establish complete duplicate-source handling, source encoding validation, lossless content
round trips or all-device offline guarantees. Those remain native/feature work rather than claimed fixes.

## Remaining gaps and priority

1. **Native functional acceptance:** run the integrated desktop against an explicitly authorized scratch
   project in V20 and V21: browse, nested-group export, import/readback, compile, VCI map and sync.
   Current results use mocks/in-memory HTTP, not Siemens execution. Native single-source and VCI failure
   outcomes must also be checked. Full Build-Release remains outside the present authorization.
2. **Legacy migration:** preserve the 62-tool ledger (55 implementations/candidates, 7 pending,
   7 exact-compatible migrations). The seven pending tools remain EnsureOpennessUserGroup,
   PlcBuildAndImport, ImportPlcExternalSource, GenerateBlocksFromExternalSource,
   ImportTechnologyObject, ImportTechnologyObjectsFromDirectory and SeedProjectFromReference.
   Full-engine fixes here do not close their separate legacy migration gates.
3. **Desktop scope:** root PLC user-block grid currently omits UDT, software/safety-unit, system-block
   and HMI browsing. Advanced APIs remain in the tools panel. Session ownership stays with the MCP
   client; native launch/open/close/disconnect are not duplicated. VCI restore retains engine edition
   restrictions. No-overwrite XML import needs an atomic backend contract before the dialog can offer it.
4. **Existing official audit gaps:** native lifecycle/ownership tests; cross-client session coordination;
   cache invalidation after edits outside this client; consistent optional-read diagnostics; device-specific
   Safety/Startdrive/Unified/technology-object acceptance and documented XML envelopes. Continue from
   [the official workflow audit](official-openness-audit-20260929.md), not historical claims of 100% coverage.
5. **Packaging:** Studio source builds with .NET 10; it is not yet added to the released self-contained
   delivery ZIP. Release-gate hashes remain stale for this WIP. No release, tag, main-branch merge or
   runtime-entry enablement is implied by this integration.

## GitHub projects worth reusing

| Project | Fit | Decision |
|---|---|---|
| [asckye/tia-openness-studio](https://github.com/asckye/tia-openness-studio) | Desktop UI, mock, inspection, Git workspace UX; MIT. | Integrated at the pinned commit; keep one native engine. |
| [Czarnak/tia-git-addin](https://github.com/Czarnak/tia-git-addin) | MIT; structured SimaticML and graphical LAD comparison. | Its Core parser is already vendored here. Next useful work is connecting the existing visual diff to Studio, not importing another engine. |
| [EidoAut/EidoTiaWorkbench](https://github.com/EidoAut/EidoTiaWorkbench) | Classic WinCC HMI scan/export/import/validation workspace. | Good next reference for the HMI desktop gap; inspect exact file licenses/API calls at a pinned commit before reuse. No new source imported this turn. |
| [Siemens MAC use cases](https://github.com/siemens/modular-application-creator-use-cases) | Official project-generation/equipment-module examples. | Useful for a template/generation integration after matching the installed MAC version; avoid making MAC a mandatory runtime dependency. |
| [Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp) | Independent V21 worker, fake-worker and documented operation architecture. | Use as a comparison for contracts and test scenarios; wholesale merging would duplicate native ownership and transport. No new source imported. |

The existing 86-entry [V21 ecosystem inventory](../../reference/v21-ecosystem.json), bundled PLC Tools,
OPC UA model generator and SimaticML decoder remain available. New candidates above are recommendations,
not new native compatibility claims. External code was reviewed as source, not executed for research.

## Repository deletion request

The user authorized deleting `asckye/tia-openness-studio` **after integration** through the GitHub plugin.
The installed GitHub connector exposes file deletion but no repository deletion/administration mutation.
Plugin discovery confirmed GitHub is installed; there was no second repository-administration plugin in
the returned results. No delete request has been sent, and the remote repository remains intact.
This is a missing callable capability, not a user-approval rejection. The backup covers Git history and
the complete source tree; it does not claim to back up GitHub release assets, settings or issue metadata.

## Validation status

| Check | Result | Scope |
|---|---|---|
| Studio core + integration | 36 passed, 0 failed, 0 skipped | Actual typed client/HTTP codec with an in-memory engine fixture, synthetic backend, inspection, and real temporary Git repositories. |
| Studio WPF | 505 passed, 0 failed, 0 skipped | Localization, controls, theme layout/rendering; no TIA. |
| Studio desktop build | 0 warnings, 0 errors | .NET 10 WPF; no Siemens references or native bridge. |
| Main engine offline suite | 3035 passed, 0 failed, 0 skipped | Existing pure managed suite, not native operations. |
| External-source method bodies | 11 assertions passed | Actual source methods compiled with managed API fakes: root/user-group import, generation, exact arguments, single dispatch. |
| Complete V20 engine | Actual Csc, 23 warnings, 0 errors | Compile-only against local V20 PublicAPI. |
| Complete V21 engine | Actual Csc, 21 warnings, 0 errors | Compile-only against local V21 PublicAPI. |

The integration workflows exercise browse, nested block paths, XML/text file export, overwrite import,
compile, inspection, save, VCI create/map/status/sync and MCP result decoding. Normal reported tool
errors can be corrected without restarting Studio; transport response loss is not automatically replayed.
The in-memory fixture is a client contract test, not a real HTTP server or native engine execution.
The current increment does not add speculative fallback API calls or repeatedly re-read binding on
every desktop read. The existing engine remains responsible for its operation policies.

Earlier in this local continuation, the separate legacy suite reported 4103 passed, 0 failed, 1 skipped;
all eight adapter/worker compile targets and static PE inspection passed. Those checks are recorded in
[the preceding validation record](local-development-validation-20261002.md); they were not rerun solely
for the new desktop. No legacy migration status or production enablement was promoted.

Logs, source SHA-256 inventory, official SDK inventory CSVs and complete upstream backups are
retained in the local `studio-api-evidence-20261003.zip` deliverable. Compile evidence records actual
Csc execution with `DesignTimeBuild=true`; no weaving, JIT or native artifact execution occurred.
No TIA launch/attach, project opening, PLC operation, user-group repair or production release workflow
was performed during this work.
