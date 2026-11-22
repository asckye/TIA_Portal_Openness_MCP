using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcB4Capabilities
    {
        private const string InterpreterMappings = "InterpreterMappings, IdentTechnologicalObjectProvider and SuperimposingAxes require V21.";

        public static string? UnsupportedOperation(int version, string tool, string action)
        {
            if (tool == "ManagePlcTableEntries" && version < OpennessApi.WatchForceEntries.FirstMajor && action != "createComment" && action != "createTable")
                return "Typed watch/force entry evidence requires V17; only createTable and createComment are available on this release.";
            if (tool == "ManageMotionAxis" && version < OpennessApi.MotionAxisMappings.FirstMajor && (action.EndsWith("Mapping", StringComparison.Ordinal)
                || action == "connectIdent"))
                return InterpreterMappings;
            return null;
        }

        public static string? Unsupported(string release, PlcSoftwareRequest request)
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release, System.Globalization.CultureInfo.InvariantCulture);
            // Guard a real V21 crash, not an API difference. Extend these pairs only after native verification.
            if (version == 21 && request.Operation == "ManageTechnologyObject" && request.Action == "create"
                && !((request.TypeIdentifier == "TO_PositioningAxis" || request.TypeIdentifier == "TO_SpeedAxis") && request.Version == "5.0")
                && !(request.TypeIdentifier == "PID_Compact" && request.Version == "2.3"))
                return "V21 crash protection (not an API difference): create accepts only verified pairs TO_PositioningAxis 5.0, " +
                    "TO_SpeedAxis 5.0 or PID_Compact 2.3. Other pairs are refused because a real V21 crash lost unsaved work; " +
                    "verified pairs can be added after native verification.";
            if (request.Operation == "ManagePlcSoftwareUnit" && version < 18 && request.Properties.ContainsKey("NamespacePreset"))
                return "PlcUnitBase.NamespacePreset requires V18.";
            if (UnsupportedOperation(version, request.Operation, request.Action) is string reason) return reason;
            if (request.Operation == "ManageMotionAxis")
            {
                if (version < OpennessApi.MotionAxisMappings.FirstMajor && request.Aspect == "superimposingSetPoint") return InterpreterMappings;
                if (version < 16 && (request.Aspect.StartsWith("conveyor", StringComparison.Ordinal) || request.Aspect == "synchronousDelayed"))
                    return "ConveyorTrackingLeadingValues and DelayedCoupling require V16.";
                if (version < 15 && request.Aspect == "torque") return "TorqueHardwareConnectionInterface requires V15.1.";
            }
            if (request.Operation == "ConfigureMotionHardwareConnection" && version < 15 && request.Aspect == "torque")
                return "TorqueHardwareConnectionInterface requires V15.1.";
            if (version < OpennessApi.TechnologyGroups.FirstMajor && (request.Operation == "ManageTechnologyObject" || request.Operation == "ManageMotionAxis"
                || request.Operation == "GetMotionAxisConfiguration" || request.Operation == "ConfigureMotionHardwareConnection" || request
                    .Operation == "ExchangeMotionCamData")
                && request.Path.Replace('\\', '/').Contains("/"))
                return "Technology-object user groups require V19; use a root object on this release.";
            if (version < OpennessApi.TechnologyGroups.FirstMajor && (request.Operation == "GetTechnologyObjectTree"
                || request.Operation == "ImportTechnologyObject"
                || request.Operation == "ImportTechnologyObjectsFromDirectory") && request.GroupPath.Length != 0)
                return "Technology-object user groups require V19; only the root group is available on this release.";
            return null;
        }
    }
}
