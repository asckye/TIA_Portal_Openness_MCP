# TIA Openness V18–V21: targeted adapter evidence matrix

Reviewed 2026-10-02 against official Siemens online manuals and the local tia-unified source. Read-only research; no live TIA calls, repository modifications, or claim of a complete all-tool audit. “Documented” does not mean SDK compilation or native acceptance has passed. Absence of evidence is unknown, not unsupported.

Current build scope includes V14 SP1, V15.1 and V16–V21; original V14 and V15
are excluded. All eight exact SDK targets have compiled. V14 SP1–V19 use the
foundation host; V20/V21 use the full engines. Native acceptance remains separate
from these build results; see the [current version matrix](version-tools.md).

## Implementation status

This evidence review informed the source increment described in
[unified version framework](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/unified-version-framework.md).
The initial library-import and OB-move findings have source corrections there.
Library export callers were traced and already use the bound ProjectLibrary;
no global-library export-scope bug was confirmed. Source-only corrections and
offline tests do not establish native acceptance.

## Evidence matrix

| Function / boundary | V18 | V19 | V20 | V21 | Implementation action |
|---|---|---|---|---|---|
| Classic PLC XML export/import | Existing API; exact earliest signature covered by legacy research | Existing API; verify SDK | Documented [XML] | Continuing format, export engineering version 21 [NEW21] | Keep XML fallback separate from SD; preserve engine-version metadata |
| PLC compile via ICompilable | Verify release SDK | Verify release SDK | Documented [COMPILE] | No evidence of removal | Per-object service, Offline prerequisite, recursive results; never infer runtime success from method existence |
| Block/UDT SD documents | Not established here; do not enable newer member references | Not established here; do not enable newer member references | Documented [DOC], limited DB/UDT/LAD [STEP21] | SCL/FBD/mixed added [STEP21] | Feature family alone is insufficient |
| SD OB round trip | N/A | N/A | Do not infer safe | Documented loss of OB type/number [SD21] | Avoid as implicit lossless relocation |
| Library document creation / version update | Earliest availability unknown | Earliest availability unknown | Project library only [LCREATE20, LUPDATE20] | Project library only [LCREATE21] | Scope guard before GetSupportedExportFormats / export / import |
| Library import culture flags | Unknown | Unknown | SkipInactiveCultures / ActivateInactiveCultures explicitly rejected [LCREATE20] | V21 docs retain V20-specific warning; V21 behavior unverified | Reject known V20 case; do not fabricate V21 support/absence |
| Named value types | No assertion | Introduced in S7-1500 software units [MIG19] | Documents available in manual object model | Master copy/library type use added [NEW21] | Do not equate generic PLC Documents with all named value functionality |
| Hardware communication connections | No claim | No claim | Current SDK branch excludes namespace | NEW creation/management includes HMI, S7/TCP/etc. [NEW21] | HardwareHmiConnection minimum 21 justified; separate Unified software HmiConnections |
| Traditional HMI / Unified | Unified screen properties require formatted text since 18 [MIG19] | Same | Separate models | Separate assemblies [MIG21] | Retain `HmiTarget` / `HmiSoftware` dispatch; don't globally disable HMI for absent optional product |
| ScriptModuleType.Name write | Unknown | Unknown | No incident assertion | Local incident quarantine, not official unsupported API | Keep guard; no SetAttribute fallback; connection failure aborts traversal |
| HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText read | Unknown | Unknown | Guard currently not major-scoped | Local two-capture correlation, causality unconfirmed | Preserve unknown/quarantined status; do not call read safe just because read-only |

## Concrete APIs and boundaries

### PLC read, export, import, compile

Use DeviceItem.GetService<SoftwareContainer>() then inspect Software as PlcSoftware or HmiTarget [TARGET]. In repository `Portal.SoftwareLookup.cs` recursively traverses items; this is more robust than assuming a single fixed item level. Traditional HMI remains distinct from Unified.

[XML] documents `PlcBlock.Export(FileInfo, ExportOptions)` and `PlcBlockComposition.Import(FileInfo, ImportOptions)` returning IList<PlcBlock>. LAD/FBD/GRAPH/SCL/STL and FB/FC/OB/global DB are covered; PLC must be Offline. Import may succeed even if content does not compile, and it does not automatically create missing instance DBs. XML export requires consistency. Do not report imported == compiled.

[COMPILE] gives `provider.GetService<ICompilable>()`, then `CompilerResult Compile()`. Applies to PLC software/blocks/types/groups, classic HmiTarget, Device and DeviceItem with different HW/SW scopes. All devices Offline; protected safety programs require login. Capture State, WarningCount, ErrorCount and nested Messages. Repository `Portal.Software.cs` already treats Unified compiler lookup differently; preserve it instead of flattening all Software subclasses.

[DOC] block export is `ExportAsDocuments(DirectoryInfo, string baseName)` returning DocumentExportResult. Import is `Blocks.ImportFromDocuments(DirectoryInfo, string baseName, ImportDocumentOptions)` returning DocumentImportResultForBlocks. Inspect State, Messages, ExportedDocuments / ImportedPlcBlocks. Output directory must exist. Manual prose occasionally says singular ExportAsDocument, and enum spelling in tables has errors; code example uses plural. Verify actual enum identifiers against SDK rather than copying typos.

[STEP21] establishes V21 additions SCL, FBD, and mixed blocks relative to earlier DB/PLC-type/LAD support. [SD21] warns pre-V20 Update 4 LAD document exports require regeneration, and OB import changes event type to cyclic plus automatically assigns a three-digit number. These are provenance/semantic checks, not just API method gates.

### Library APIs

[LEXPORT20]: `LibraryTypeVersion.ExportAsDocuments(DirectoryInfo, string, string exportFormat, LibraryExportOptions)` returns ExportTransferResult. Project-library only. [LFORMATS20]: `LibraryType.GetSupportedExportFormats()` returns IEnumerable<string>; use that actual object response rather than a hardcoded format guess. Project-library restriction is explicit.

[LCREATE20]: `LibraryTypeComposition.CreateFromDocuments(directory, basename, [targetEnvironment], LibraryImportOptions)` returns TypeCreateTransferResults. Only None is safe on V20: both culture flags explicitly throw. CreatedType's default version is InWork; transfer warnings are not unconditional success. [LCREATE21] makes targetEnvironment mandatory for every STEP7 type and lists PlcBlockGroup/PlcTypeGroup as targets; optional for others. Both versions reject global-library use.

[LUPDATE20]: version creation uses `LibraryTypeVersionComposition.CreateFromDocuments(directory, basename, [targetEnvironment], CreateOptions, LibraryImportOptions)` and VersionCreateTransferResults. CreateOptions.None throws if an InWork version exists. Both creation/update reject ambiguous same basename with multiple supported source extensions, e.g. .xml plus .s7dcl. An optional .s7res companion is not that ambiguity.

### Assembly and schema identity

[MIG21] requires rebuilding against modular V21 assemblies under PublicAPI/V21/net48. .NET Framework 4.8 only; Copy Local false; old engineering DLL cannot substitute. Base + Step7 for PLC; WinCC and WinCC.Extension for classic, WinCCUnified for Unified. Public key changes to 29bfe5fdf4ba5d3b. Project Graphics/PlantViews and PLC simulation settings move to services. ISoftwareCompareTarget moves SW→Compare. SimotionProvider and Workspace.UpdateLibraryInstancesInProject are removed without replacement. Repository already branches simulation settings in Siemens/Services/SoftwareUnitDeepService.cs; audit all direct old paths before sharing source with older builds.

[NEW20] V20 installation supplies API versions 17–20 and exports engineering-version-20 XML regardless of chosen API. Imports engineering versions 17–20. [NEW21] supports XML imports 18–21 and exports 21; it does not supply the older API assemblies. Model compiled API, installed Portal, source XML engineering version, product modules, and native validation as separate fields.

[MIG18] introduces explicit product-license checks (LicenseNotFoundException), PlcUnitBase with PlcUnit/PlcSafetyUnit, and mandatory SimaticML namespace metadata for new exports/imports, while legacy XML remains importable when its engineering version is clear. Old SWImportOptions flag-combination bug is fixed in V18. [MIG19] adds NamedValueConstant schema scope for NamedValueTypes; Unified screen texts require formatted markup from V18. These failures are not uniformly “unsupported version.”

## Evidence limitations / next acceptance stage

Exact V18/V19 full per-method signatures need their release SDK/manual comparison; the current research has verified release migration notes but has not completed their entire object model. No live installation, module availability, native process safety, installed-update level or full tool catalogue was tested. Local crash comments are incident evidence, not Siemens confirmation. Preserve statuses such as documented, unimplemented, unknown, unsupported-by-object, missing-product, missing-license, native-crash-quarantined, and native-verified separately.

## Official sources

- [TARGET](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-devices-networks-and-connections/querying-plc-and-hmi-targets)
- [XML export](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-blocks), [XML import](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/importing-block)
- [COMPILE](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project)
- [DOC export](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-program-block-as-document), [DOC import](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/importing-program-block-from-document)
- [STEP21](https://docs.tia.siemens.cloud/r/en-us/v21/what-s-new-in-tia-portal/what-s-new-in-v21/simatic-step-7)
- [SD21](https://docs.tia.siemens.cloud/r/en-us/v21/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500-s7-1200-g2)
- [LEXPORT20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/exporting-library-type-version-as-document)
- [LFORMATS20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/retrieving-supported-export-formats-for-library)
- [LCREATE20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/creating-new-type-from-document), [LCREATE21](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/creating-new-type-from-document)
- [LUPDATE20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-type-from-document)
- [MIG21](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)
- [NEW20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness), [NEW21](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness)
- [MIG18](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-for-long-term-stability-in-tia-portal-openness-v18), [MIG19](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-for-long-term-stability-in-tia-portal-openness-v19)


## Additional V21 evidence and bounded claims

The [V21 Openness additions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness)
explicitly include safety activation tests, block write protection and SiVArc
layout configuration. The exact Drive SafetyAcceptanceTestProvider minimum was
not independently established from official release notes: its central V20
exclusion follows the repository's existing compiled V20 unavailable branch,
not an assertion that all drive safety acceptance began at V21.
