# Adapter contracts (steps B, D and G)

Targets: net48 and net10.0. No Siemens or JSON dependency. Existing declarations keep
their namespaces, member order, accessors and defaults. Step D implements session, PLC program and PLC data facets by delegation. The worker still calls the existing engine.
Step G adds `IStudioSession` and fills hardware (device listing), VCI and HMI export
facets for the opt-in `StudioAdapter`. The Foundation adapter continues returning
null for these surfaces. [Adapter documentation](../TiaMcp.Adapters/README.md)
describes the separate Studio policy, release capabilities and STA ownership.
The `Studio/` values preserve the original Studio DTO shapes in a distinct
namespace; Core and bridge keep using their original DTOs until the host switch.

## Moved declarations

The remaining sources now live under `../TiaMcp.Adapters/Native/` and `../TiaMcp.Adapters/Policy/`.
The tables below record the declarations moved in step B; the step D inventory gives their current paths.

| Original file | Types moved verbatim |
|---|---|
| `BindingSnapshotContract.cs` | `BindingObservationState`, `BindingIdentityStrength`, `BindingObservation`, `BindingSnapshotObservation`, `BindingProcessObservation`, `BindingCachedFields`, `BindingProjectObservation` |
| `PlcBatchDocumentExportPolicy.cs` | `PlcBatchDocumentExportItem`, `PlcBatchDocumentExportResult` |
| `PlcBatchDocumentImportPolicy.cs` | `PlcBatchDocumentImportItem`, `PlcBatchDocumentImportResult` |
| `PlcBatchExportPolicy.cs` | `PlcBatchExportItem`, `PlcBatchExportResult` |
| `PlcBatchImportPolicy.cs` | `PlcBatchImportObject`, `PlcBatchImportItem`, `PlcBatchImportFailure`, `PlcBatchImportResult` |
| `PlcCompilePolicy.cs` | `PlcDiagnostic` |
| `PlcDeviceAddPolicy.cs` | `PlcDeviceAddResult` |
| `PlcDisconnectPolicy.cs` | `PlcDisconnectResult` |
| `PlcDocumentExportPolicy.cs` | `PlcDocumentExportResult` |
| `PlcDocumentImportPolicy.cs` | `PlcDocumentImportResult` |
| `PlcExternalSourceDeletePolicy.cs` | `PlcExternalSourceDeleteResult` |
| `PlcExternalSourceImportPolicy.cs` | `PlcExternalSourceImportPlan` |
| `PlcExternalSourceWorkflowPolicy.cs` | `PlcExternalSourceWorkflowResult`, `PlcExternalSourceImportResult`, `PlcExternalSourceObject`, `PlcExternalSourceGenerationResult` |
| `PlcFoundationPolicy.cs` | `PlcObjectInfo`, `PlcMutationResult`, `PlcCompileResult` |
| `PlcHardwareCatalogPolicy.cs` | `PlcHardwareCatalogCandidate`, `PlcHardwareCatalogSearchResult` |
| `PlcLifecyclePolicy.cs` | `PlcConnectionResult` |
| `PlcReadContracts.cs` | `PlcProjectDetails`, `PlcAttributeValue`, `PlcTypeDetails`, `PlcBlockDetails`, `PlcBlockHierarchy` |
| `PlcRuntimeQueryPolicy.cs` | `PlcRuntimeState`, `PlcProcessSnapshot`, `PlcProcessQuery`, `PlcConnectReadiness` |
| `PlcSoftwareRead.cs` | `PlcSoftwareDetails`, `PlcSoftwareTreeDetails` |
| `PlcSpecialExportPolicy.cs` | `PlcSpecialExportResult` |
| `PlcSupplementaryReadPolicy.cs` | `PlcTechnologyReadRow`, `PlcSupplementaryReadResult` |

`PlcExternalSourceImportPlan` referenced a policy constant. Its exact text now lives in
`ExternalSourceImportContract`; a file-local alias preserves the DTO declaration, and the
adapter policy uses the same constant. No text or decision changed.

## Considered and retained

The following inventory includes all remaining Foundation types and the four host envelopes.
Internal field bags are policy/native implementation inputs, not the PascalCase worker wire
contract. Worker requests remain the existing operation parameters.

| Original file | Retained types | Reason |
|---|---|---|
| `BindingSnapshotAdapter.cs` | `PlcFoundationEngine`, `BoundProjectObservationSource` | Native object access, ownership, traversal or calls. |
| `BindingSnapshotContract.cs` | `BindingLifecycleChange`, `IBindingProcessObservationSource`, `IBindingProjectObservationSource`, `BindingObservationPolicy`, `BindingSnapshotTracker` | Lifecycle state and native/OS observation callbacks. |
| `BindingSnapshotProcessSource.cs` | `BindingSnapshotProcessSource` | Native context or OS observation/callback implementation. |
| `MutationIdentityPolicy.cs` | `MutationIdentityPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcBatchDocumentExport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcBatchDocumentExportPolicy.cs` | `PlcBatchDocumentExportSource`, `PlcBatchDocumentExportPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcBatchDocumentImport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcBatchDocumentImportPolicy.cs` | `PlcDocumentImportContext`, `PlcBatchDocumentImportPolicy`, `BorrowedStream` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcBatchExport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcBatchExportPolicy.cs` | `PlcBatchExportSource`, `PlcBatchExportPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcBatchImport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcBatchImportPolicy.cs` | `PlcBatchImportRequest`, `PlcBatchImportDependency`, `PlcBatchImportPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcBlockXmlPolicy.cs` | `PlcBlockXmlCapability`, `PlcBlockXmlPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcCompilePolicy.cs` | `PlcCompilePolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcDeviceAdd.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcDeviceAddPolicy.cs` | `PlcDeviceAddRequest`, `PlcDeviceAddItem`, `PlcDeviceAddPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcDisconnectPolicy.cs` | `PlcDisconnectState` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcDocumentExport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcDocumentExportPolicy.cs` | `PlcDocumentExportPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcDocumentImport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcDocumentImportPolicy.cs` | `PlcDocumentImportRequest`, `PlcDocumentImportNative`, `PlcDocumentImportPolicy`, `PlcDocumentDeclaration`, `Parser` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcExchangePolicy.cs` | `PlcExchangePolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcExportPublication.cs` | `PlcExportPublication` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcExternalSourceDelete.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcExternalSourceDeletePolicy.cs` | `PlcExternalSourceDeleteIdentities`, `PlcExternalSourceDeleteItem`, `PlcExternalSourceDeleteRequest`, `PlcExternalSourceDeletePolicy` | Native context or OS observation/callback implementation. |
| `PlcExternalSourceImport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcExternalSourceImportPolicy.cs` | `PlcExternalSourceImportRequest`, `PlcExternalSourceImportPolicy` | Native context or OS observation/callback implementation. |
| `PlcExternalSourceWorkflow.cs` | `PlcFoundationEngine`, `ExternalSourceNativeObject` | Native object access, ownership, traversal or calls. |
| `PlcExternalSourceWorkflowPolicy.cs` | `PlcExternalSourceWorkflowPolicy` | Native context or OS observation/callback implementation. |
| `PlcFoundationEngine.cs` | `PlcFoundationEngine`, `Located<T>` | Native object access, ownership, traversal or calls. |
| `PlcFoundationPolicy.cs` | `PlcFoundationPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcHardwareCatalog.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcHardwareCatalogPolicy.cs` | `PlcHardwareCatalogPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcLifecyclePolicy.cs` | `PlcLifecycleState`, `PlcLifecyclePolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcOfflineChecks.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcOfflinePolicy.cs` | `PlcOfflineObservation`, `PlcOfflinePolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcReadContracts.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcReadPathPolicy.cs` | `PlcReadCandidate<T>`, `PlcReadPathPolicy` | Native context or OS observation/callback implementation. |
| `PlcRuntimeQueries.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcRuntimeQueryPolicy.cs` | `PlcRuntimeQueryPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcSoftwareRead.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcSoftwareReadPolicy.cs` | `PlcSoftwareTreeNode`, `PlcSoftwareReadBudget`, `PlcSoftwareReadPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcSpecialExport.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcSpecialExportPolicy.cs` | `PlcSpecialExportPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| `PlcSupplementaryRead.cs` | `PlcFoundationEngine` | Native object access, ownership, traversal or calls. |
| `PlcSupplementaryReadPolicy.cs` | `PlcSupplementaryReadPolicy` | Internal policy state, inputs, validation, file access or callback orchestration. |
| LegacyHost | `V17ReadEnvelope`, `V17MutationEnvelope`, `V17CompileEnvelope`, `V17ProjectEnvelope` | JSON/SDK-dependent validation, naming policy and timestamp construction; not data wrappers. |

## Versioning visibility (R1)

`TIA_ADAPTER_INTERNAL_VERSIONING` makes only the adapter copies of `TiaVersionCatalog` and
`TiaVersionDescriptor` internal. Logic keeps both public. The eight adapter define expectations
gain exactly this symbol; native feature flags are unchanged.

No repository consumer required the public adapter copy; no versioning friend was added.
Adapter-local consumers are `PlcFoundationPolicy`, `PlcCompilePolicy`, `PlcLifecyclePolicy`
and source-linked `OpennessReleaseContract`. Workers use the facade. LegacyHost and offline
tests use Logic; other source-linked catalog consumers keep their previous visibility.

## Compatibility evidence

The 208 exact JSON checks passed against the original woven V20 adapter before any move.
The sample builder and assertions are unchanged apart from the required LF normalization;
the golden JSON is byte-identical.
They cover all 48 moved types, defaults/nulls, empty/populated collections, all moved enum
values, polymorphic values, nested envelopes and successful/failed batch items.
Newtonsoft defaults match PlcWorker; STJ uses the MCP options used by LegacyHost.
The new `adapter-contracts` suite has 232 checks including error and marker tests.

| Release | Total weave sites before → after | Siemens sites (unchanged) |
|---|---:|---:|
| 14sp1 | 1181 → 1174 | 512 |
| 15.1 | 1208 → 1201 | 534 |
| 16 | 1213 → 1206 | 539 |
| 17 | 1240 → 1233 | 563 |
| 18 | 1240 → 1233 | 563 |
| 19 | 1296 → 1289 | 605 |
| 20 | 1388 → 1381 | 665 |
| 21 | 1385 → 1378 | 662 |

All eight Siemens member multisets are identical. Exactly seven `enumeration-input` sites
leave each adapter with `PlcBatchImportResult`: `Imported` has Where/SelectMany/ToArray plus
its nested Select; `Failed` has Where/Select/ToArray. They enumerate DTO arrays only.
No Siemens calls, parameters, sequence or thread ownership changed. Builds, metadata and
fake API tests are offline evidence, not native acceptance.

## Step D: source ownership and delegation

All 46 remaining files were moved byte for byte, retaining namespaces, comments and original
line endings. No project file remained in the old directory. The explicit adapter allowlist
still compiles every moved source into each woven release assembly; no policy moved to a
separate assembly.

Only four files meet the strict pure-rule boundary: block XML admission, lifecycle bookkeeping,
runtime DTO diagnosis and software snapshot rendering. Files named `Policy` stay in Native
when they enumerate native values or control native callbacks:

- `PlcFoundationPolicy`, `PlcExchangePolicy` and `PlcReadPathPolicy` select from lazy native
  inventories; read candidates also retain native software/device contexts.
- `PlcOfflinePolicy` traverses device/software objects and invokes online-state observations.
  `PlcCompilePolicy` consumes the lazy native compiler-message projection.
- Batch/document/external-source import and export policies order validation, inventory,
  offline checks, mutation callbacks and publication. `PlcExternalSourceDeletePolicy` also
  retains native object identities. `PlcExportPublication` invokes the native export callback.
- `PlcSupplementaryReadPolicy` traverses native groups and invokes metadata getters.
  `PlcHardwareCatalogPolicy` consumes native Find projections; `PlcDeviceAddPolicy` orders
  catalog/inventory reads, checks and creation.
- `BindingSnapshotContract` owns observation callback ordering (including repeated native
  project reads); `PlcDisconnectPolicy` orders detach; `MutationIdentityPolicy` can invoke
  the bound-project native identity check. These remain in Native/Session conservatively.

`OpennessAdapter` borrows an existing, release-validated `PlcFoundationEngine`. Its 14 session,
26 program and 16 data members forward once with unchanged argument order, defaults, return
objects and exceptions. It does not dispose the engine, create a session, switch threads,
translate errors, or introduce a new worker dispatch path. API identity comes from the same
exact-release contract that the engine validates. Facet capability bits describe implemented
surfaces; the additional feature bits reflect all nine Adapter-profile compiler defines in
`TiaFeatures.props`. For example, HardwareCatalog describes compiled native support, while
Hardware remains unset and its still-unimplemented facet is null.

The software-read fake-SDK suite exercises all forwarding members: 54 engine-boundary spies
check explicit/default arguments, result identity, exception identity/evidence, call count
and thread; the two real software-read methods run through the existing fake SDK and compare
results and failures with direct engine calls. The same suite is compiled for all eight
release feature sets. Contract checks retain the 208 golden JSON cases and reject native/JSON
references and non-contract facet parameter/return types.

The fifth fake-SDK project is `TiaMcpServer.DiagnosticMembershipTests`; it links the full
engine's `Siemens/Openness.cs`, so it has no Foundation path to update. Worker isolation lives
at `TiaMcp.Adapters/build/Test-WorkerIsolation.ps1` (there is no checks-directory copy) and has
no old Foundation source path. Its selection checks now require exactly the selected adapter
plus Contracts, correcting the obsolete single-reference assumption from before step B.
The three required-file lists and solution files likewise have
no dependency on the deleted directory. The swallowed-exception scan already covers the whole
MCP source tree. Their source paths require no edits.

The foundation minimum rises only by the 11 additional source-closure checks (6593 → 6604),
verified without the PublicAPI test environment variable. The software-read minimum rises
by the 218 added facet checks (39 → 257); it needs neither PublicAPI nor prebuilt adapters.
The foundation-api and adapter-contracts minimums remain unchanged.

### Step D move inventory

Paths are relative to `TiaMcp.Adapters/`; original basenames are unchanged.

| Source | Destination |
|---|---|
| `BindingSnapshotAdapter.cs` | [Native/Session/BindingSnapshotAdapter.cs](../TiaMcp.Adapters/Native/Session/BindingSnapshotAdapter.cs) |
| `BindingSnapshotContract.cs` | [Native/Session/BindingSnapshotContract.cs](../TiaMcp.Adapters/Native/Session/BindingSnapshotContract.cs) |
| `BindingSnapshotProcessSource.cs` | [Native/Session/BindingSnapshotProcessSource.cs](../TiaMcp.Adapters/Native/Session/BindingSnapshotProcessSource.cs) |
| `MutationIdentityPolicy.cs` | [Native/Session/MutationIdentityPolicy.cs](../TiaMcp.Adapters/Native/Session/MutationIdentityPolicy.cs) |
| `PlcBatchDocumentExport.cs` | [Native/Plc/PlcBatchDocumentExport.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchDocumentExport.cs) |
| `PlcBatchDocumentExportPolicy.cs` | [Native/Plc/PlcBatchDocumentExportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchDocumentExportPolicy.cs) |
| `PlcBatchDocumentImport.cs` | [Native/Plc/PlcBatchDocumentImport.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchDocumentImport.cs) |
| `PlcBatchDocumentImportPolicy.cs` | [Native/Plc/PlcBatchDocumentImportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchDocumentImportPolicy.cs) |
| `PlcBatchExport.cs` | [Native/Plc/PlcBatchExport.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchExport.cs) |
| `PlcBatchExportPolicy.cs` | [Native/Plc/PlcBatchExportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchExportPolicy.cs) |
| `PlcBatchImport.cs` | [Native/Plc/PlcBatchImport.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchImport.cs) |
| `PlcBatchImportPolicy.cs` | [Native/Plc/PlcBatchImportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcBatchImportPolicy.cs) |
| `PlcBlockXmlPolicy.cs` | [Policy/PlcBlockXmlPolicy.cs](../TiaMcp.Adapters/Policy/PlcBlockXmlPolicy.cs) |
| `PlcCompilePolicy.cs` | [Native/Plc/PlcCompilePolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcCompilePolicy.cs) |
| `PlcDeviceAdd.cs` | [Native/Hardware/PlcDeviceAdd.cs](../TiaMcp.Adapters/Native/Hardware/PlcDeviceAdd.cs) |
| `PlcDeviceAddPolicy.cs` | [Native/Hardware/PlcDeviceAddPolicy.cs](../TiaMcp.Adapters/Native/Hardware/PlcDeviceAddPolicy.cs) |
| `PlcDisconnectPolicy.cs` | [Native/Session/PlcDisconnectPolicy.cs](../TiaMcp.Adapters/Native/Session/PlcDisconnectPolicy.cs) |
| `PlcDocumentExport.cs` | [Native/Plc/PlcDocumentExport.cs](../TiaMcp.Adapters/Native/Plc/PlcDocumentExport.cs) |
| `PlcDocumentExportPolicy.cs` | [Native/Plc/PlcDocumentExportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcDocumentExportPolicy.cs) |
| `PlcDocumentImport.cs` | [Native/Plc/PlcDocumentImport.cs](../TiaMcp.Adapters/Native/Plc/PlcDocumentImport.cs) |
| `PlcDocumentImportPolicy.cs` | [Native/Plc/PlcDocumentImportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcDocumentImportPolicy.cs) |
| `PlcExchangePolicy.cs` | [Native/Plc/PlcExchangePolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcExchangePolicy.cs) |
| `PlcExportPublication.cs` | [Native/Plc/PlcExportPublication.cs](../TiaMcp.Adapters/Native/Plc/PlcExportPublication.cs) |
| `PlcExternalSourceDelete.cs` | [Native/Plc/PlcExternalSourceDelete.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceDelete.cs) |
| `PlcExternalSourceDeletePolicy.cs` | [Native/Plc/PlcExternalSourceDeletePolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceDeletePolicy.cs) |
| `PlcExternalSourceImport.cs` | [Native/Plc/PlcExternalSourceImport.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceImport.cs) |
| `PlcExternalSourceImportPolicy.cs` | [Native/Plc/PlcExternalSourceImportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceImportPolicy.cs) |
| `PlcExternalSourceWorkflow.cs` | [Native/Plc/PlcExternalSourceWorkflow.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceWorkflow.cs) |
| `PlcExternalSourceWorkflowPolicy.cs` | [Native/Plc/PlcExternalSourceWorkflowPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcExternalSourceWorkflowPolicy.cs) |
| `PlcFoundationEngine.cs` | [Native/Session/PlcFoundationEngine.cs](../TiaMcp.Adapters/Native/Session/PlcFoundationEngine.cs) |
| `PlcFoundationPolicy.cs` | [Native/Plc/PlcFoundationPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcFoundationPolicy.cs) |
| `PlcHardwareCatalog.cs` | [Native/Hardware/PlcHardwareCatalog.cs](../TiaMcp.Adapters/Native/Hardware/PlcHardwareCatalog.cs) |
| `PlcHardwareCatalogPolicy.cs` | [Native/Hardware/PlcHardwareCatalogPolicy.cs](../TiaMcp.Adapters/Native/Hardware/PlcHardwareCatalogPolicy.cs) |
| `PlcLifecyclePolicy.cs` | [Policy/PlcLifecyclePolicy.cs](../TiaMcp.Adapters/Policy/PlcLifecyclePolicy.cs) |
| `PlcOfflineChecks.cs` | [Native/Plc/PlcOfflineChecks.cs](../TiaMcp.Adapters/Native/Plc/PlcOfflineChecks.cs) |
| `PlcOfflinePolicy.cs` | [Native/Plc/PlcOfflinePolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcOfflinePolicy.cs) |
| `PlcReadContracts.cs` | [Native/Plc/PlcReadContracts.cs](../TiaMcp.Adapters/Native/Plc/PlcReadContracts.cs) |
| `PlcReadPathPolicy.cs` | [Native/Plc/PlcReadPathPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcReadPathPolicy.cs) |
| `PlcRuntimeQueries.cs` | [Native/Session/PlcRuntimeQueries.cs](../TiaMcp.Adapters/Native/Session/PlcRuntimeQueries.cs) |
| `PlcRuntimeQueryPolicy.cs` | [Policy/PlcRuntimeQueryPolicy.cs](../TiaMcp.Adapters/Policy/PlcRuntimeQueryPolicy.cs) |
| `PlcSoftwareRead.cs` | [Native/Plc/PlcSoftwareRead.cs](../TiaMcp.Adapters/Native/Plc/PlcSoftwareRead.cs) |
| `PlcSoftwareReadPolicy.cs` | [Policy/PlcSoftwareReadPolicy.cs](../TiaMcp.Adapters/Policy/PlcSoftwareReadPolicy.cs) |
| `PlcSpecialExport.cs` | [Native/Plc/PlcSpecialExport.cs](../TiaMcp.Adapters/Native/Plc/PlcSpecialExport.cs) |
| `PlcSpecialExportPolicy.cs` | [Native/Plc/PlcSpecialExportPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcSpecialExportPolicy.cs) |
| `PlcSupplementaryRead.cs` | [Native/Plc/PlcSupplementaryRead.cs](../TiaMcp.Adapters/Native/Plc/PlcSupplementaryRead.cs) |
| `PlcSupplementaryReadPolicy.cs` | [Native/Plc/PlcSupplementaryReadPolicy.cs](../TiaMcp.Adapters/Native/Plc/PlcSupplementaryReadPolicy.cs) |

### Step D native coverage

| Release | Total sites before → after | Siemens sites before → after |
|---|---:|---:|
| 14sp1 | 1174 → 1174 | 512 → 512 |
| 15.1 | 1201 → 1201 | 534 → 534 |
| 16 | 1206 → 1206 | 539 → 539 |
| 17 | 1233 → 1233 | 563 → 563 |
| 18 | 1233 → 1233 | 563 → 563 |
| 19 | 1289 → 1289 | 605 → 605 |
| 20 | 1381 → 1381 | 665 → 665 |
| 21 | 1378 → 1378 | 662 → 662 |

All eight adapters and workers build before and after against the exact local PublicAPI.
NativeCallWeaver verify passes for every copied adapter. The Siemens member multiset and
per-method Siemens call order are identical, as is the complete boundary multiset. The 56
new forwarding methods have no Siemens access and add zero weave sites. No original call
sequence, parameter or thread ownership changes. Native acceptance remains NOT RUN.
