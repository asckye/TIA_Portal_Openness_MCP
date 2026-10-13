using System;
using System.Runtime.CompilerServices;
using TiaMcp.Adapters.Native.Optional;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Safety;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    public HardwareAddressingReply ExecuteSafety(SafetyRequest request, bool dryRun = true)
        => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.Safety", () => ExecuteSafetyCore(request, dryRun));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HardwareAddressingReply ExecuteSafetyCore(SafetyRequest request, bool dryRun)
    {
        Check();
        bool writing = !request.DryRun && request.Action != "read";
        if (writing == dryRun) throw new ArgumentException("Conflicting Safety preview mode.");
#if SAFETY_GLOBAL
#if !SAFETY_V21
        if (request.Action == "generateBaseId") throw new NotSupportedException("SafetyBaseIdProvider requires V21.");
#endif
#if !SAFETY_GLOBAL_FULL
        if (request.Action is "createRuntimeGroup" or "deleteRuntimeGroup" or "generateGlobalFIOStatusBlock"
            || request.Operation == "ManageSafetyGlobalSettings" && request.Action == "update" && request.Scalars.Keys.Any(k => k is "GenerationOfDefaultFailsafeProgram" or "ManagementOfFailsafeInSoftwareUnitsEnvironment"))
            throw new NotSupportedException("The requested Safety action requires V18 or later.");
#endif
#if !SAFETY_GLOBAL_READ
        if (request.Action == "read") throw new NotSupportedException("GlobalSettings getters require V17 or later.");
#endif
        var adapter = new SafetyManagementAdapter(new SafetySession(this, request));
        switch (request.Operation)
        {
            case "ManageSafetyGlobalSettings": return adapter.ManageSafetyGlobalSettings(request.Action, "{}", request.DryRun);
#if SAFETY_PROGRAM
            case "ManagePlcSafety": return adapter.ManagePlcSafety(request.SoftwarePath, request.Action, request.RuntimeGroup, "{}", request.DryRun,
                request.Password, request.ConfirmSafetyChange, request.MainSafetyBlockPath, request.MainSafetyInstanceDbPath);
            case "GetSafetyBlockSignatures": return adapter.ReadSafetyBlockSignatures(request.SoftwarePath, request.BlockPath, request.IncludeProgramSignatures, request.Offset, request.Limit);
            case "ExportSafetyPrintout": return adapter.ExportSafetyPrintout(request.SoftwarePath, request.FilePath, request.Printer, request.Option, request.DocumentLayout, request.DryRun);
#endif
        }
#endif
        throw new NotSupportedException("The requested Safety operation is unavailable on this release.");
    }
    public HardwareAddressingReply ExecuteSafetyValidation(SafetyValidationRequest request, bool dryRun = true)
        => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.SafetyValidation", () => ExecuteSafetyValidationCore(request, dryRun));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HardwareAddressingReply ExecuteSafetyValidationCore(SafetyValidationRequest request, bool dryRun)
    {
        Check();
        if ((request.Plan.Writes || request.Plan.WritesFile) != OptionalEngineeringMutations.Writes(request) || (request.Plan.Writes || request.Plan.WritesFile) == dryRun) throw new ArgumentException("Conflicting Safety Validation preview mode.");
#if SAFETY_V21
        var adapter = new SafetyValidationAdapter(new SafetyValidationSession(this, request));
        switch (request.Operation)
        {
            case "ListSafetyActivationTests": return adapter.ReadSafetyActivationTests("[]", request.Name, request.IncludeSafetyFunctions, request.IncludeConditions, request.Offset, request.Limit);
            case "ManageSafetyActivationTest": return adapter.ManageSafetyActivationTest(request.Name, request.Action, "[]", request.EvaluationDeviceName, request.SourceName, request.MasterCopyPath, request.LibraryName, request.NewValue, request.FilePath, request.ExportOptions, request.DocumentInfoOptions, request.ImportOptions, request.ConfirmDelete, request.DryRun);
            case "ManageSafetyActivationTestGroup": return adapter.ManageSafetyActivationTestGroup(request.Action, "[]", request.Name, request.MasterCopyPath, request.LibraryName, request.NewName, request.ConfirmDelete, request.DryRun);
            case "ManageSafetyFunction": return adapter.ManageSafetyFunction(request.ActivationTest, request.Action, "[]", request.Name, request.SourceName, "{}", request.FilePath, request.ExportOptions, request.DocumentInfoOptions, request.ImportOptions, request.ConfirmDelete, request.DryRun);
            case "ManageSafetyFunctionCondition": return adapter.ManageSafetyFunctionCondition(request.ActivationTest, request.SafetyFunction, request.Action, "[]", request.Index, request.DeviceName, request.SignalUsage, request.SignalName, "{}", request.DryRun);
        }
#endif
        throw new NotSupportedException("Safety Validation Assistant requires V21.");
    }
}
