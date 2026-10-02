# Unified version framework: first increment

This is a source-only development change, based on master
`fac2b6aab343257bb10af421ed30b1c65f82375f`. It is not a release or a claim of
runtime support for every target release.

## What is implemented

- One dependency-free, C# 5-compatible source catalog is compiled into both the
  configurator and engines: `Siemens/TiaVersionCatalog.cs` under the server source.
- Canonical keys are `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`.
  Scope starts at V14 SP1 and explicitly excludes original V14 and V15. V15.1
  remains included. Excluded keys are unsupported, not planned; they cannot be
  selected or used to instantiate an adapter. Service/minor keys are never
  decimal numbers or aliases for their major release.
- Only `20` and `21` have existing engine build targets and executable paths.
  Earlier entries are `planned`, have no executable paths and are rejected for
  execution. `existing-engine` does not mean every tool has native acceptance.
- The configurator derives its choices and runtime/source-tree engine paths
  from the catalog. V21 remains the default; existing integer V20/V21 settings,
  runtime-first lookup and source-tree fallback are preserved.
- The engine accepts `--tia-version 20` or `--tia-version 21`. Existing
  `--tia-major-version` and `-tia-major-version` aliases remain valid for these
  canonical keys. Missing, malformed, planned, unknown and conflicting options
  are rejected instead of silently falling back to another installed version.
- Without a detected installation, the default is the binary's compiled version,
  not unconditionally V21. A requested version must match the compiled engine
  after routing. A missing sibling or redirect loop guard can no longer fall
  through into cross-version Openness execution. A wrongly placed sibling with
  a different compiled version fails the same startup check.
- Static help/version/schema commands remain available without a supported TIA
  installation. Doctor reports unsupported detected installations as failures
  without looking up a nonexistent legacy engine. Explicit version flags still
  require a runnable key. Client registration reuses the resolved version for
  every CLI alias rather than detecting a different installation a second time.
- Existing feature gates reject unknown, planned and future versions. The
  established V20/V21 document-export and hardware-HMI differences are preserved.
- The shared source participates in both engine-source and configurator-source
  build evidence. Packaging's exact configurator input inventory is updated;
  no generated manifest hashes have been changed by hand.

`--tia-version` does not yet change the engine's internal integer version model:
planned service/minor versions are refused before conversion. A future adapter
must carry the complete descriptor through the resolver, project checks and
worker protocol before its catalog entry can become runnable.

## Second increment: documented adapters and capability admission

Official evidence is in [legacy release matrix](../reference/openness-release-matrix.md)
and [modern release matrix](../reference/openness-modern-matrix.md). This increment
adds executable source and offline tests, not a claim of complete native support.

### Adapter boundary

`OpennessReleaseContract` implements:

- Exact release keys and selected `PublicAPI/Vxx` directory parsing, including
  V14 SP1 and V15.1; the hosting installation version is not substituted.
- Historical, out-of-scope V14 `HW.ISoftwareContainer` / `HW.SoftwareBase` versus SP1+ concrete service
  names; read-only software acquisition through the explicit service interface.
- XML export/import overload selection by exact signature: V14 string versus
  SP1+ FileInfo. External-source CreateFromFile is deliberately excluded from
  this conversion because official examples retain its string path.
- Pre-V17 explicit-interface attribute reads, original-V14 constants versus
  user/system constants, and V16-to-V17 Unified collection renames.
- Original-V14 SCL XML body status `interface-only`; SP1 status `unverified`.
  Export success must never be presented as complete SCL source acquisition.
- Unbound contracts refuse native dispatch. A future adapter must supply an
  independently verified reference-assembly identity, which is compared in full
  to the candidate. Directory identity is checked separately. Runtime resolution
  must also validate module assemblies, especially V21 Step7/WinCC splitting.

Original-V14 branches are preserved as historical reference only; the contract
factory and selected PublicAPI parser reject both V14 and V15.
These are not selectable legacy engines. The current typed Portal still depends
on modern APIs. V14 SP1, V15.1 and V16–V19 require real reference inputs, full per-family extraction,
shape verification and adapter-host integration before any entry becomes runnable.
V18/V19 exact per-method coverage remains incomplete; common modern contract
shapes are candidates for shape validation, not newly certified compatibility.

### One admission policy across protocol paths

`ToolVersionPolicy` runs in full/lite discovery, category/search discovery,
preflight, bridge invocation and the outer MCP SDK wrapper. The outer wrapper
runs before ordinary dispatch or worker forwarding; the worker uses the same
registration path. Batch/transaction calls route through bridge/preflight.
Compiled engine identity drives admission, so informational commands can enumerate
without initializing TIA. Startup separately proves requested/compiled identity.
Build-time schema/example validation intentionally retains the complete roster.

On V20, eleven wholly unavailable tools are omitted and execution-denied:

- ReadCommunicationConnections, ManageCommunicationConnection
- ReadSafetyActivationTests, ManageSafetyActivationTest,
  ManageSafetyActivationTestGroup, ManageSafetyFunction,
  ManageSafetyFunctionCondition
- ManagePlcBlockWriteProtection, ManageDriveSafetyAcceptanceTest
- ManageSivarcScreenLayout, ManageClassicHmiGraphic

Eight mixed-action families retain supported routes and deny the known V21-only
selectors before native access, including previews:

| Tool | V20 refused selector | V20 retained route |
|---|---|---|
| ManageDcbLibraries | action=import | read |
| ManageDriveHardwareModule | changeType, setPositionNumber | read, with existing partial-data note |
| ManagePlcProtection | protectAllConfiguration, unprotectAllConfiguration | other declared actions |
| ManagePlcExternalSources | renameGroup | other declared actions |
| ManagePlcSafety | generateBaseId | other declared actions |
| ManagePlcDocuments | objectKind=document + createFromMasterCopy/createFromLibraryType | type routes and other document actions |
| ManageSivarcBlockDefinition | tagMemberSettings, commonParameters, blockParameter | tagDefinition/textDefinition |
| ManageDeviceServiceObjects | webApplications; telecontrol read/update/delete | telecontrol export/import; certificate services |

These branches were traced to the existing typed V20 conditional implementations.
Allowed selectors reuse their existing validators; unknown actions/kinds/families
fail closed within these mapped tools. This is not a claim that all other 486
registered tools/actions have received a complete capability audit. Native
object/product/license, quarantine and write guards remain authoritative.
Known but unavailable tools report `toolFound=true, versionAvailable=false` rather
than pretending the name is unknown.

Read-only partial fields are not wholesale tool exclusions: text categories,
linked channel tags, program signatures, drive hardware-module details, unit
system-group names, SiVArc tag-member settings and technology-mapping additions
already report V20 omissions. API branches that retain equivalent functionality
(e.g. online overloads, plant views, process-image assignment, compilation settings)
remain version-specific implementations rather than unavailable capabilities.

### Manual-derived workflow corrections

Library document import now rejects global-library scope and V20 inactive-culture
options during admission; it also rejects ambiguous same-basename XML/S7DCL files
before native import. Library export callers were checked and already navigate
only the bound project's ProjectLibrary. STEP7 target-environment completeness
and in-work version-state semantics still require native/type-aware preflight.

MoveBlockToGroup refuses organization blocks before group creation or export (already-in-place no-ops remain allowed):
Siemens documents that SD import changes OB type/number. Failed relocation keeps
its recovery export and includes the path in the error; cleanup occurs only after
destination presence is verified; destination-name collisions are refused. This is still not a transactional move or a
full semantic-equivalence verification for other block types.

SIMATIC SD language/update-level constraints remain a precise documented gap:
V20 DB/UDT/LAD, V21 adds SCL/FBD/mixed; older LAD exports before V20 Update 4 need
regeneration. The generic DocumentExport feature denotes an API route only;
it does not certify every block language, update level or round trip.

## Verification and release gates

Executed in the Linux review workspace on 2026-10-02 using official Microsoft
.NET SDK 8.0.425 installed with user permission:

- Offline console regression suite, normal V21 compilation: 2,942 passed,
  0 failed, 0 skipped.
- Same suite with `-p:DefineConstants=TIA_V20`: exercises actual bridge/preflight
  admission and non-invocation sentinels in the V20 build: 2,942 passed,
  0 failed, 0 skipped.
- New `TiaMcpServer.VersionPolicyTests` links the real outer wrapper against the
  repository's actual MCP SDK 0.3.0-preview.4: 10 passed, 0 failed, 0 skipped.
  Covers roster filtering, schema identity, rejection before inner dispatch,
  case-insensitive argument keys, denial text, allowed single dispatch,
  cancellation forwarding, omitted arguments, null requests and conflicting case-duplicate selectors.
- Exclusion regressions reject original V14/V15 in the catalog, all CLI version
  aliases, adapter factory and selected PublicAPI directory parser; V14 SP1 and
  V15.1 retain their planned identities. Configurator rejection tests are updated
  but WPF execution remains a Windows-only verification gap.
- `Test-VersionCatalogWiring.py`: 10 static contract checks pass, including dispatch
  wiring, unbound adapter requirements and move recovery/refusal ordering.
- Repository/dead-reference/Python/XML/XAML checks and `git diff --check` are run
  against the final source. These are not native acceptance.

Reproduce the console suites with `dotnet run`, not `dotnet test`:

```text
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release --nologo
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj -c Release --nologo -p:DefineConstants=TIA_V20
dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.VersionPolicyTests/TiaMcpServer.VersionPolicyTests.csproj -c Release --nologo
python scripts/checks/Test-VersionCatalogWiring.py
```

`Build-Release.ps1` now includes both offline symbol modes, static policy wiring,
and real-MCP-SDK boundary tests, recording their counts when a real release build
is run. It has not been run here: Windows tooling and authorized V20/V21 PublicAPI
inputs are absent. Full engine compilation, WPF execution, all SDK-shape checks,
worker protocol tests against built engines and native TIA acceptance remain
unrun. Existing manifests have not been edited to claim a build.

No commit, push or release is allowed until the repository's full Build-Release
gate succeeds. Source progress and Linux test results do not replace it.

### Reference-input availability

No real Siemens.Engineering reference DLLs are present in this workspace. The
[official Openness NuGet package](https://www.nuget.org/packages/Siemens.Collaboration.Net.TiaPortal.Packages.Openness)
locates a TIA installation through TiaPortalLocation/registry; it is not a
standalone package of the actual SDK reference assemblies. Siemens'
[assembly-resolution instructions](https://support.industry.siemens.com/cs/attachments/109815895/109815895_AssemblyResolve_V1_0_EN.pdf)
and [Openness Explorer documentation](https://cache.industry.siemens.com/dl/files/816/109760816/att_1115949/v1/109760816_TIAOpennessExplorer_DOC_V11_en.pdf)
use assemblies from the corresponding installed PublicAPI directory. No verified
official standalone reference download was found in this review.

This is a missing reference-input blocker, not proof that Linux cannot compile
any adapter. Authorized reference copies could allow further compile/metadata
checks here before real TIA testing. No conclusion about a legal ban on private
reference storage is made. Full Windows release/WPF and native acceptance remain
separate later gates.

Independent read-only re-review found no new blocking regression in the corrected
admission paths, selector defaults, exact-overload refusal, move-recovery ordering,
release wiring or roster counts. It does not replace actual SDK/native checks.

## Upstream references considered

No external code or new dependency is incorporated in this increment.

- [heilingbrunner/tiaportal-mcp at 9c1883c](https://github.com/heilingbrunner/tiaportal-mcp/tree/9c1883c8558dbf4651e73aa0a211813bd61c21be):
  MIT; reference for assembly resolution and document guards. Its examined build
  is V21/.NET Framework 4.8, not evidence of legacy compatibility. Check existing
  ancestry before importing equivalent functionality.
- [Czarnak/tia-portal-mcp at 6b6599b](https://github.com/Czarnak/tia-portal-mcp/tree/6b6599bc223ea4548967424f45723a3a3fc8a1c9):
  MIT; reference for explicit operation capabilities, unknown-operation denial,
  XML sanitization and import-path safety. Its examined resolver is V21-only.
- DotNetSiemensPLCToolBoxLibrary: reference for separate version adapters and
  V14 SP1/V15.1 distinctions. Root LGPL-2.1 and differing subproject declarations
  require a component-level license review; do not copy it into MIT sources.
- The examined V18 fork `1748477582/tia-portal-openness-mcp` includes compatibility
  shims inventing Siemens API types; it is not accepted as capability evidence.
  Proprietary TiaImportExport VSExt 4.x is excluded from source integration.
