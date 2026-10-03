# Adapter contracts (step B)

Targets: net461, net48 and net8.0. No Siemens or JSON dependency. Existing declarations keep
their namespaces, member order, accessors and defaults. The root and six facet interfaces
are markers only; existing adapters are not wired to them until step D.

## Moved declarations

Original sources are in `../TiaMcpServer.PlcFoundation/`. They remain as native/policy files.

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
