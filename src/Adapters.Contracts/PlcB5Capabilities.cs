using System;
namespace TiaMcp.Adapters.Contracts
{
    public static class PlcB5Capabilities
    {
        public static string? Unsupported(string release, PlcB5Request request)
        {
            bool AtLeast(string first) => EngineeringServicesCapabilities.AtLeast(release, first);
            var tool = request.Operation;
            if (!PortedFamilies.Available(release, tool)) return tool + " has no available native action on TIA " + release + ".";
            if (request.UnitName.Length != 0 && !AtLeast("16")) return "PlcUnitProvider requires V16.";
            if (request.UnitName.Length != 0 && request.UnitKind == "safety" && !AtLeast("18")) return "PlcSafetyUnit requires V18.";
            if ((tool == "ManageOpcUaInterface" || tool == "SetOpcUaInterfaceEnabled" || tool == "ExportOpcUaInterface" || tool == "ImportOpcUaInterface")
                && (request.InterfaceType == "SimaticInterface" || request.InterfaceType == "ReferenceNamespace") && !AtLeast("16"))
                return "SimaticInterfaces and ReferenceNamespaces require V16.";
            if ((tool == "GetOpcUaAccessControl" || tool == "ManageOpcUaAccessControl") && !AtLeast("21"))
                return "ServerInterfaceGroup.AccessControl requires V21.";
            if (tool == "ManagePlcDocuments" && request.ObjectKind == "document" && request.Action.StartsWith("createFrom", StringComparison.Ordinal) && !AtLeast("21"))
                return "PlcDocumentComposition.CreateFrom requires V21.";
            if (tool == "ManagePlcAlarmTextList" && request.Action == "createFromMasterCopy" && request.CopyMode.Length != 0 && !AtLeast("20"))
                return "PlcAlarmUserTextlistComposition.CreateFrom(MasterCopy, MasterCopyMode) requires V20.";
            return null;
        }
    }
}
