# API audit VM checklist

Status: current · Source: the 2026-10-19 official-API audit, the P8-81a/P8-81b/P8-81c fix reports (2026-10-19 to 21) and the
P8-73b Workbench fix report (2026-10-21).

The audit compared the repository with the Siemens SDK XML and the official Openness documentation. The items below depend on native
TIA Portal behavior that neither the SDK XML nor the documentation states, so the offline checks cannot verify them. None of them has
been run on a real TIA Portal. Pre-release notes copy this list into their "Not verified on a real TIA Portal" section
(`generate-prerelease-notes`); see [the real-machine ledger](real-machine-ledger.md) for the other open real-machine items.

Every row names its source: the fix report and its VM item number, and the audit area file and finding id. The reports and the
audit files are kept in the maintainer checkout and are not tracked: `bin-build/refactor/P8-81a-evidence/P8-81a.result.md`,
`bin-build/refactor/P8-81b-evidence/P8-81b.result.md` and `bin-build/refactor/P8-81c-evidence/P8-81c.result.md` ("VM checklist"
sections), and `bin-build/refactor/api-audit/<area>.md`. The
Workbench fix items come from `bin-build/refactor/P8-73b.result.md` and item O1 of `bin-build/refactor/P8-70-73-triage.md`.

## Online, download and simulation (fixed in P8-81a)

1. P8-81a VM item 1 · online.md F1: DisconnectOnlinePlc on an online PLC on the PlcWorker path reaches GoOffline and reads back
   Offline.
2. P8-81a VM item 2 · online.md F2 (no R/H hardware here): rhTarget reaches the R/H branches; `RHDownloadProvider.Configuration`
   lists the routes; the legitimation prompts arrive on it; the V17+ 5-argument R/H overloads work with a created address; the R/H
   download precondition (both CPUs Offline) is right for downloading one CPU.
3. P8-81a VM item 3 · online.md F3: `DataBlockReinitialization=NoAction` aborts with a clear error; `StopPlcAndReinitialize` with
   stopBeforeDownload resets values.
4. P8-81a VM item 4 · online.md F4: DownloadPlc refuses an online PLC before any native call; ConnectOnlinePlc on an online PLC
   returns Online; the text of the ApplyConfiguration exception while online.
5. P8-81a VM item 5 · online.md F5: an exception thrown from the pre-download delegate stops the download before any transfer and how
   TIA surfaces it; whether an untouched SafetyProgram prompt or `LoadNothing` alone would already abort; that the five prompts are
   pre-download configurations.
6. P8-81a VM item 6 · online.md F6: `IsSecureCommunication` values on PLCSIM Advanced and on a non-TLS protected CPU; a protected
   download with and without `allowNonTlsLogin`.
7. P8-81a VM item 7 · online.md F7: content of V21 `TlsVerificationConfiguration.Certificates`; the first contact with default false
   is refused and reports the certificate; whether a trusted decision persists beyond the session.
8. P8-81a VM item 8 · online.md F8: `IsConfigured` on a fresh project and after a ConnectOnlinePlc with a route; GoOnline states
   while Connecting.
9. P8-81a VM item 9 · online.md F11, F12: `ApplyConfiguration(ConfigurationAddress)` returns true for subnet, gateway and created
   addresses on the PLCSIM route (otherwise the `download-plcsim` sequence is refused); the typed Download overload on V15.1/V16.
10. P8-81a VM item 10 · online.md F13: CompareIrrelevant in real online compares.
11. P8-81a VM item 11 · online.md F14: whether `ConfigurationAddressComposition.Create` persists addresses in the project;
    UploadStationFromPlc execution with scan + create after approval.
12. P8-81a VM item 12 · online.md F16: ParameterUpload with a null address on a StartDrive head module.
13. P8-81a VM item 13 · online.md F17: which ERuntimeErrorCode values PowerOn returns without an exception (warning names).
14. P8-81a VM item 14 · online.md F20: on V20/V21 (MTA owner), pre/post download delegates and `OnlineLegitimation` callbacks fire
    on the owning thread.
15. P8-81a VM item 15 · online.md F10: downloadScope=softwareOnlyChanges on V17+.
16. P8-81a VM item 16 · online.md "Not verified / out of reach": InterfaceSnapshot service owner on 15.1/16; V21 Download
    fire-and-forget on the PLC path.

## Hardware and networks (fixed in P8-81b)

1. P8-81b VM item 1 · hardware.md H-01: where TIA files a device created through `project.Devices.CreateWithItem` (root vs
   UngroupedDevicesGroup) for CPUs, HMIs, ET 200 heads and GSD devices; path tools (`GetDeviceInfo`, `devicePathJson` tools,
   `["$ungrouped", "<device>"]`) on an ungrouped station; Studio tree shows ungrouped stations under the group's name;
   `ValidateAutomationContext` lists grouped/ungrouped devices.
2. P8-81b VM item 2 · hardware.md H-02, H-03, H-04 (device creation decision): CreateDevice with a wrong version is refused with the
   catalog candidates and the inventory is unchanged; the exact message TIA throws; an empty version for a module with several
   versions; `CreateWithItem` never leaves a partial device after throwing (the unknown branch).
3. P8-81b VM item 3 · hardware.md H-03: scaffold apply with `plcMlfb`/`plcVersion` and `hmiMlfb`/`hmiVersion` (Unified PC/panel
   identifiers per release).
4. P8-81b VM item 4 · hardware.md H-05: the exact `GSD:` TypeIdentifier format (type segment, id, file-name casing) for re-enabling
   CreateGsdDevice on V14 SP1-V17; on V18+ that installed GSD devices appear in `HardwareCatalog.Find` with a `GSD:` TypeIdentifier.
5. P8-81b VM item 5 · hardware.md H-06: GSDML folder name on V15.1/V14 SP1 (`Portal V15_1`?).
6. P8-81b VM item 6 · hardware.md H-07: the argument `FindModuleTypes` expects (`OrderNumber:<article>` vs the bare article) and its
   result format; PlugDeviceItem on an S7-1200 signal board and an S7-1500 rail module.
7. P8-81b VM item 7 · hardware.md H-09: whether `HardwareCatalog.Find` matches a full `OrderNumber:...` TypeIdentifier text (exact
   paths and the candidate list use the article part only for the list).
8. P8-81b VM item 8 · hardware.md H-10: TIA's name-collision rules for device vs head item vs other items (only device and top-level
   item names are checked).
9. P8-81b VM item 9 · hardware.md H-11: parent of IO devices created through the safe-v4 candidate path.
10. P8-81b VM item 10 · hardware.md H-12, H-13: `SupportedTypes` content for typical CPU attributes; a read-only attribute is
    refused; a normalized value reports `valueMismatch`.
11. P8-81b VM item 11 · hardware.md H-14, H-15, H-16: EnsureSubnet with an existing node-less subnet; Attach to a node on another
    subnet is refused; ConnectDeviceNodes with an existing subnet of that name joins it; HMI connections listed from the HMI software
    under the second root.
12. P8-81b VM item 12 · hardware.md H-17: a duplicate IO system name is refused; the post-create checks hold for a real controller
    (count and Subnet).
13. P8-81b VM item 13 · hardware.md H-18: V19 multicast create/createReceiver/delete and DataLength/Addresses read (host gate open
    since the follow-up).
14. P8-81b VM item 14 · hardware.md H-19: V19+ TransferResult content of an AML import.
15. P8-81b VM item 15 · hardware.md H-21, H-22: rail-module address read-back after plug; StartAddress change moving the other IO
    type; PLC online refusal text.
16. P8-81b VM item 16 · hardware.md H-01 (ungrouped devices in every device walk, VCI): ConnectProjectToWorkspace (dry run and real
    run, legacy V16-V19 and modern V20/V21 VCI) maps the devices of the "Ungrouped devices" folder with workspace file names prefixed
    `UngroupedDevices_`; the exact text when VCI refuses such a device (FAILED line); a root device group named `UngroupedDevices` is
    refused with PRECONDITION_FAILED before anything is mapped.
17. P8-81b VM item 17 · hardware.md H-01 (ungrouped devices in every device walk, delivered PlcWorker; the same adapter code on all
    eight releases, the engine lookup is no longer delivered since P8-04j2): a PLC in the ungrouped folder (if TIA allows one there)
    is in the PlcWorker's PLC inventory under `ungrouped/<device>/...` (also used by DisconnectOnlinePlcs) and is resolved by that
    exact address and by its device or CPU-item name; group-qualified aliases do not apply to it.
18. P8-81b VM item 18 · hardware.md H-03 (scaffold spec keys): `tia schema` shows the new spec lines on all eight releases.

## Session and project lifecycle (fixed in P8-81c)

1. P8-81c VM item 1 · session.md F1: a worker-started headless TIA Portal closes on DisconnectPortal; DisconnectPortal is refused
   while a project is open in it; with `--with-ui` Dispose only disconnects; whether the headless TIA Portal outlives the worker
   when the worker is killed; what happens to unsaved changes when shutdown disposes a worker-started portal with an open project.
2. P8-81c VM item 2 · session.md F2: OpenProject/RetrieveProjectArchive of a UMAC-protected project with correct, wrong and missing
   credentials on 15.1-21; the exception type TIA throws without credentials (if it is not `EngineeringSecurityException`, the
   outcome stays unknown); on 17-21 whether `TiaPortal.Authentication` is raised in a headless session and whether
   `AuthenticationEventArgs.UmacCredentials` is non-null for the handler; `Global` users through a UMC server.
3. P8-81c VM item 3 · session.md F3: V20/V21 Studio bridge on its MTA owner thread (attach, open, export, VCI); the V14 SP1-V19
   PlcWorker and bridge on their console STA without a pump (the planned eight-release MTA check).
4. P8-81c VM item 4 · session.md F4: the actual exceptions for a firewall denial on Attach, missing products on Open/Retrieve, a
   missing licence and a MultiuserException; partial commit after a MultiuserException from CloseAndCommit (what was committed or
   closed; the MCP keeps OUTCOME_UNKNOWN and requires a session reset); "process has ProjectPath, Projects empty" on a project with
   missing products.
5. P8-81c VM item 5 · session.md F5: OpenProject/CreateProject refused on a portal with an open primary project or local session.
6. P8-81c VM item 6 · session.md F6: the AllowList entries TIA writes for "Yes to all" (key and value names, date format and time
   zone, Base64 hash) compared with the Workbench check on a real machine; the first-attach timeout message while the firewall
   dialog is open.
7. P8-81c VM item 7 · session.md F10, F11: Studio with zero, one and several TIA processes; denied attach does not start a new TIA
   Portal; primary project bound when a secondary is open; disconnect with a modified project in a Studio-started portal.
8. P8-81c VM item 8 · session.md F12: a failing GenerateBlocksFromSource leaves no `TIA_Studio_<guid>` source.
9. P8-81c VM item 9 · session.md F13: SaveProjectCopy on a multiuser local session is refused (the host already refuses first).
10. P8-81c VM item 10 · session.md F14: ShowObjectInEditor with UI opens the editor; headless is refused.
11. P8-81c VM item 11 · session.md F15: the exclusive-access dialog text shown in the TIA UI.
12. P8-81c VM item 12 · session.md F17: which TIA dialogs reach the Confirmation handler (texts, choices), whether setting
    `Result`/`IsHandled` from the handler answers them, the Disposed event when TIA is closed by the user, and `GetPortalInfo`
    `data.portalDialogs`.
13. P8-81c VM item 13 · session.md F18: `TiaPortal.GetProcess(pid, timeout)` for an unknown PID and a busy TIA Portal (returns null
    or throws; time to answer).

## Workbench environment fix (P8-73b)

Item 3 of the P8-73b report (the engine worker's fix result on V20/V21) was withdrawn on 2026-10-21: since P8-04j2 the package no
longer contains the engine worker, so a tester cannot reach that path.

1. P8-73b VM item 1 · triage O1: on any of the eight releases (all use the delivered PlcWorker), a Windows user who is not in
   `Siemens TIA Openness` selects Fix on the Workbench environment page. The hidden `doctor --fix --json` gets the worker's elevated
   `net.exe` UAC prompt in front of the Workbench, not only as a taskbar button. After approval the page shows "Added to group; sign
   out and in" (`AddedPendingLogon`); after a Windows sign-out and sign-in the recheck passes.
2. P8-73b VM item 2 · triage O1: declining that UAC prompt shows a failed fix with the declined reason (`ElevationDeclined`). A
   second Fix after the add and before signing out shows the sign-out guidance (`PendingLogon`). The PlcWorker starts and runs the
   repair while the current token lacks the group.

## Other audit areas (fixes pending)

These findings are not fixed yet; each needs a real TIA Portal to confirm the native behavior the fix depends on. The source is the
audit area file and its finding id.

- hmi.md F5: the legacy Unified tag-create helper and a non-default tag table.
- hmi.md F8: Unified driver and connection property names.
- hmi.md F15: Unified collections claimed for V16–V18.
- plc.md F2: `CrossReferenceService` on a `PlcUserConstant`.
- plc.md F5: the exception type for exporting an inconsistent block per release.
- plc.md F7: a compile with warnings on one old and one new release.
- plc.md F16: `CreateFromFile` in a user group.
- plc.md F20: user-group rename where `Name` has no public setter.
- plc.md F21: `CreateInstanceDB` on V15.1/V16.
- plc.md F22: whether the XML re-import of a non-know-how-protected block keeps its number (MovePlcBlockToGroup on V20/V21).
- libraries-vci-safety.md L-1: the native outcome of `Edit()` on STEP 7 library types.
- libraries-vci-safety.md L-6: `GlobalLibrary.IsReadOnly` semantics.
- libraries-vci-safety.md V-1: absolute versus relative `ExportObject` paths.
- libraries-vci-safety.md V-5: the object kinds V18/V19 VCI can map.
- libraries-vci-safety.md F-4: attribute spellings and types.
- libraries-vci-safety.md RF-3: V21 split-assembly service lookup.
- libraries-vci-safety.md S-2: `PlcProtectionAccessLevel` reads.
- libraries-vci-safety.md S-4: the PUT/GET attribute name and type per CPU family.
- libraries-vci-safety.md S-5, S-6: certificate-assignment and SysLog attribute names.
- libraries-vci-safety.md S-9: `Project.GetService<ProtectionProviderBase>()`.
- libraries-vci-safety.md S-13: the UMC `Authentication` event.
- libraries-vci-safety.md O-R3 (Appendix B R3): legacy `GenerateSivarc` with PLC device names that differ from software names
  (V15.1–V19).
- libraries-vci-safety.md O-R4 (Appendix B R4): motor/encoder `ConfigurationEntry.Value` types.
- libraries-vci-safety.md O-R5 (Appendix B R5): whether V14 SP1 `Startdrive.DriveObject` is an `IEngineeringService`.
