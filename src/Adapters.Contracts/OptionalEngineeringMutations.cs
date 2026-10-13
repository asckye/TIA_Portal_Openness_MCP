using System;

namespace TiaMcp.Adapters.Contracts;

public static class OptionalEngineeringMutations
{
    public static bool Writes(OptionalEngineeringRequest request)
    {
        if (request.Operation is "GetSivarcRuleTree" or "ListSivarcBlockDefinitions" or "ListSivarcRules" or "ResolveSivarcExpression"
            or "GetDccObject" or "GetDriveParameters" or "GetOnlineDriveParameters" or "ListDccCharts" or "ListDriveObjects") return false;
        if (request.Operation == "ManageOnlineDriveFunctions") return request.Action != "read";
        if (request.DryRun) return false;
        if (request.Operation is "GenerateSivarc" or "UpgradeSivarcDefinitions") return true;
        return request.Action is not "read" and not "readSequence" and not "check" and not "checkValidity" and not "checkTraceValidity"
            and not "readPackages" and not "readMotorConfiguration" and not "readEncoderConfiguration";
    }

    public static bool Writes(SafetyValidationRequest request)
    {
        if (request.Operation == "ListSafetyActivationTests" || request.DryRun) return false;
        return request.Action is not "read" and not "checkValidity" and not "checkTraceValidity";
    }

    public static bool IsPortalScopedExtensionAction(string action)
        => action is "readPackages" or "install" or "installAndGetIdentifier" or "uninstall";

    public static bool PortalScope(string operation, string action)
        => operation == "ManageSafetyGlobalSettings" || operation == "ManageTechnologyExtensions"
            && IsPortalScopedExtensionAction(action);

    public static void ValidateWrite(string module, string operation, string action, bool confirm, string expectedProject,
        Func<(bool IsAttached, string? ProjectFile)> portalBinding, Action<string> refuse, Action validateProjectTarget)
    {
        if (module is not "plc-safety.ExecuteSafety" and not "startdrive.ExecuteStartdrive" || !PortalScope(operation, action))
        { validateProjectTarget(); return; }
        if (!confirm) { refuse("confirm"); return; }
        var state = portalBinding();
        if (!state.IsAttached || !string.Equals(expectedProject, state.ProjectFile ?? "", StringComparison.OrdinalIgnoreCase))
            refuse("expectedProjectFile");
    }
}
