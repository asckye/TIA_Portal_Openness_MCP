# V21 Unified script library rename probe

This diagnostic tests `LSicar_GeneralScripts` from the user's diagnostic global library. It does not attach to an existing TIA process, open AutomaticDipCoatingMachine, or modify the diagnostic input library. No MCP service or network token is required. It is a test package, not a deployed rename fix.

## Revision 3: second VM run reached export, but not rename

The returned `20261001-123143-91878807` campaign created its short-path project, opened the diagnostic library read-only, copied the source type, and verified both original version identities. Its first `ExportAsDocuments` call failed with `The argument 'directoryInfo' cannot be a relative path.` No Name setter or document import was attempted. Project close and TIA disposal returned; the supervisor verified the owned process had exited and existing TIA processes retained their identities. This was another probe preparation defect, not a rename result or a nonrecoverable channel fault.

On .NET Framework, the object returned by `Directory.CreateDirectory(absolutePath)` can expose an absolute `FullName` but only the leaf folder name through `ToString()`. This was reproduced locally, including the `5.1.1` export folder. Revision 3 constructs every Openness directory argument with `new DirectoryInfo(absolutePath)`, checks both representations, and records export/import directory arguments before the native call. This follows Siemens' absolute-path constructor guidance; whether it clears native export remains to be verified on the VM.

The 64 offline checks include this .NET Framework regression for version, import, and Unicode/space paths. The launcher now displays the full recorded exception and evidence paths when a stage fails, including stderr if the child fails before producing a result. Four independent supervisor simulations verified this output and the existing stop/continue behavior. No native rename case has yet run in this standalone probe.

## Revision 2: first VM run stopped before rename

The returned `20261001-122041-40b08e26` campaign successfully started a new TIA instance, then failed at `Projects.Create`: the nested Desktop extraction produced a 149-character project directory, while that V21 installation reported a 143-character maximum. No source library was opened and no rename case ran. `nativeChannelFaulted` was false; `Dispose` returned and the supervisor subsequently observed the owned process exit. This was a probe setup defect, not evidence of another Name crash.

Revision 2 creates native projects and document exchange directories automatically under **`%LOCALAPPDATA%\TRP\<case hash>`**, independent of the ZIP's extraction depth. Both possible project directories are length-checked before starting TIA. The owner marker prevents another case or an earlier run from reusing that workspace. Native project files remain there for diagnosis; `workspace-location.json` records the location. Logs, results, and copies of exported scripts still appear beside the launcher under `results`.

Windows PowerShell 5.1 now reads UTF-8 result JSON explicitly, preserving Chinese exception messages. Aggregated results also record the supervisor's final process-exit check, separate from the probe's immediate post-Dispose process sample. The second VM run confirmed project creation and these result records; its separate export failure is described above.

## Run on the TIA virtual machine

1. Extract the complete ZIP to a local directory on the VM, such as `C:\Temp\LibraryRenameProbe`.
2. Double-click **Run-Tests.cmd**. No Visual Studio or .NET SDK is required; installed TIA V21 Openness and .NET Framework 4.8 are required.
3. If the Siemens Openness firewall asks, check that the requesting program is this `LibraryRenameProbe.exe` before allowing it. Use a Windows account already configured for Openness.
4. Keep the console open. Nine cases create independent headless TIA sessions; allow roughly 10–20 minutes, depending on the VM. Each stage has a ten-minute timeout.
5. Read `results\<timestamp>\summary.json` and `cases.json`. Return that results directory for analysis. Exit code 1 means one or more cases failed, which is expected when reproducing the crash; it does not mean the evidence was lost.

The default input is the diagnostic `.al21` already created on this VM:

```
C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\library-general-1133\MCP_GeneralScripts_Rename_20261001_1133\MCP_GeneralScripts_Rename_20261001_1133.al21
```

If it has moved, start PowerShell in the extracted directory:

```powershell
.\Run-Tests.ps1 -SourceLibrary 'D:\Diagnostics\MCP_GeneralScripts_Rename_20261001_1133.al21'
```

Copy the **whole global-library directory**, not just its `.al21` file. The input must retain the diagnostic filename prefix and contain the recorded type GUID `04254662-9034-496e-a436-109c945e1de2` and both original committed versions. Arbitrary user projects are not accepted.

## Cases and evidence

| Case | Operation |
|---|---|
| control | Copy, export both versions, save, reopen, compare script hashes |
| property | Direct compiled C# `LibraryType.Name = ...` |
| attributes | Direct compiled `IEngineeringObject.SetAttributes` for Name |
| edit-property | `Edit()` then direct Name assignment |
| edit-attributes | `Edit()` then bulk attributes assignment |
| document-create-control | Export the real files and import into a new empty project |
| document-update-control | Export/import unchanged content as a new version |
| document-create-rename | Change the one module key in YAML, create and release a new type |
| document-update-rename | Change the YAML module key and import into the existing type |

Each case has a preparation process and a separate execution process. Preparation opens the diagnostic library read-only, copies the sample into a newly created project, verifies the two original version identities, exports baseline bodies, saves, and closes TIA. The execution process starts another fresh TIA instance and opens only that owned project. Name setters are compiled direct API calls, without the MCP reflection/property wrapper or its full property snapshot.

Every API boundary flushes a `BEFORE`, `RETURNED`, or `THREW` record to disk. PID/start time, exception chains, application error events, original/final exported scripts, and per-stage results are retained. Successful rename checks include readback, GUID checks, original version preservation, save/reopen, old-name absence, and script-body hashes. New versions are released only in the disposable test project. No transaction rollback is treated as crash isolation.

After `NonRecoverableException`, the probe makes no more Openness calls on that channel. The supervisor advances only after the exact owned TIA process has exited and all previously open TIA processes still have their original PID/start time. On timeout, only the exact probe child is terminated; TIA processes are never killed by this runner. An uncertain/orphaned TIA state stops the campaign for inspection. Do not delete result directories while the tests run.

`RENAME_PERSISTED` means the original type GUID was retained. `CLONE_RENAMED_PERSISTED` means a **new GUID**, with the original type left intact. It does not migrate existing references or retain the old minimum target version. `CONTROL_PASSED` is not a rename result. An API returning successfully while leaving the old type name is reported as failed rename verification.

## Current live MCP findings (2026-10-01)

- Existing MCP Name writes crashed isolated V21 TIA in a project library, a global library, and after entering InWork. The MCP process survived and was explicitly recovered.
- Native export produces `Type.def.hmi.yml` and `Type.def.hmi.js`. For the tested library import, the native filename argument is **`Type.def.hmi`**, removing only the last extension. `Type` and extra YAML aliases are not equivalent inputs.
- The unchanged document imported successfully into the existing type as a new InWork version.
- Changing the YAML module key created `MCP_GeneralScripts_DocumentRenamed`, released as 5.1.1 and retained after save/close/reopen in the isolated project. Its new GUID is `d9013b09-1684-411f-8b14-546c9a0d6ffc`; minimum target version became **21.0.0.0**, versus **20.0.0.0** on the original. The MCP metadata test did not verify a re-exported body because its current library reader unnecessarily requires an HMI device; this probe performs that body check directly.
- Importing changed-name YAML into the existing type returned success but **left the original library type name unchanged**. Its temporary InWork version was discarded.
- AutomaticDipCoatingMachine's original type and two version records matched the before-test readback. The 8765 service was reconnected to its original process without a fault.
- The independent C# matrix has been compiled and checked offline on the host. **Its native execution is pending a VM run.** Do not attribute a cause to Siemens, or call the in-place rename fixed, solely from the MCP stack.

## Build and offline verification

```powershell
dotnet build .\LibraryRenameProbe.csproj -c Release -p:SiemensEngineeringDirectory='C:\SDK\V21\net48'
.\bin\Release\net48\LibraryRenameProbe.exe --self-test
```

The compiler uses the V21 PublicAPI as references only. No Siemens SDK binaries are redistributed. Runtime resolution uses the installed TIA registry location and exact assembly identity. `--self-test` loads no Siemens assemblies and makes no native calls. `--preflight` reads installation identity only; it does not prove a valid license or firewall/group permissions.

## Official references

- [Library type attributes and rename examples](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-types)
- [Construct DirectoryInfo and FileInfo with absolute paths](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/creating-a-directoryinfo/fileinfo-object)
- [Create library type from documents](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/creating-new-type-from-document)
- [Update library version from documents](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-type-from-document)

The installed PublicAPI signatures, rather than inconsistent enum names in some documentation examples, determine the compiled calls.
