# Legacy PLC host source preview

## Compile evidence update (2026-10-03)

For published source commit `aaa9e0d7891d6b7a74a1153d2f1c9a0ed4d4f2fa`,
[2026-10-03 compile-only evidence](windows-compile-evidence-aaa9e0d-20261003.md) supersedes the earlier typed-SDK/build-pending
statements below for compilation only. All eight exact Adapter/Worker targets and
the full V20/V21 engines compiled with zero errors; actual Csc was verified.
This was `DesignTimeBuild` compile-only validation, without weaving, artifact
execution or native acceptance. Earlier batch-local results below remain history.
Windows filesystem/runtime checks, release-build gates, per-version enablement
and incomplete final batch-import review are not closed by this evidence.

This is an incomplete migration of the 62-tool V17 PLC profile. The production
version catalog remains disabled for legacy releases. Compilation is not native
acceptance and does not establish support for any release.
The 62 entries describe the historical V17 PLC profile only, not all Openness
features of any TIA version. HMI, online control, downloads and the broader
product/API inventory are not covered by a completion count for this profile.

The exact targets are V14 SP1, V15.1, V16, V17, V18, V19, V20 and V21. Original
V14 and V15 are excluded. The first three workers target .NET Framework 4.6.1;
the remaining workers target .NET Framework 4.8. V21 uses the split Base/Step7
assemblies and its distinct public-key token. Reference directories and core
identities are checked during compilation and again at the engine boundary.

## Manual review limitations

The [tool manual checklist](legacy-plc-manual-checklist.md) records actual
chapter reads, source links, fixes and remaining exact-release gaps. Its JSON
companion provides a machine-readable handoff. V20 workflow evidence is not
silently generalized to other versions.

Block/type exchange and compile still require completion of hardware classification
and R/H coverage review before native execution. Read-only state checks compile
for all eight exact releases. Local-session opening and every bound-session write
remain blocked pending identity/kind/server-lock review.

All actual block/type/tag-table export callbacks now write to an independent
sibling staging directory. The helper validates nonempty safe XML, hashes the
read-locked file, and uses a same-volume no-overwrite Move. Competing destinations
are preserved; failures retain recovery paths/hash/phase without native exception
contents. The publication-specific execution gate is removed; remaining hardware
and session gates still apply. Real filesystem tests are not native acceptance.

V14 SP1 SCL XML exports only interfaces. Preview/results explicitly disclose
`XmlContent=interface-only`, `FullProgramRestoreSupported=false` and a warning.
The block import path checks format under its input read lock and refuses to use
V14 SCL interface XML as complete program restoration/replacement, including into
later target releases. Unknown source SCL completeness fails closed. No XML version
rewrite or source-code reconstruction occurs. Other format/XSD gaps remain.
Engineering Offline is not CPU STOP; external UI changes can race state checks.

## Current implementation

`TiaMcpServer.LegacyHost` is a .NET 8 stdio MCP host with no Siemens reference.
It uses the actual MCP SDK. Of the original 62-profile names, 55 are source-implemented:
12 scoped ordinary source/offline/manual closures and 43 partial candidates; seven remain
unimplemented. Exactly compatible migrations remain seven. Additional foundation tools
include explicit tag/constant operations and the distinct read-only external-source import
plan, which does not implement the original import route. The exact inventory is in
[legacy-plc-migration-status.json](legacy-plc-migration-status.json).

The latest partial candidates add [bounded catalog search](hardware-catalog-search-candidate.md)
for V19–21 and [document export](legacy-document-export-candidate.md) for V20/V21 ordinary
LAD/DB blocks. The [bounded batch document route](legacy-batch-document-export-candidate.md) publishes one new tree, retaining staged/failed/not-attempted outcomes on failure. The [external-source delete candidate](external-source-delete-candidate.md) adds separately confirmed exact-root single-source deletion without generation or save. All retain exact-release gates and native acceptance gaps. The dated compile evidence below is limited to its exact source commit.

`TiaMcpServer.PlcWorker` supplies a separate framework worker for each exact
release. It serializes calls on its owning STA thread and invokes only the typed
foundation facade. It never launches TIA or automatically selects a process.
The host requires an explicit process ID for attachment, exact project paths,
and group-qualified PLC object addresses. Mutations default to `dryRun=true`;
imports also default to `overwrite=false`. Native operations require a separate
explicit session switch; ordinary discovery rejects them before worker startup.
Every mutation additionally requires `confirm=true` and an absolute
`expectedProjectFile`. The worker checks that path against the bound project
immediately before dispatch. Open/create instead verify their explicit target;
creation uses the release's `.apXX` project filename. These guards are intentional
deviations from the unsafe defaults of the old wrapper, not claimed backward
compatibility.

Four read tools now use the V17 argument names and response fields:
`GetBlocks`, `GetTypes`, `GetBlocksWithHierarchy`, and `GetPlcTagTables`.
The first two support the existing case-insensitive name regex, with a bounded
regex evaluation time. The hierarchy contains real nested and empty groups;
block/type details read actual properties and attributes. Optional absent
namespace values remain null. Read errors use MCP `InvalidParams` or
`InternalError` rather than fabricated empty success. Software paths accept a
unique PLC host name, device name, device/host pair, or case-sensitive group path
followed by these aliases. Device and host names ignore case, as in V17.
Canonical foundation addresses disambiguate duplicate names. Ungrouped canonical
addresses are accepted without silently shadowing the V17 aliases.

The declared `v17-read-safe-v1` contract deliberately rejects ambiguous aliases
and extra path segments that V17 could ignore. Invalid regex and native failures
are errors instead of empty/partial success. Root-only tag-table enumeration
matches the V17 implementation. These four tools are source/offline/manual closed for ordinary PLC root/user-group scope under
this declared contract; native acceptance is a separate, still unrun status.

Six single-object exchange tools are implementation/offline verified (official manual reconciliation pending) under
`v17-exchange-safe-v1`: `ExportBlock`, `ExportType`, `ExportPlcTagTable`,
`ImportBlock`, `ImportType`, and `ImportPlcTagTable`. V17 argument names are
preserved; preview, confirmation, exact project identity and explicit import
overwrite options extend the old contract. Block/type exports use an existing
directory plus the object name and `.xml`, with optional existing group
subdirectories. Inconsistent objects and existing output files are refused.
Tag-table export selects the exact root table name and an explicit output file.
Imports select an exact group (empty explicitly means root), never fall back,
and never rewrite XML version headers. Export directories are not created during
preview; exports do not delete existing files.

Exchange responses retain `Message`/`Meta` (and tag-table `ExportPath`). Metadata
distinguishes preview from execution and includes exact project/file targets.
Imports validate XML and hash the source while holding a read lock through native
import. Input path/hash and partial export output paths survive native failure
in the error evidence. File-lock compatibility with installed TIA is unverified.
Conflicting execution results or a mismatched returned project identity stop the
session before releasing its request lock. No unknown write is replayed.

`CompileSoftware` and `CompileAndDiagnosePlc` are implementation/offline verified (official manual reconciliation pending) under
`v17-compile-safe-v1`. They retain V17 software/password arguments and result
fields, plus explicit preview and project-identity confirmation. Preview does not
log in or compile, and leaves state/counts null. Actual results preserve native
aggregate counts and nested raw messages separately from deduplicated classified
details. Summary messages remain in raw output, not error/warning detail lists;
diagnostic timestamps use round-trip formatting. Compilation errors set response
success false. Missing/invalid native result fields stop the session.

Password-free compilation uses the common typed `ICompilable` contract. All DLLs
in the supplied native V14 SP1, V15.1 and V16 directories were checked for the
`SafetyAdministration` type and do not contain it. Nonempty passwords therefore
receive an explicit adapter capability gate for these references; this is not a
claim about every installation/package of those product versions. V17-V20 bind
the monolithic safety API; V21 binds the separately signed Safety assembly with
build-time and runtime identity/source-directory checks. The operation reuses an
already logged-in safety session, or logs in with the provided password and logs
off only the session it established. Login failures do not echo passwords. Any
unknown compile/login/cleanup outcome stops the worker session without replay.
Authentication and cleanup have not been exercised against TIA.

Eight project tools are implementation/offline verified (official manual reconciliation pending) under `v17-project-safe-v1`:
`Connect`, `GetProject`, `AttachToOpenProject`, `OpenProject`, `CreateProject`,
`SaveProject`, `CloseProject` and `GetProjectTree`. Connection requires an explicit
PID. Binding requires both the exact name and absolute project path. Opening and
creation require an unbound connection; creation requires a new project directory
under an existing parent. Neither operation upgrades or replaces a project.
Closing requires ownership established by this worker and an unmodified project;
borrowed projects cannot be closed. Save does not commit to a multi-user server.
Project trees include actual devices, groups, device items and software names.

The supplied V14 SP1/V15.1/V16 APIs have no LocalSession type. V17-V21 compile
against typed local-session enumeration, `.als` opening, Save and Close APIs.
Their installed runtime and Path semantics remain unverified. Pure tests cover
ownership, repeated lifecycle operations, preview, version gates and exact result
identity. The worker uses an explicit operation allowlist; internal facade helpers
are not callable. Request logging is restricted to warnings to avoid tracing
sensitive request arguments.

Request cancellation or protocol failures after dispatch poison the host session
and do not replay a potentially executed operation. Worker input closure disposes
the Openness connection without saving, closing projects or killing TIA. A worker
blocked in native code is not forcibly terminated. Current error responses are
classified by phase: explicit pre-dispatch rejection and completed read failure
allow subsequent calls. Unknown mutation outcomes and malformed/timeout responses
still poison the session, without automatic retry.
The request timeout covers writes, flushes and response reads. Response parsing
rejects missing/duplicate result fields, conflicting result/error fields,
malformed errors and mismatched IDs. An explicitly present null result is valid
for void operations; a missing result is not success.

The resolver loads only identity-matched Siemens assemblies from the explicitly
selected API directory. Private/runtime fallback is intentionally absent. The
supplied API copies do not establish availability of all installed runtime
dependencies. Worker startup and attachment remain unverified and must not be
advertised as functional on an installed TIA system.

Two single-object tools, `GetBlockInfo` and `GetTypeInfo`, return flat V17 detail
fields using the same native property readers as enumeration. Selection requires
an exact case-sensitive group-qualified path, with no root or basename fallback.
Malformed worker DTOs and ambiguous paths are errors. The supplied API XML contains
all 17 checked block/type/group property entries for each of the eight releases;
[XML evidence](legacy-object-info-api-evidence.json) records filenames and hashes.
These member references do not establish version-specific manual workflow coverage.

The previous 20-source-complete count has been withdrawn: source implementation,
offline verification, official manual reconciliation and native acceptance are
separate gates. This preview has 23 implementation/offline-verified entries,
16 manual-partial entries and seven scoped source-complete entries scoped to ordinary PLC root/user groups; 39 profile entries remain unimplemented.

## Offline verification

The framework projects are compilation targets only during this phase. Never run
the worker, native smoke tools or a generated release build as an offline check.
Use an explicit `SiemensEngineeringDirectory`, `TiaReleaseKey`, and, where local
4.6.1 reference assemblies are incomplete, `UseReferenceAssemblyPackage=true`.
`Build-PlcFoundation.ps1` provides compile-only foundation builds for the exact
eight directory mappings. The worker uses the same project properties.

`TiaMcpServer.LegacyHostTests` exercises real MCP SDK request dispatch with a fake
foundation worker: required/type/case/unknown-argument checks, safe defaults,
cancellation and native-disabled behavior. It does not launch any worker or load
Siemens assemblies. The existing offline suites separately exercise the pure
release contract and foundation policy.
Optional test arguments select the PublicAPI root and foundation project
directory. This checks real PE metadata for the exact eight core identities,
required read API properties and compiled facade parameter contracts without
loading Siemens assemblies. SDK serialization tests cover field casing, nulls,
string lists, nested empty groups and invalid worker payloads.

The new host and worker have no integration references from the existing V20/V21
engines, configurator or release scripts. Their protocol is private to this
preview. This isolation statement concerns the new host: the cumulative patch
also deliberately changes existing version routing to fail closed.

## Remaining work

Seven V17 profile entries remain unimplemented in the current ledger; the
55 source implementations comprise 12 scoped closures and 43 partial candidates.
Exactly compatible migrations remain seven. Source exposure is not full behavioral
parity or production support. Remaining work includes exact-release semantics,
Windows filesystem/runtime verification, the incomplete final batch-document-import
wiring review (automated tool block), and separately authorized native acceptance.
Document routes retain their release-specific capability gates. HMI, downloads and
online PLC writes remain outside this foundation preview.

## Independent read closure and new source reader

Six ordinary-PLC reads are now manual/source/offline closed for all eight exact releases: GetBlocks, GetTypes, GetBlocksWithHierarchy, GetPlcTagTables, GetBlockInfo, GetTypeInfo. Special scopes and native acceptance remain unverified. DateTime ticks/Kind are preserved through actual worker Newtonsoft serialization and host envelopes without coercion.

GetPlcExternalSources is the 23rd implemented profile entry, typed root source-name enumeration only. It has no offline/write prerequisite. Eight compile targets pass; all eight exact-release navigation/composition chapters and 24 XML members close this ordinary-PLC root name-reader scope. Native acceptance remains NOT RUN. Current totals: 7 scoped source-complete, 16 implemented manual-partial, 39 unimplemented, 6 extra tools (29 preview tools total).

## Integrated adapter diagnostics

Eight exact Adapter/Worker targets rebuild against supplied API assemblies and pass mandatory weave/verify on each copied Adapter. Coverage sites: 14sp1=543; 15.1/16=548; 17-21=575. API-independent runtime and PE checks pass 124 assertions; worker selection/publication/coverage checks pass 36, including 40 deliberate coverage corruptions rejected across eight releases. Main host/policy/metadata suite: 1543 passed, zero failed/skipped. Native NOT RUN.

IPC request correlation, binding snapshot and process-loss observer are still unwired; Publish/Pack remains blocked. Adapter builds report the two known catalog CS8625 warnings plus two CS0649 warnings for unwired hooks. That diagnostics increment did not change the full V20/V21 engines. The cumulative WIP does include full-engine source changes (including import safety and prompt registration); both full engines were recompiled for `aaa9e0d` in the 2026-10-03 compile-only validation. The earlier weave counts above are historical and are not evidence of weaving the current candidate.

## Declaration read contracts

ReadPlcTags, ReadPlcUserConstants and ReadPlcSystemConstants now have all-eight-release manual/XML/PE evidence for their ordinary root/user-group table scope. These are three existing extra tools; the 62-tool profile counts stay 23 implemented (7 scoped closed, 16 manual-partial), 39 unimplemented, and 29 preview tools total.

They retain the existing PascalCase array. Tag Value contains LogicalAddress; constant Value is unchanged native declaration text. Empty addresses and strings are retained. Host validation rejects malformed/partial results, wrong table/kind and duplicate identity; blank selection is rejected before dispatch. Their errors now use standard MCP InvalidParams/InternalError. No online value read, expression evaluation or hardware-wide constant enumeration is implied. Software units and special ownership/session scopes remain excluded.

Main pure suite: 1851 passed/0 failed/0 skipped. Eight actual API XMLs provide 104 verified member entries, with PE inheritance and compiled operation checks. Host rebuilt; typed adapter/worker sources unchanged in this batch. Native NOT RUN; production and publication gates remain.
