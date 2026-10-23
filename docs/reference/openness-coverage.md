# Current official API audit

The eight local official PublicAPI XML sets are compared with compiled native-call inventories. No Siemens DLL or XML is redistributed.
Current V4 registration is summarized in [the version matrix](version-tools.md). The [generated name catalog](version-tool-catalog.md) is a release-time record of the last built package; release validation regenerates it.

| Release | Domain signatures | Unique member names | Compiled direct references | Names requiring further review |
|---|---:|---:|---:|---:|
| 14sp1 | 847 | 825 | 98 | 727 |
| 15.1 | 1209 | 1161 | 107 | 1054 |
| 16 | 1476 | 1420 | 109 | 1311 |
| 17 | 1797 | 1719 | 117 | 1602 |
| 18 | 2215 | 2113 | 117 | 1996 |
| 19 | 3714 | 3586 | 128 | 3458 |
| 20 | 4016 | 3860 | 1577 | 2283 |
| 21 | 4480 | 4323 | 1719 | 2604 |

Counts exclude AddIn assemblies, constructors, explicit interfaces, private API and common collection/engineering boilerplate. Signature counts retain overloads; member-name counts combine overloads of one owner/name. Direct references match compiled IL call-site owners and method/property names; comments and inactive source branches are not counted.

The six legacy columns use the selected PLC adapter; V20/V21 use the full compiled engines. A referenced member may be in an unadvertised adapter path, inherited/generic calls can require manual mapping, and reflection can reach some unreferenced members. Therefore these are review candidates, not missing-tool counts, a coverage percentage or proof of native behavior.

The historical PLC migration backlog contained seven pending tool names at baseline
`c5379c1`; this batch adds scoped external-source creation and generation, leaving
five names unimplemented. It is only one migration list. The functional review checks the
engineering workflows those names represent, existing runtime gates, and additional
API families. See the [implementation order](version-tools.md#release-differences).

Reproduce with `build-tools/release/CatalogAudits.cs --public-api-root <SDK-root>` after building all targets. [Machine-readable summary and SDK hashes](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/version-api-audit.json) identify the exact inputs. Detailed identifier-only candidate lists are written under `bin-build/multi-version/api-audit`.

The previous V21 lexical audit (2.7.42) is preserved in [Git history](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6f3a5e1f2d4554f2adfefce883843c4bb8f5d6d2/docs/reference/openness-coverage.md). It does not describe the newly enabled version routes.

## Functional review

Reviewed on 2026-10-03 against the eight supplied SDK XML sets, current source routes
and the advertised catalogs. The [functional matrix JSON](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/version-feature-matrix.json)
contains exact identifiers, input hashes, source files, tool names and follow-up work
for every row below. It is separate from the generated member-reference audit and
does not redistribute the SDKs. The table covers 29 workflows/families, not all
possible Siemens actions. A broad family can be partial even when it already has
many working tool implementations.

All **N** cells were also checked against assembly metadata with reflection-only
loading of the original V14 SP1 through V18 `Siemens.Engineering.dll`: the relevant
type, property or collection import method is absent. The JSON records these DLL
hashes as well as XML hashes. No TIA session or native engineering operation was
started by that check. **?** cells deliberately make no equivalent absence claim.

- **I — implemented:** an advertised implementation exists for the stated scope;
  this is not native acceptance.
- **P — partial:** some stages/operations exist, but a workflow, format or execution
  scope remains incomplete.
- **B — pending:** the inspected API exists, but the dedicated MCP scope is missing.
- **N — unavailable API:** the exact inspected API is absent from the supplied SDK;
  manual TIA product functionality is a separate question.
- **? — unverified:** an equivalent interface or its behavior has not been established.

| Workflow / inspected scope | 14 SP1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 |
|---|---|---|---|---|---|---|---|---|
| Ordinary PLC block/type/tag-table browsing | I | I | I | I | I | I | I | I |
| PLC block/type/tag-table XML exchange | P | P | P | P | P | P | P | P |
| Create external source from a file | I | I | I | I | I | I | I | I |
| Generate blocks from external source | I | I | I | I | I | I | I | I |
| Compile PLC and read nested diagnostics | P | P | P | P | P | P | I | I |
| Source-to-compile/readback stages | P | P | P | P | P | P | I | I |
| Offline builders targeting the selected XML release | P | P | P | P | P | P | P | I |
| Read ordinary technology-object metadata | I | I | I | I | I | I | I | I |
| Import technology-object XML | N | N | B | B | B | B | I | I |
| Traverse technology-object user groups | N | N | N | N | N | I | I | I |
| Export technology-object XML | B | B | I | I | I | I | I | I |
| Watch-table listing and XML exchange | N | P | P | P | P | P | I | I |
| Hardware catalog search / exact catalog device creation | N | N | N | N | P | I | I | I |
| Create a device with its initial item | B | B | B | B | B | I | I | I |
| Network interface/connection engineering | P | P | P | P | P | P | P | P |
| CAx device exchange | B | B | B | B | B | B | I | I |
| Project/global library engineering | B | B | B | B | B | B | P | P |
| Version Control Interface through MCP | N | N | P | P | P | P | P | P |
| Classic HMI engineering | B | B | B | B | B | B | P | P |
| Unified HMI engineering | ? | ? | ? | ? | ? | B | P | P |
| PLC software-unit engineering | N | N | B | B | B | B | P | P |
| Safety offline-program login for compilation | N | N | N | P | P | P | I | I |
| Modern Startdrive parameter operations | ? | B | B | B | B | B | P | P |
| SiVArc engineering | ? | B | B | B | B | B | P | P |
| Drive Control Chart engineering | ? | ? | B | B | B | B | P | P |
| Continuous Function Chart exchange | ? | ? | ? | ? | B | B | P | P |
| Test Suite case management/execution | ? | ? | ? | B | B | B | P | P |
| Teamcenter integration | ? | ? | ? | ? | B | B | P | P |
| PLC upload/download | ? | B | B | B | B | B | P | P |

`BuildPlcSymbolManifestFromPath` is an offline bounded extractor for native PLC tag-table and GlobalDB XML snapshots. Empty tag tables produce a valid file result with zero symbols; tag comments are ignored; user constants retain their raw value; system constants are omitted with an explicit warning. It does not certify the complete XML schema, import behavior, or program semantics.

The ported catalog follows the 2026-10-09 maintainer decision: at least one implemented action must run on the selected API. PLC protection/instance DB creation start at V15.1; system block groups start at V14 SP1 with system type groups unavailable until V15.1, program updates/cross-references at V18, network domains at V16, transfer areas at V15.1, system diagnostics exchange at V17, and catalog device creation/certificate service objects at V18. Communication connections remain V21 only. Absent whole-tool API means no registration; partial tools keep their action refusals before any native call. `ManageDeviceUserGroup`, module insertion, older attribute-write overloads, Classic HMI compile and PLC-only reference seeding keep usable old-release paths. Foundation catalog search and exact device creation start at V18. Single watch-table export starts at V15.1. Schema fragment validation uses each selected release's local schemas; V14 SP1 validates Interface fragments and reports unsuffixed network fragments as skipped. Classic screen building starts at V18, CWC ZIP packaging at V17 (V16 remains uncertain and closed), and the HMI/PLC synchronization precheck suite at V19. ImportMasterCopyFromGlobalLibrary remains registered from V19 but refuses screen-item instantiation: the official API exposes no Unified ScreenItems.CreateFrom(MasterCopy). Use the documented screen XML/document import workflow instead. The six V21-only PLC builders are withheld from V14 SP1–V19; their V20/V21 registrations are unchanged. See the [per-pair recheck and VM acceptance list](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/phase8-port-plan.md).

All eight-release native acceptance entries remain **NOT RUN**. The compiled
adapters and functional/transport tests are separate evidence. Neither a build nor
a schema-valid example establishes a successful native import or compile.

## Foundation selector, failure and audit behavior

Foundation reads and ordinary single-object tools accept an exact `softwarePath` or a unique PLC/device alias; ambiguous aliases are refused. The `plc` declaration tools use the same resolver. For `table`, use the exact escaped path returned by `ListPlcTagTables`, or a unique raw table name when it is one path segment. A Chinese name such as `默认变量表` can be passed as returned. Directory batch export and import deliberately require the exact canonical `softwarePath` and exact group paths.

Worker-side validations that finish before the first native mutation return `rejected-before-operation` and identify the known argument. They do not poison the session. A worker read failure exposes a bounded, redacted `nativeMessage`; an unknown write outcome retains the bounded worker message in its details and still requires inspection before another write. `ExportPlcBlocks` and `ExportPlcTypes` write into an existing directory. These source-contract changes do not change the **NOT RUN** native acceptance status; retest the exact release and TIA build before claiming native acceptance.

For audited MCP writes, one `request` row uses the result's `meta.requestId` and records `planHash` when available. An approved or approval-disabled dispatch adds `start` after the decision; a denied or timed-out call records its approval decision and `end` without `start`. Request, approval, start and end rows share that request ID on every release. `dryRun=true` and candidate `mode=preview` calls do not queue for approval or enter the write audit.

When approval is enabled, writes with a preview first run their own `dryRun:true` or `mode:"preview"` call through the same session and native dispatch lane. A refused precheck returns the original error with `meta.outcome="rejected-before-operation"`, `meta.execution="not-started"`, and a `meta.warnings` entry `{ "code": "APPROVAL_PRECHECK_REFUSED", "details": { "stage": "approval-precheck" } }`. Only the original write records `request` and `end`; the internal preview adds no write audit, approval decision or start. A successful preview releases the lane before approval waits; the real call checks preconditions again under the lane. Approval disabled skips the precheck. Tools without a preview retain their existing gate. Current FILE/EXECUTE tools that were not approval gated retain their classification; promoted export/compile candidates use their own mode preview.

After a native write has an unknown outcome, subsequent native reads and writes are refused before approval with `SESSION_RESET_REQUIRED`, `error.details.reason="previous-outcome-unknown"`, `rejected-before-operation` and `not-started`. Inspect TIA, then DisconnectPortal and establish a new explicit ConnectPortal/AttachOpenProject session, or explicitly restart the worker with `RestartOpennessWorker` and bind the project again. Recovery is the same on every release, and the host never replays the interrupted request automatically.

## Findings that determine implementation order

1. **The source pipeline is available in the official APIs.** Every inspected SDK has
   `PlcExternalSourceComposition.CreateFromFile(string, string)` and parameterless
   `PlcExternalSource.GenerateBlocksFromSource()`. The two foundation execution
   routes are now implemented for root sources. Import accepts ASCII SCL/AWL/DB/UDT;
   other encodings, including Chinese comments, still need version-specific native
   validation. V15.1+ generation uses `GenerateBlockOption.None` and returns native
   generated objects. V14 SP1 only provides a void operation, so its inventory
   changes must not be described as a complete native generated-object list. Existing
   blocks/types may be overwritten. Normal native generation errors are returned
   as `failed` while retaining the session; interruption/readback uncertainty still
   requires a reset. Planning is not import, and generation requires separate
   compilation and readback.
2. **Compilation had an execution gate hidden behind an advertised route.** The
   foundation implementation called an unconditional refusal before its native
   compiler. This batch replaces that blanket refusal with explicit traversal of
   top-level, ungrouped and nested-group devices and all exposed standard/RH states.
   The selected PLC must be Offline; observed unknown/online states and PLCs without
   required providers are refused. HMI/passive devices without exposed state are
   reported through `offlineStateNotExposedByDevices`, without inferring Offline or
   refusing solely for provider absence. The official all-devices-offline prerequisite
   remains, so those states require independent confirmation. Diagnostics use the
   documented typed fields and recursively nested messages, without guessing
   additional dynamic diagnostic attributes.
3. **Technology-object import has a genuine API boundary.** The collection import
   member is absent from the supplied V14 SP1/V15.1 XML and present from V16.
   `TechnologicalInstanceDBGroup.Groups` appears from V19. An older export API does
   not imply a corresponding import API or support for user folders.
4. **Host version and generated XML format are different facts.** This batch makes
   flat UDT and GlobalDB declarations target each exact release's interface format
   and object attributes. Sixteen interface fragments passed the supplied official
   XSDs, but complete-document/native import and CPU/type semantics remain unverified.
   The other six PLC builders still emit V21 candidates, so the family remains
   partial for older hosts. V14 SP1 SCL block XML is also interface-only. A version
   marker change cannot establish compatibility. See the
   [V16 manual](https://cache.industry.siemens.com/dl/files/802/109773802/att_1007204/v1/TIAPortalOpenness_en-US.pdf)
   and [V18 object namespace change](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-for-long-term-stability-in-tia-portal-openness-v18).
5. **Several legacy gaps are exposure gaps.** Watch-table import exists from V15.1,
   catalog search types from V18, and common device creation from V14 SP1. Classic
   HMI, project libraries and CAx APIs also exist in every inspected SDK. Studio's
   V16-V19 VCI implementation is an existing reuse candidate. These families need
   contract-level ports, not duplicate generic implementations or one tool per
   unreferenced member.

For each completed scope, update this table and the JSON together, add its calls and
programming assets to the existing unified example library, and record functional
test evidence separately from native test-project import/compile/readback evidence.
Original V14/V15, uninspected SDKs, missing optional services and untested project
types do not inherit support from a nearby release number.

## V20/V21 optional tools and scope

These V4 tools are registered by the full engines. Exact parameters and examples
come from `GetToolUsage`; registration does not prove that an optional product is
installed or that a native operation has passed acceptance.

| V4 tool | Release scope | Current boundary |
|---|---|---|
| `GetOpennessCompatibility` | V20/V21 | Reports loaded Siemens assembly identity, engine identity, cross-reference policy, and known issue links. Installed update/hotfix state remains unknown unless directly supplied. |
| `InspectSimaticSdCompatibility` | V20/V21 | Offline checks `.s7dcl` for known language and format risks. It is not a complete parser; no warning does not establish import compatibility. |
| `GetNativeInvocationLog` | V20/V21 | Reads bounded native-call diagnostics. It records call stages, not every native path, and omits credentials and engineering payloads. |
| `GetPlcBlockScopes` | V20/V21 | Enumerates root, software-unit, and safety-unit PLC blocks. Partial enumeration is not reported as a complete empty result. |
| `ManagePlcBlockDocuments` | V20/V21 | Lists, reads, exports, or imports documents in explicit user block groups. SIMATIC SD import requires an offline project; system groups are not import targets. |
| `ManageSinumerikArchive` | V20 only | Uses the SINUMERIK archive provider for archive/retrieve/address-archive operations. Output checks do not validate every restored engineering property. |
| `ImportSinumerikAlarmTexts` | V20 only | Imports the selected TS/CSV alarm text files through the NCU alarm text provider. Existing DB2 text can be replaced; returned success is not per-row content verification. |
| `ManageSinumerikSafetyMode` | V20 only | Reads or sets NCU safety mode with an explicit dry-run/confirmation contract and readback. This does not replace safety engineering acceptance. |
| `InitializeSimotionScripting` | V20 only | Calls the project SIMOTION initializer and returns its string; it does not execute that string or run an external SCOUT script. |
| `ExportScadaData` | V20 only | Exports project or PLC configuration through the SCADA Export provider. It requires the optional component and does not deploy a WinCC runtime. |
| `GetPlcCrossReferences` | V20/V21 | Native PLC cross-reference stays disabled by default. The gated route requires explicit process configuration and complete, true block/type consistency evidence. |
| `CheckDownloadReadiness` | V20/V21 | Unknown device reachability, access, hardware consistency, or download prompts remain unknown; `Ready=null` is not permission to download. |

The optional providers above have no matching V21 SDK types in the supplied
baseline, so their 4.0 routes reject on V21. This says only that the supplied SDK
does not expose the reviewed path; it does not establish that Siemens has no V21
product capability. V21's modular optional-product assemblies and installed
components require their own exact SDK and acceptance evidence.

## Session and project lifecycle (API audit 2026-10-19, P8-81c)

Used since P8-81c, with the releases where the SDK has each member:

| Official API | Releases | Use |
|---|---|---|
| `ProjectComposition.Open/OpenWithUpgrade/Retrieve/RetrieveWithUpgrade(..., UmacDelegate)`, `UmacCredentials` | 15.1-21 | Optional `umacUserName`/`umacPassword`/`umacUserType` on `OpenProject` (and its session candidate) and `RetrieveProjectArchive`. The password exists only as a `SecureString`; it is never logged, journaled, echoed, stored in results or audit records, or shown on the approval card. V14 SP1 has no UMAC overloads. |
| `TiaPortal.Authentication` | 17-21 | Answered for the one open or retrieve call only when credentials were given. Without credentials no handler is installed; a refused protected project is `PRECONDITION_FAILED` naming the missing credentials. |
| `TiaPortal.Confirmation`, `Notification`, `Disposed` | all eight | Confirmations are refused by default (Cancel, Abort, No, NoToAll in that order; never Yes/Ok) and recorded; notifications are recorded; `Disposed` marks the session lost. `GetPortalInfo` lists the records. |
| `TiaPortal.GetProcess(int, TimeSpan)` | all eight | Targeted attach lookup with a 30-second timeout inside the host's attach budget. |
| `EngineeringSecurityException`, `LicenseNotFoundException` (17-21), `MissingProductsException` (18-21), `Multiuser.MultiuserException` (17-21) | as listed | Explicit, non-poisoning `PRECONDITION_FAILED` conditions `openness-access-denied`, `license-not-found`, `missing-products`, `multiuser-refused` with guidance. |
| Openness firewall AllowList (`HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList`) | all eight | Read only by the Workbench check "Openness AllowList" (path, UTC modification date, Base64 SHA-256 per worker executable). Nothing writes the registry. |

Official APIs in this family that no 4.0 tool uses:

- Secondary (reference) projects: `ProjectOpenMode.Secondary` with `ProjectComposition.Open(FileInfo, UmacDelegate, ProjectOpenMode)` and the `Retrieve` overload with `ProjectOpenMode` (15.1-21). `CompareProjects` with `targetProjectName` therefore needs a second project opened outside the MCP.
- `LocalSessionComposition.OpenServerProject` and `ProjectServer.CreateLocalSession`, `AddProjectToServer`, `DeleteLocalSessionFromServer`, `SetHostName`, `SetPort`, `SetProtocol` (17-21). `ManageMultiuserSession` does not claim them.
- `ProjectBase.ExportProjectTexts(FileInfo, Language, IEnumerable<Language>, IEnumerable<TextCategory>, IEnumerable<IEngineeringObject>)` (21 only). `ExportProjectTexts` uses the culture-pair overload that exists on all eight releases.
- The Workbench Studio session (`TiaOpenness.Bridge`) has no process picker: with several running TIA Portal processes it refuses and lists them instead of attaching to the first one.

Not verified on a real TIA Portal: every row above. In particular the headless behaviour of a protected project without credentials, the dialog texts that reach the Confirmation handler, whether `UmacCredentials` is supplied in `AuthenticationEventArgs`, and whether a worker-started headless TIA Portal outlives the worker.

## PLC software (API audit 2026-10-19, P8-81d)

Used since P8-81d, with the releases where the SDK has each member:

| Official API | Releases | Use |
|---|---|---|
| SimaticML `Document/Engineering/@version` and "Version-specific SimaticML import" | all eight | Never rewritten. Every legacy PLC import path (BuildAndImportPlcArtifact, RepairAndReimportPlcBlock, ImportPlcTagTablesFromDirectory, SeedProjectFromReference) refuses a file outside the documented window before any native call: up to V18 the file's version must not be newer than the API (V14 SP1, V15.1, V16, V17, V18); V19 imports V16-V19, V20 imports V17-V20, V21 imports V18-V21. "V14 SP1", "V15.1" and the numeric spellings are parsed. Builders write the serving release's header (`PlcDeclarationXmlFormat.ForRelease`). |
| `ImportOptions.None` / `ImportOptions.Override` | all eight | `overwrite=false` (the default) refuses an existing object of the same name before the import and then imports with `None`; `overwrite=true` uses `Override`. An existing object in another group is refused, because TIA cancels such an Override import. |
| `PlcTagTableSystemGroup.Groups.Find` / `PlcTagTableUserGroup.Groups.Find` | all eight | Exact tag-table group resolution; an unresolved `folderPath` is refused, never redirected to the root group. |
| `PlcBlock.IsKnowHowProtected`, `PlcBlock.IsConsistent`, `PlcBlock.Number` | all eight | MovePlcBlockToGroup refuses know-how-protected and inconsistent blocks before any delete, moves through SimaticML XML only (SIMATIC SD does not store the block number), and compares the number before and after; a change is reported as a failure with the retained recovery export. |
| `ExportOptions.WithDefaults` | all eight | Backup and recovery exports (import recovery, batch-import recovery, the move round trip). User-facing XML exports keep `ExportOptions.None`: TIA writes only modified and non-default values plus what a later import needs. |
| `CompilerResult.State` and `ErrorCount` | all eight | The V4 compile candidate decides success from the root state (Success, Information, Warning) and root ErrorCount 0. A root/leaf count mismatch is kept as a diagnostic. The legacy diagnostics lists compare `State` with the enum names. |
| `DocumentImportResult.Messages`, `DocumentExportResult.Messages` | 20, 21 | Kept (at most 8 messages of 512 characters) with every failed or partial document result. `PartialSuccess` is never success: B5 document tools return outcome partial with the messages; candidate, move and batch paths stop. |
| `PlcTypeGroup.Documents` | 20, 21 | A type group that holds only PLC documents is not empty and is not deleted. |
| `PlcWatchAndForceTableGroup.ForceTables` | 15.1-21 | A watch-table group that holds only force tables is not empty and is not deleted. |
| `PlcUnitProvider` units / `SafetyUnits` | 16-21 / 18-21 | The download-readiness consistency read includes software units from V16 (it started at V18 before). |
| `CrossReference.Location.Address`, `TypeName`, `ReferencedAsName` | 18-21 | Returned with every cross-reference row as `locationAddress`, `locationTypeName`, `locationReferencedAs`. `CrossReferenceService` does not cover `PlcUserConstant`, so a tag table that holds user constants never reports a complete delete aggregate. |
| `PlcExternalSourceComposition.CreateFromFile(String, String)` | all eight | Only `.scl`, `.awl`, `.db` and `.udt` ("Adding an external file"; `.stl` throws natively) and a fully qualified path; TIA receives the normalized full path. |
| `PlcBlockComposition.CreateInstanceDB` | 15.1-21 | V15.1 and V16 document it for ProDiag FBs only, so other FBs are refused there; `autoNumber=false` needs `number >= 1`. |
| User-group `Name` setter | blocks, types, watch tables: 21; tag tables: 20-21; technology: 19-21 | ManagePlcUserGroup `rename` is refused where the setter does not exist (no `SetAttribute` fallback). |

Withdrawn by decision (2026-10-20): on V20 the four whole-block composers that write V21 documents (`BuildPlcFcBlock`,
`BuildPlcFbBlock`, `BuildPlcLadFcBlock`, `BuildPlcAliasAlarmLad`) are not registered, and `BuildAndImportPlcArtifact` refuses
`kind` fc/fb below V21. They stay on V21. They open on V20 only after an import of their output there has been verified.

Official APIs in this family that no 4.0 tool uses:

- Software-unit scope for the candidate import/export and batch tools (`PlcUnitBase.BlockGroup`, `TypeGroup`, `TagTableGroup`,
  `ExternalSourceGroup`, 16-21). ImportPlcBlock, ImportPlcType, ImportPlcTagTable, their directory variants, ExportPlcBlock,
  ExportPlcBlocks, ExportPlcTypes and ImportPlcProgramFromDirectory work on the PLC root groups only; ManagePlcExternalSources and
  ManagePlcBlockDocuments take `unitName`.
- `SWImportOptions` (`IgnoreStructuralChanges`, `IgnoreMissingReferencedObjects` 15.1-21, `IgnoreUnitAttributes` 16-21) and the
  culture options `ImportOptions.SkipInactiveCultures` / `ActivateInactiveCultures` (19-21). A file exported from a software unit,
  one that references objects not yet in the project, or one with inactive cultures is therefore refused by TIA; the import
  failure names these options.
- SIMATIC SD for FBD, F-FBD, SCL and mixed-language blocks on V21 (V21 "What's new"). The SD document tools accept LAD and DB blocks
  (and UDTs) on V20 and V21; V20 documents exactly that set.
- `PlcBlock.Export(FileInfo, ExportOptions, DocumentInfoOptions)` (18-21) and `ExportOptions.WithReadOnly` outside
  ImportPlcBlockVerified.
- `GenerateOptions.WithDependencies` for `PlcExternalSourceSystemGroup.GenerateSource`. GeneratePlcSourceFromBlocks does not pre-check
  the block language; TIA generates sources only from SCL and STL blocks, DBs and UDTs and rejects the rest.

Not verified on a real TIA Portal: every row above; the VM checklist names the native behaviours each fix depends on.

## HMI API audit (2026-10-19, P8-81e)

Classic XML imports default to `overwrite=false` (`ImportOptions.None`), check the
exported target names before calling Import, and refuse a missing folder instead of
falling back to the root. Explicit `overwrite=true` selects Override. Existing
Manage tools retain the equivalent `importOptions=None/Override` choice and require
`confirmDelete=true` for Override. Native Unified package imports also default to
`overwrite=false`: because their complete identities are not parsed offline, a
nonempty target is refused unless replacement is explicitly authorized. Expected
names are readback hints and cannot authorize additional names in a package.

Classic XML export tools and Manage export actions default to
`ExportOptions.WithDefaults`; `exportOptions` also accepts `None` and `WithReadOnly`.
The chosen option remains in the request and any approval hash. Unified directory
exports have no ExportOptions overload in the SDK and retain their documented
native directory API.

| Official API / finding | Releases | 4.0 boundary |
|---|---|---|
| Classic Screen/TagTable/Connection XML exchange (F1, F2, F10, F11) | all eight | Classic targets only. Unified refuses with `UNSUPPORTED_CAPABILITY` and directs callers to the supported Unified directory exchange tools. Existing targets and unresolved folders refuse before native Import. |
| Unified `HmiScreen.ScreenItems` / `DescribeHmiScreenItem` (F3) | 19–21 | Registered only on these releases. Classic WinCC has no ScreenItems API; use `ExportHmiScreen` XML for its item content. |
| Project multilingual graphics (F4) | all eight | V14 SP1–V16: `Project.Graphics`; V17–V20: inherited `ProjectBase.Graphics`; V21: `project.GetService<GraphicsProvider>().Graphics`. `ManageClassicHmiGraphic` and `GetClassicHmiGlobalization` are project-wide; softwarePath remains a compatibility parameter. |
| Unified `Tags.Create(name, tagTableName)` (F5) | 19–21 | Uses the device Tags composition and the requested table's actual Name; verifies `TagTableName`. Device-wide tag-name conflicts name the owning table. No alternate overload is attempted after a failure. |
| `IHmiScript.ScriptCode`, `GlobalDefinitionAreaScriptCode`, `Async` (F6) | 19–21 | An omitted globals/Async value is preserved. Empty globals and false Async are explicit changes. SyntaxCheck remains opt-in. |
| Multilingual captions (F7) | 19–21 | Nonempty `EnsureUnifiedHmiScreenItem.text` refuses before changes because this endpoint has no culture. Use `SetUnifiedMultilingualProperty` with an explicit culture. Requested geometry must be written and read back or the call fails. |
| Unified tags and connections (F8) | 19–21 | Tags use documented properties and typed scalar conversion. Connection Ensure verifies an existing exact partner/driver/address; automatic configuration refuses until values are verified. No DriverProperties substring writes or setters for read-only Partner/Station/Node. The start/stop shortcut stops on connection failure. |
| Unified dynamizations (F9) | 19–21; Expression/TagParameter 20–21 | Exact types from the shared documented kind table only, including Script.ScriptDynamization and Flashing.FlashingDynamization. No substring/type-order search. |
| Screen and tag-table identity (F12) | classic all eight; Unified 19–21 | Exact paths or unique names; ambiguous names refuse. Legacy and exact tag-table exchange share one resolver. Missing parents do not become root creates. |
| Classic screen / tag-table Delete and single-tag XML exchange (F13) | SDK: all eight | Not exposed in 4.0. `ManageClassicHmiScreenObject` covers popup/template/slidein/overview/globalElements only; `DeleteHmiTag` deletes individual tags. |
| Nested Unified runtime settings (F14) | root/nested families differ by release | 4.0 exposes root scalars only. MaxLoginRuntimeSettings, OpcUaServerRuntimeSettings, ProcessDiagnosticsRuntimeSettings, HmiReportingSettings, RuntimeResourceSettings and LanguageAndFonts are not exposed; neither are the V20+ Telemetry/UPSS/tag settings or V21 exclusive-operation settings. |
| Unified collection contract (F15) | 19–21 | Every gate follows the supplied SDKs. There is no V16–V18 contract or speculative HmiTags/HmiTagTables alias. |
| `BuildClassicHmiTagTable`, `BuildClassicHmiMinimalPackage` | 21 only | Withdrawn from V20: their V21 XML header is outside V20's HMI import window and is not rewritten by import. |

Evidence is the per-release official PublicAPI XML, including V21
`Siemens.Engineering.Base.xml`, `Siemens.Engineering.WinCC.xml` and
`Siemens.Engineering.WinCCUnified.xml`. The official V21 examples for
[project graphics](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-graphics/exporting-all-graphics-of-a-project?contentId=Px1AGQqehconPuADamuypQ),
[tag creation](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/tags.create?contentId=cPRa9QLNbXGdPkNx2KVPdg)
and [connection properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/connections/accessing-connection-properties?contentId=QHUB47YCl~QPv3cdmf1TnA)
were recorded by the audit. No native acceptance is claimed.

VM/prerelease checklist: graphics import/export/delete on a pre-V21 release and on
V21 (including companion image files); non-default Unified tag-table creation and
device-wide duplicate names; driver-specific CommunicationDriver values and
InitialAddress key/value strings (including rack/slot) and exact Partner readback;
supported script/dynamization kinds on each Unified release; multilingual HTML
normalization and explicit language readback; classic import size/number/device-type
restrictions and partial-failure evidence. The legacy start/stop shortcut also needs
caption-language selection and V19/V20 pressed-state behavior confirmed; unknown
properties are refused and incomplete steps are reported. None of these checks was
run against TIA Portal, a PLC or a VM in this worktree.

## Additional version-specific evidence boundaries

- Classic HMI and Unified HMI use separate object models and dispatch paths. An
  absent optional Unified service does not disable Classic HMI, or vice versa.
- SIMATIC SD document export/import remains distinct from classic XML exchange.
  An OB document round trip can change its event type and assigned block number;
  do not describe it as lossless relocation.
- Library document operations are limited to project libraries. V20 explicitly
  rejects inactive-culture import flags; equivalent V21 behavior remains unverified.
  Supported export formats must come from the selected object rather than a guessed
  constant.
- A locally observed Unified script rename failure remains quarantined. The
  relationship to the API is not established by current evidence; no fallback write
  path is claimed.
- Exact API names and overloads remain release-specific. `ExportAsDocuments`,
  `ImportFromDocuments`, and `GetCrossReferences` in the source audit are Siemens
  members, while their MCP entry points use the V4 names in the current catalogs.

These are bounded evidence summaries from the supplied API sets, source routes, and
prior read-only reviews. They do not change the functional matrix or mark native
acceptance complete; the actual release, action, project, and optional product must
be recorded in the [real-machine ledger](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md).

## Library, VCI, Safety and reflection audit (2026-10-19)

P8-81f uses the eight supplied SDK XML sets and the local [library](../../reference/siemens-openness/skills/global-library/SKILL.md)
and [Safety administration](../../reference/siemens-openness/skills/plc-safety-administration/SKILL.md) references. These are source-level
API corrections; native behavior remains unverified. The following documented VCI APIs are outside the 4.0 tool surface and remain
coverage work for 4.1:

| Uncovered VCI operation | Official API | Releases |
|---|---|---|
| Language selection at creation | `WorkspaceComposition.Create(string, DirectoryInfo, CultureInfo)` | 18–21 |
| Workspace / mapping deletion | `Workspace.Delete`; `WorkspaceMapping.Delete` / `MappedObject.Delete` | 16–21; legacy mapping 16–20, modern mapping 20–21 |
| User-group creation | `WorkspaceUserGroupComposition.Create` | 16–21 |
| Hierarchical child status | `ChildObjectsStateProvider.GetStatus`; `MappedObject.GetChildStatus` | 18–20; 20–21 |
| Initialize an unknown, unmodified mapping's status | `IndividualObjectSynchronizationStatus.InitializeStatus` | 19–20 |
| Connect an existing workspace file | `Workspace.ConnectObject` | 20–21 |
| Library path / deletion policy | `Workspace.GlobalLibraryPath`, `DeleteUnusedTypeVersionFromLibrary` | 20–21 |
| Library-instance update policy | `Workspace.UpdateLibraryInstancesInProject` | 20 |

The registered synchronization tool refuses `WorkspaceToProject`, including preview. `ConnectProjectToWorkspace` creates files and
mappings with `ExportObject` on V20/V21; its directory argument is relative to `RootPath`. All mapping paths share the same format
preference (s7dcl, SimaticML, XML, then the first offered format). Actual returned format names and additional V18/V19 mappable kinds
need native confirmation. The V16–V19 list remains limited to `PlcBlock`, `PlcType` and `PlcTagTable` until evidence supports more.

DCB extension-library import is available on V16–V20 through `ProjectLibrary.ImportDcbLibrary(string)` and on V21 through
`DcbLibraryImporter.ImportDcbLibrary(string)`. The service moved in V21; import is not a V21 addition. Drive-library reads start at V18.
Unified screen items have no documented master-copy instantiation API; the exact-signature allow-list is empty and the existing
import tool refuses without opening a library or modifying screen items.

Safety dynamic attributes remain a native-confirmation item: `FOBNumber` is documented as `Int32`; the spellings and value types of
`FOBCycleTime`, `FOBPhaseShift`, `FOBPriority`, `EnableConsistentUploadFromFCpu`, `EnableFCommunicationIdTag`, and V21 trace attributes
`PretriggerTime`, `RecordingDuration`, `Signals` require confirmation. SDK XML documents `TraceConfiguration.IsTraced` only.
`setTrace` now requires observable dynamic values/types before writing and verifies every readback; it does not establish that the
attribute names or `Signals` representation are supported on any native build.

Reflection dotted paths read CLR properties only. Dynamic engineering attributes are read through `InvokeObject` with `GetAttribute`
and an exact attribute name. Classic tag tables traverse `TagFolder.TagTables` and nested `Folders`; Classic screen items are refused
because they are not exposed by that Openness API. Service lookup uses exact full names or unique suffixes in Siemens assemblies and
loads the documented V21 split assemblies before searching. Reflection writes to guarded Safety, SafetyValidation, protection, UMAC
and other protected objects must use their dedicated tools; these restrictions do not add a replacement native engineering surface.
