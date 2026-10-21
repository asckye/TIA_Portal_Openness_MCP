# API audit VM checklist

Status: current · Source: the 2026-10-19 official-API audit, the P8-81a/P8-81b fix reports (2026-10-19/20) and the P8-73b
Workbench fix report (2026-10-21).

The audit compared the repository with the Siemens SDK XML and the official Openness documentation. The items below depend on native
TIA Portal behavior that neither the SDK XML nor the documentation states, so the offline checks cannot verify them. None of them has
been run on a real TIA Portal. Pre-release notes copy this list into their "Not verified on a real TIA Portal" section
(`generate-prerelease-notes`); see [the real-machine ledger](real-machine-ledger.md) for the other open real-machine items.

Every row names its source: the fix report and its VM item number, and the audit area file and finding id. The reports and the
audit files are kept in the maintainer checkout and are not tracked: `bin-build/refactor/P8-81a-evidence/P8-81a.result.md` and
`bin-build/refactor/P8-81b-evidence/P8-81b.result.md` ("VM checklist" sections), and `bin-build/refactor/api-audit/<area>.md`. The
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
17. P8-81b VM item 17 · hardware.md H-01 (ungrouped devices in every device walk, engine V20/V21): a PLC in the ungrouped folder (if
    TIA allows one there) is listed by GetAllPlcSoftware and resolved by name; exact software lookup finds software below ungrouped
    devices.
18. P8-81b VM item 18 · hardware.md H-03 (scaffold spec keys): `tia schema` shows the new spec lines on all eight releases.

## Workbench environment fix (P8-73b)

1. P8-73b VM item 1 · triage O1: on V14 SP1–V19, and on V20/V21 with `--plc-worker`, a Windows user who is not in
   `Siemens TIA Openness` selects Fix on the Workbench environment page. The hidden `doctor --fix --json` gets the worker's elevated
   `net.exe` UAC prompt in front of the Workbench, not only as a taskbar button. After approval the page shows "Added to group; sign
   out and in" (`AddedPendingLogon`); after a Windows sign-out and sign-in the recheck passes.
2. P8-73b VM item 2 · triage O1: declining that UAC prompt shows a failed fix with the declined reason (`ElevationDeclined`). A
   second Fix after the add and before signing out shows the sign-out guidance (`PendingLogon`). The PlcWorker starts and runs the
   repair while the current token lacks the group.
3. P8-73b VM item 3 · triage O1: on V20/V21 without `--plc-worker` (engine worker), the fix result is the engine helper's bool
   (`Unknown`), and the page shows the recheck that follows it.

## Other audit areas (fixes pending)

These findings are not fixed yet; each needs a real TIA Portal to confirm the native behavior the fix depends on. The source is the
audit area file and its finding id.

- session.md F1: disposing an isolated portal the worker started.
- session.md F2: opening or retrieving UMAC-protected projects (V17+ `Authentication` event).
- session.md F3: STA owner threads without a message pump (V14 SP1–V19 worker, Studio bridge on all eight).
- session.md F11: Studio Disconnect without ownership or project handling.
- session.md F14: ShowObjectInEditor without a UI.
- session.md F17: TIA dialog and lifetime events.
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
