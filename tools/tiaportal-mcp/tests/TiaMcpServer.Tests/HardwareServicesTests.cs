using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.Tests
{
    internal static class HardwareServicesTests
    {
        internal static void Run(Action<bool,string> check)
        {
            bool Fails<TException>(Action action) where TException : Exception { try { action(); return false; } catch (TException) { return true; } catch { return false; } }

            // Connection kinds: exact case-sensitive names from the V21 XML, mapped into the official namespace.
            check(HardwareServicesLogic.ConnectionKinds.Length == 8, "eight concrete connection kinds");
            check(HardwareServicesLogic.ConnectionTypeName("S7Connection") == "Siemens.Engineering.HW.CommunicationConnections.S7Connection", "connection kind maps to official type name");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ConnectionTypeName("s7connection")), "connection kind is case-sensitive");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ConnectionTypeName("Connection")), "abstract Connection base is not creatable");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ConnectionTypeName("")), "empty connection kind refused");

            // Generic exact-choice validation
            check(HardwareServicesLogic.RequireOneOf("read", new[] { "read", "assign" }, "action") == "read", "valid action passes (negative sentinel)");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireOneOf("Read", new[] { "read", "assign" }, "action")), "action names are case-sensitive");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireOneOf(null!, new[] { "read" }, "action")), "null action refused");

            // Exact names
            check(HardwareServicesLogic.RequireExactName("S7_Connection_1", "connectionName") == "S7_Connection_1", "exact name passes");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireExactName(" name", "connectionName")), "leading whitespace refused");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireExactName("", "connectionName")), "empty name refused");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireExactName(new string('x', 257), "connectionName")), "over-long name refused");

            // Pagination
            HardwareServicesLogic.ValidatePagination(0, 500);
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ValidatePagination(-1, 10)), "negative offset refused");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ValidatePagination(0, 501)), "limit above 500 refused");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.ValidatePagination(0, 0)), "zero limit refused");
            var page = HardwareServicesLogic.PageMeta(12, 5, 5, 5);
            check(page["expectedCount"]!.GetValue<int>() == 12 && page["actualCount"]!.GetValue<int>() == 5 && page["nextOffset"]!.GetValue<int>() == 10 && page["truncated"]!.GetValue<bool>(), "page meta reports next offset and truncation");
            var last = HardwareServicesLogic.PageMeta(12, 10, 5, 2);
            check(last["nextOffset"] == null && !last["truncated"]!.GetValue<bool>(), "final page has no next offset");
            var empty = HardwareServicesLogic.PageMeta(0, 0, 100, 0);
            check(empty["nextOffset"] == null && !empty["truncated"]!.GetValue<bool>() && empty["expectedCount"]!.GetValue<int>() == 0, "empty collection is complete, not truncated");

            // Confirmation gates: preview never needs them, real execution always does.
            HardwareServicesLogic.RequireConfirmation(false, "confirmDelete", true);
            HardwareServicesLogic.RequireConfirmation(true, "confirmDelete", false);
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireConfirmation(false, "confirmDelete", false)), "real execution without confirmation refused");

            // Input files
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireExistingInputFile("relative.aml", "filePath")), "relative input path refused");
            check(Fails<FileNotFoundException>(() => HardwareServicesLogic.RequireExistingInputFile(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".aml"), "filePath")), "missing input file refused");
            var temp = Path.Combine(Path.GetTempPath(), "hw-services-" + Guid.NewGuid().ToString("N") + ".aml");
            try
            {
                File.WriteAllText(temp, "");
                check(Fails<FileNotFoundException>(() => HardwareServicesLogic.RequireExistingInputFile(temp, "filePath")), "empty input file refused");
                File.WriteAllText(temp, "<CAEXFile/>");
                check(HardwareServicesLogic.RequireExistingInputFile(temp, "filePath").Length > 0, "existing nonempty input file passes");
            }
            finally { File.Delete(temp); }

            // Catalogs from the official XML
            check(HardwareServicesLogic.FeatureCatalog.Length == 45 && HardwareServicesLogic.FeatureCatalog.Distinct(StringComparer.Ordinal).Count() == 45, "feature catalog lists 45 distinct HW.Features types");
            check(HardwareServicesLogic.FeatureTypeName("NetworkInterface") == "Siemens.Engineering.HW.Features.NetworkInterface", "feature type name in official namespace");
            check(!HardwareServicesLogic.FeatureValuesAllowed("PlcMasterSecretConfigurator") && !HardwareServicesLogic.FeatureValuesAllowed("WebserverUserManagement"), "credential-related features never dump values");
            check(HardwareServicesLogic.FeatureValuesAllowed("NetworkInterface"), "ordinary feature values are readable (negative sentinel)");
            check(HardwareServicesLogic.FeatureValueDenylist.All(d => HardwareServicesLogic.FeatureCatalog.Contains(d, StringComparer.Ordinal)), "denylist entries exist in the catalog");
            check(HardwareServicesLogic.OpcUaPermissionNames.SequenceEqual(new[] { "Browse", "Read", "Write", "Call", "ReceiveEvents", "ReadRolePermissions" }), "OPC UA permission names match NamespacePermission properties");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireOneOf("Execute", HardwareServicesLogic.OpcUaPermissionNames, "permission")), "unknown OPC UA permission refused");
            check(HardwareServicesLogic.OpcUaActions.Length == 6 && HardwareServicesLogic.OpcUaActions.Contains("deleteRole"), "OPC UA action catalog");
            check(HardwareServicesLogic.CaxImportOptions.SequenceEqual(new[] { "MoveToParkingLot", "OverwriteTiaDevice", "RetainTiaDevice" }), "CaxImportOptions enum names");
            check(Fails<ArgumentException>(() => HardwareServicesLogic.RequireOneOf("Overwrite", HardwareServicesLogic.CaxImportOptions, "importOption")), "partial import option name refused");
            check(HardwareServicesLogic.TableAccessValues.SequenceEqual(new[] { "None", "Read", "Write" }), "WatchAndForceTableAccess enum names");
        }
    }
}
