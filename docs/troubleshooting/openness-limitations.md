# TIA Openness — Capability Boundaries

This document lists what the Siemens TIA Openness Public API **cannot do**, based on
static inspection of `D:\app\TIA21\Portal V21\PublicAPI\V21\net48\*.xml`.

If a capability is listed as **NOT SUPPORTED**, do not attempt to add an MCP tool
that pretends to do it via reflection — there is no documented path. These
operations require an alternate channel (OPC UA server on the CPU, S7
communication library, or physical-panel interaction).

> Last verified: 2026-05-09 against TIA Portal V21.0 PublicAPI; cross-checked 2026-09-17 against the
> official V21 online documentation (docs.tia.siemens.cloud, publication dated 03/2026). No V22 exists as of
> that date; V21 Update 1/2 add no new Openness API functions.

---

## Online operations: what is supported

| Capability | API Type/Method | MCP Tool |
|---|---|---|
| Go online / offline | `OnlineProvider.GoOnline / GoOffline` | `GoOnline`, `GoOffline` |
| Read connection state (Offline/Online/Connecting/...) | `OnlineProvider.State` | `GetOnlineState` |
| Download project to CPU (full) | `DownloadProvider.Download(...)` | `DownloadToPlc` |
| Download with retained actual values (do not wipe DB values) | `DataBlockReinitialization` (V20/V21) and `DataBlockReinitializationOrKeepActualValues` (V21) download configurations | `DownloadToPlc` parameter `keepActualValues` (default `true`) |
| Download-prompt coverage | 43 concrete `Download.Configurations` prompt types exist in V21 (42 in V20). The `DownloadToPlc` delegate answers 16 of them; an unanswered prompt aborts the download. **Known defect:** `UserManagementDownload` is a `CurrentSelection` prompt in both V20 and V21 but the delegate treats it as a checkbox, so it stays unanswered. The 27 unhandled types (passwords, memory card, R/H, web apps, HMI, Startdrive) are listed in `docs/development/roadmap.md` E7. | *engine fix pending* |
| Download to a Windows folder (memory-card image; also PLCSIM Advanced target; no PLC connection) | `DownloadProvider.Download(DirectoryInfo, delegate)` with `TargetForSoftware`, `OverwriteOnMemoryCard`, `SafetyProgram` | *not yet wrapped* |
| Pre-download readiness check | (custom probe) | `CheckDownloadReadiness` |
| Compare offline project vs live CPU | `PlcSoftware.CompareToOnline()` | `CompareSoftwareToOnline` |
| Set CPU access password (for protected modules) | `OnlinePasswordConfiguration.SetPassword(SecureString)` | `password` parameter on `GoOnline` and `DownloadToPlc` |
| Read watch-table values online | reflection over `PlcWatchTableEntry` | `ReadPlcWatchTableCurrentValuesReadOnly` |
| Edit watch-table modify values (offline definition) | `PlcWatchTableEntry.ModifyValue` | `SetWatchTableModifyValue` |
| Edit force-table values (offline definition) | `PlcForceTableEntry.ForceValue` | *(deliberately not exposed as an MCP tool — forcing is a human-only action)* |

> Note on Watch/Force: TIA Openness exposes the **table definition**, but no
> documented method to "send modify now" or "apply force now" as a discrete
> runtime command. The values become effective when TIA Portal is online and
> the table's trigger fires. If you need precise runtime push, use OPC UA.

---

## Online operations: NOT supported via Openness

These were investigated and are **not present** in V21 PublicAPI XML:

| Capability | What was searched | Workaround |
|---|---|---|
| **Read CPU operating mode (RUN/STOP/STARTUP)** | `CpuOperatingState`, `OperatingMode`, `RequestStateChange` — none found | OPC UA client; or physical panel readback |
| **Change CPU operating mode (Run/Stop) as a standalone command** | `Run()`, `Stop()`, `RequestStateChange()` on online providers — none found. **Nuance:** the official chapter "Running and stopping PLC" only exposes `StopModules.CurrentSelection = StopAll` (pre-download) and `StartModules.CurrentSelection = StartModule` (post-download) *inside a `DownloadConfiguration` delegate* — i.e. mode changes are bound to a download, not independently invocable | OPC UA client; manual via TIA Portal UI; or accept the stop/start that a `DownloadToPlc` performs |
| **Clear all forces / unforce** | `ClearForces`, `Unforce`, `RemoveForce` — none found | Delete force-table entries via project, then download |
| **Read diagnostic / fault buffer** | `DiagnosticBuffer`, `FaultBuffer`, `DiagnosticEntry` — none found in any XML | OPC UA `Server` namespace; or S7 SZL request |
| **Selective per-block download** | `DownloadSelectionConfiguration` exists, but no documented filter API | Use full `DownloadToPlc`; reflection probe is fragile |

If a user asks for any of these, the MCP server should politely refuse with a
pointer to this document, **not** silently fail or return a misleading
"success". Do not implement reflection-based stubs that look like they work.

---

## Hardware operations: NOT supported

| Capability | Status |
|---|---|
| Read CPU diagnostic LEDs status remotely | Not exposed |
| Read module slot health (online) | Not exposed |
| Identify online PROFINET nodes from a discovery scan | **Supported, but not yet wrapped as an MCP tool.** `ConfigurationPcInterface.GetAccessibleDevices()` returns a live snapshot of reachable participants on a chosen PC interface (Name / Address / MAC / DeviceSeries) and can feed `StationUpload`. See [official chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-configuration-accessible-devices). *(Earlier revisions of this file wrongly said this was limited to project config.)* |

---

## When to suggest OPC UA instead

The TIA Openness API is fundamentally an **engineering / project-modification**
API. It models "the project I am editing in TIA Portal", not "the CPU running
right now". When a user asks for runtime data (current variable value, RUN
state, alarms, diagnostics history), redirect them to:

1. Enable the CPU's OPC UA server (`SetOpcUaInterfaceEnabled` MCP tool)
2. Connect with an OPC UA client (separate component, not this MCP server)

This boundary is by design — Siemens publishes OPC UA as the runtime data
channel and Openness as the engineering channel.

---

## How to keep this document accurate

When a new TIA Portal version is released:

1. Re-run the static API inspection against the new `PublicAPI\V<n>\net48` directory
2. Diff against the current "supported" / "not supported" lists
3. Update this file before announcing version support

Search patterns that proved useful (use over `*.xml` files):

- `OperatingState|OperatingMode|RunStop|RequestStateChange` — CPU mode
- `ForceValue|ModifyValue|ClearForce|ApplyForce` — force/watch
- `CompareToOnline|CompareTo` — compare APIs
- `OnlinePassword|SetPassword|OnlineCredentials` — auth
- `DiagnosticBuffer|FaultBuffer|DiagnosticEntry` — diagnostics
- `DownloadSelectionConfiguration|DownloadConfiguration` — download configs
