using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public static class OptionalProductCapabilities
{
    public static string? Unsupported(string release, string tool, string category = "", string kind = "", string action = "",
        string executionMode = "", string password = "")
    {
        if (!PortedFamilies.Contains(tool) || PortedFamilies.ForTool(tool).Name is not ("F32" or "F34")) return null;
        if (!PortedFamilies.Available(release, tool)) return tool + " has no API on this release.";
        if (release == "17" && category == "system") return "System Test requires V18 or later.";
        if (tool == "ManageTestSuiteCase" && category == "system" && action == "showInEditor")
            return "SystemTestCase has no ShowInEditor member.";
        if (tool == "ExchangeCfcCharts" && release == "18" && action == "exportInstructionData") return "ExportInstructionData requires V19 or later.";
        if (tool.Contains("TestSuite") && new[] { "17", "18", "19" }.Contains(release) && (kind == "testSet" || action == "importTestSets"))
            return "Application test sets require V20 or later.";
        if (tool == "ManageTestSuiteCase" && release == "18" && category == "system" && action == "createFromMasterCopy")
            return "SystemTestCaseComposition.CreateFrom requires V19 or later.";
        if (tool == "ManageTestSuiteCase" && new[] { "17", "18" }.Contains(release) && action == "setScope"
            && (category == "system" || !string.IsNullOrEmpty(executionMode))) return "The selected scope overload requires V19 or later.";
        if (tool == "ExchangeTestSuiteCase" && new[] { "18", "19" }.Contains(release) && category == "system" && action == "delete")
            return "SystemTestCase.Delete requires V20 or later.";
        if (tool == "ManageSinumerikArchive")
        {
            if (release == "17" && action == "fAddressArchive") return "CreateFAddressAssignmentArchive is absent on V17.";
            if (!string.IsNullOrEmpty(password) && (new[] { "16", "17", "18" }.Contains(release) || release == "19" && action == "retrieve"))
                return "The requested password overload is unavailable on this release.";
        }
        return null;
    }
}
