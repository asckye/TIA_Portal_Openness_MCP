# Siemens Openness V14 SP1–V17: foundational adapter evidence

Research date: 2026-10-02. Official Siemens system manuals only. No TIA installation or live project was opened or tested. `Documented` means API evidence, not runtime certification. This is a bounded foundational matrix, not exhaustive feature coverage.

Target scope: V14 SP1, V15.1, V16, V17, V18, V19, V20 and V21. Original
V14 and V15 are excluded, including from planned targets. Their manual excerpts
and comparison columns below are historical migration evidence only. V15.1 is
retained; only V20/V21 currently have runnable engine build targets.

## Sources and reproducibility

Page numbers below are printed manual pages. Source PDFs can be downloaded from the official links below.

- **M14**: Automating projects with scripts, 10/2016 (original V14): https://cache.industry.siemens.com/dl/files/163/109477163/att_899010/v1/TIAPortalOpennessenUS_en-US.pdf?download=true
- **M15**: Automating projects with scripts, 12/2017 (V15; includes explicit V14 SP1 migration chapter): https://cache.industry.siemens.com/dl/files/218/109755218/att_940461/v1/TIAPortalOpennessenUS_en-US.pdf
- **M151**: Openness: Automating creation of projects, 10/2018 (V15.1): https://cache.industry.siemens.com/dl/files/163/109477163/att_926042/v1/TIAPortalOpennessenUS_en-US.pdf?download=true
- **M16**: Openness: Automating creation of projects, 11/2019 (V16): https://cache.industry.siemens.com/dl/files/802/109773802/att_1007204/v1/TIAPortalOpenness_en-US.pdf
- **M17**: Openness: API for automation of engineering workflows, 05/2021 (V17): https://cache.industry.siemens.com/dl/files/533/109798533/att_1069908/v1/TIAPortalOpennessenUS_en-US.pdf

The standalone SP1 manual was not located; SP1 claims below rely on explicit historical migration statements in M15, not an assumed identity with V15. Siemens attachment URLs returned 403; official cache URLs worked.

## Release-level capability matrix

| Capability | V14 original (historical, excluded) | V14 SP1 | V15 (historical, excluded) | V15.1 | V16 | V17 |
|---|---|---|---|---|---|---|
| Enumerate/start/attach Portal | Documented | Retained | Documented | Documented | Documented | Documented |
| PLC discovery service | HW.ISoftwareContainer | HW.Features.SoftwareContainer | Same modern service | Same | Same | Same |
| Project Open/OpenWithUpgrade | string path | FileInfo | FileInfo | FileInfo | FileInfo | FileInfo |
| Project Create | Exact public signature not verified | DirectoryInfo + string | Documented | Documented | Documented | Documented |
| Save/Close | Documented | Retained | Documented | Documented | Documented | Documented |
| Project Archive/Retrieve | Not established | Not established | Not established | Listed as new; detailed signature not in located section | Exact signature documented | Documented |
| PLC block/tag/type enumerate/export/import | Documented; string paths | FileInfo boundary | Documented | Documented | Documented | Documented |
| Compile offline PLC/software/block/type | ICompilable | Retained | Documented | Documented | Documented | Documented |
| SCL XML body round-trip | **No: interface-only export** | Do not claim; exact status unverified | Explicitly new full SCL + embedded SCL XML | Yes | Yes | Yes |
| SCL external-source route | Documented | GenerateSource API moved | Documented | Documented | Documented | Documented; never use removed Create action |
| Classic HMI | HmiTarget model | Retained | Documented | Documented | Documented | Documented |
| Unified engineering | Not documented | Not documented | Not documented | Not documented | HmiSoftware; HmiTags/HmiTagTables | HmiSoftware; Tags/TagTables renamed |
| Documented development framework | .NET 4.6.1 | Unverified separately | .NET 4.6.2 | .NET 4.6.2 | .NET Framework SDK 4.6.2 | .NET Framework SDK 4.8 |

The matrix must distinguish **documented**, **implemented**, **assembly-verified**, and **live-tested**. Only the first is established here.

## Connection and process identity

M14 pp66–80 already documents `new TiaPortal(TiaPortalMode.WithUserInterface)` / `WithoutUserInterface`, `TiaPortal.GetProcesses()`, and `TiaPortalProcess.Attach()`. GetProcesses returns `IList<TiaPortalProcess>`. Useful process fields: `Id`, `Path`, `ProjectPath`, `Mode`, `InstalledSoftware`, `AcquisitionTime`, `AttachedSessions`. Some prose tables have casing/type inconsistencies; use compiled assembly metadata to finalize reflection signatures.

Discovery is not attachment: return process candidates and let caller specify the process. Do not silently attach to the first process when multiple projects exist. Do not infer API generation solely from an executable major version.

M14 uses `Portal V14\PublicAPI\V14\Siemens.Engineering.dll`. SP1 uses `Portal V14\PublicAPI\V14 SP1\Siemens.Engineering.dll`. M15 ships V14 SP1 and V15 public APIs; M151 ships V14 SP1, V15, and V15.1. Therefore host installation version and selected API version are separate fields. Loading a newer installed host is not proof that the old API can access every newer project feature.

## Exact foundational call shapes

### PLC acquisition

M14 pp94–95:

```csharp
ISoftwareContainer container = deviceItem.GetService<ISoftwareContainer>();
SoftwareBase software = container.Software;
PlcSoftware plc = software as PlcSoftware;
```

M15 migration pp550–556 explicitly replaces `Siemens.Engineering.HW.ISoftwareContainer` with `Siemens.Engineering.HW.Features.SoftwareContainer`, and `Siemens.Engineering.HW.SoftwareBase` with `Siemens.Engineering.HW.Software`:

```csharp
SoftwareContainer container = deviceItem.GetService<SoftwareContainer>();
PlcSoftware plc = container?.Software as PlcSoftware;
```

Traverse device groups and nested DeviceItems, collecting matching software; do not assume a fixed CPU index or ordering. Missing software is a meaningful result, not automatically an API incompatibility. M151 p18 documents that PLC2 in an R/H system does not expose SoftwareContainer.

### Project lifecycle

M14 p87: `Projects.Open(string)` and `Projects.OpenWithUpgrade(string)`; `project.Save()` and `project.Close()`. Upgrading changes project data and must remain a separate explicit operation.

SP1 migration plus M15 pp96–100:

```csharp
Project p = portal.Projects.Open(new FileInfo(projectFile));
Project p = portal.Projects.OpenWithUpgrade(new FileInfo(projectFile));
Project p = portal.Projects.Create(new DirectoryInfo(parentDirectory), projectName);
p.Save();
p.Close();
```

M151 p23 establishes project archive/restore availability. M16 pp115 onward provides:

```csharp
void Project.Archive(DirectoryInfo targetDirectory, string targetName,
                    ProjectArchivationMode archivationMode);
Project ProjectComposition.Retrieve(FileInfo sourcePath, DirectoryInfo targetDirectory);
```

Archive requires saved state. Retrieve accepts compressed archives. UMAC and ProjectOpenMode overloads exist; do not choose them accidentally by argument count alone. Original-V14 Create and pre-V15.1 archive should stay unknown/unavailable until verified.

### Blocks, tags, data types

Documented object paths:

```text
PlcSoftware.BlockGroup.Blocks / .Groups
PlcSoftware.TagTableGroup.TagTables / .Groups
PlcSoftware.TypeGroup.Types / .Groups
```

Each group's `.Groups` is recursive; `.Find(name)` is local to its composition. Preserve hierarchical paths and ambiguity errors in MCP identifiers.

M14 pp250–260 establishes:

```csharp
PlcBlock block = plc.BlockGroup.Blocks.Find(name);
block.Export(stringPath, ExportOptions.WithDefaults);
IList<PlcBlock> result = plc.BlockGroup.Blocks.Import(stringPath, ImportOptions.Override);
plc.TagTableGroup.TagTables.Find(name).Export(stringPath, ExportOptions.WithDefaults);
plc.TagTableGroup.TagTables.Import(stringPath, ImportOptions.Override);
plc.TagTableGroup.TagTables.Find(table).Tags.Import(stringPath, ImportOptions.Override);
plc.TypeGroup.Types.Find(name).Export(stringPath, ExportOptions.WithDefaults);
IList<PlcType> types = plc.TypeGroup.Types.Import(stringPath, ImportOptions.Override);
```

SP1 migration replaces XML file arguments with `FileInfo`; M15 supplies matching examples for blocks, tags, and types. `ImportOptions.None` is the conservative non-overwrite choice; `Override` is a separately authorized overwrite. Do not synthesize arbitrary enum numeric values.

Original V14 constants: `PlcTagTable.Constants`, `PlcConstant`. SP1 splits compositions into `UserConstants` and `SystemConstants`, with `PlcUserConstant` and `PlcSystemConstant`; base `PlcConstant` remains. M15 explicitly documents tag-table creation as `plc.TagTableGroup.TagTables.Create(name)`; original V14 direct creation not verified here.

### Compile

M14 pp106–107 and later manuals:

```csharp
ICompilable service = plc.GetService<ICompilable>();
CompilerResult result = service.Compile();
```

`CompilerResult`: State, ErrorCount, WarningCount, Messages. Recursively serialize messages including Path, Description, State and nested Messages, not only the outer success state. Device, DeviceItem, PlcSoftware, CodeBlock, DataBlock, PlcType and block/type groups have documented compile scopes. Offline prerequisites apply. V17 changes some password-protected safety compilation failures from result errors to exceptions; both are failures.

### Source files versus XML

M14 pp175–177: `ExternalSourceGroup.ExternalSources.CreateFromFile(name, stringPath)`, then `PlcExternalSource.GenerateBlocksFromSource()`; legacy source export uses `BlockGroup.GenerateSourceFromBlocks(IEnumerable<PlcBlock>, stringPath)` or `TypeGroup.GenerateSourceFromTypes(IEnumerable<PlcType>, stringPath)`.

M15 pp284–285 documents modern source generation:

```csharp
PlcExternalSourceSystemGroup.GenerateSource(
    IEnumerable<Siemens.Engineering.SW.ExternalSources.IGenerateSource> objects,
    FileInfo sourceFile, GenerateOptions generateOptions);
```

The SP1 migration explicitly moves source generation to ExternalSourceGroup. `GenerateOptions.None` and `WithDependencies` are documented. Match extension to language: SCL .scl, STL .awl, DB .db, UDT .udt.

**Documentation inconsistency:** V15 and V17 CreateFromFile examples still pass a string source path despite the broad SP1 FileInfo migration statement. Bind to the actual installed method signature; do not blindly convert that call. V17 removes the unsupported ExternalSources.Create action; it does not remove CreateFromFile.

## XML compatibility and truthful reads

M14 pp246–248: schemas installed under `PublicAPI\Schemas`, including SW.InterfaceSections.xsd, SW.PlcBlocks.Graph.xsd, SW.PlcBlocks.LADFBD.xsd, SW.PlcBlocks.STL.xsd, SW.PlcBlocks.Access.xsd, SW.Common.xsd. SCL export contains interface only in original V14.

M15 SP1 changes pp561–562: Interface namespace `/SW/Interface/v1` for V14, `/SW/Interface/v2` for SP1; array-element comments/start values use Subelement. Preserve namespace and Engineering version. M15 p21 explicitly adds SCL XML export/import, including SCL networks inside LAD/FBD.

M151 pp414–415: cross-version SimaticML import begins at SP1 and supports older formats into at least the next two major releases. Export format follows **project/Portal model version**, not merely selected API assembly. Newer→older import is not promised. `<Engineering version="V14 SP1"/>` is model metadata; do not simply rewrite it to claim conversion. Apply target-installed schemas and format validation. Preserve raw XML alongside any normalized representation.

Missing dependencies can make imports inconsistent; compilation is a separate validation result. Missing instance DBs are not automatically created (M14 p253). Unsupported/protected content must be reported, never replaced with empty bodies.

## HMI scope and V17 transitions

Classic HMI uses `Siemens.Engineering.Hmi.HmiTarget`, with TagFolder, ScreenFolder, screen templates and VBScript folders; classic XML import/export does not imply Unified XML support. Device-family feature tables constrain supported objects.

M16 pp271–273 documents Unified `HmiSoftware` and acquisition via modern SoftwareContainer. It includes tags, alarms, logs, connections, runtime settings, screens and plant objects. V16 examples use `HmiSoftware.HmiTags` and `HmiSoftware.HmiTagTables`, and a table's `HmiTags`.

M17 pp318–320 explicitly renames:

| V16 Unified property | V17 property |
|---|---|
| HmiTags | Tags |
| HmiTagTables | TagTables |
| HmiSystemTags | SystemTags |
| HmiConnections | Connections |

Alarm and log classes/compositions gain Hmi prefixes (e.g. AnalogAlarm→HmiAnalogAlarm). Screens property remains Screens. This is a genuine adapter boundary; a global `Tags` assumption fails V16. Comprehensive Unified screen generation/serialization was not researched here and must not be advertised as implemented.

## Generic reflection and error handling

M17 pp28–29 states that before V17 some IEngineeringObject methods are explicit-interface implementations. Use `IEngineeringObject.GetAttribute/GetAttributeInfos/GetAttributes` for stable generic reads when public member lookup fails. V17 makes these methods directly visible; naive reflection against public instance members alone undercounts legacy capability.

M151 p16: Openness objects are not inherently thread-safe. Keep operations serialized in an appropriate managed thread/apartment; if attached from STA, associated objects must be used on that STA. Avoid parallel MCP calls into one attached session.

Recommended implementation order: exact version identity and service selection; stable project/PLC discovery; hierarchical block/tag/type reads; raw export; explicit import/overwrite; compile diagnostics; source-generation branch; then release-specific HMI operations. Unit-test argument selection and capability reports before the deferred Windows/TIA verification phase.
