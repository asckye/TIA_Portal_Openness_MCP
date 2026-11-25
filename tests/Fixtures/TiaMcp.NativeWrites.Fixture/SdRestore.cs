using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Plc;
using TiaOpenness.Openness;

namespace Siemens.Engineering.SW.Tags { internal sealed class PlcTagTable { } internal class PlcTagTableGroup { } }
namespace Siemens.Engineering.SW.Types { internal sealed class PlcType { } internal class PlcTypeGroup { } }
namespace Siemens.Engineering.SW { internal sealed class PlcSoftware { } }
namespace Siemens.Engineering.SW.Units { internal sealed class PlcUnit { } }
namespace Siemens.Engineering.HW { internal sealed class Device { } }
namespace Siemens.Engineering.Hmi { internal sealed class HmiTarget { } }
namespace Siemens.Engineering.HmiUnified.UI { internal sealed class HmiScreen { } }

namespace Siemens.Engineering.VersionControl
{
    internal enum SynchronizationMode { WorkspaceToProject }
    internal enum CompareState { Equal, Unequal, Unknown, WorkspaceFileMissing }
    internal sealed class IndividualObjectCompareResult { internal CompareState CompareState; }
    internal sealed class MappedObject
    {
        internal object? EngineeringObject;
        internal string FileNameWithoutExtension = "PLC_1_Mapped_81f2e9af";
        internal string FileFormat = "s7dcl";
        internal DirectoryInfo DirectoryPath = new("sources");
        internal Action OnSync = () => { };
        internal Func<CompareState> Status = () => CompareState.Equal;
        internal Exception? FormatError;
        internal int Calls, FormatReads, StatusReads;
        internal IndividualObjectCompareResult GetStatus()
        {
            StatusReads++;
            return new IndividualObjectCompareResult { CompareState = Status() };
        }
    }
}

namespace TiaOpenness.Openness
{
    internal static class VersionControlPrimitives
    {
        internal static string Format(MappedObject mapped)
        {
            mapped.FormatReads++;
            if (mapped.FormatError != null) throw mapped.FormatError;
            return mapped.FileFormat;
        }
        internal static IndividualObjectCompareResult ReadStatus(MappedObject mapped) => mapped.GetStatus();
        internal static CompareState State(IndividualObjectCompareResult result) => result.CompareState;
        internal static string FileName(MappedObject mapped) => mapped.FileNameWithoutExtension;
        internal static DirectoryInfo Directory(MappedObject mapped) => mapped.DirectoryPath;
        internal static void Synchronize(MappedObject mapped, SynchronizationMode mode)
        {
            if (mode != SynchronizationMode.WorkspaceToProject)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Expected a restore to the project.");
            mapped.Calls++;
            mapped.OnSync();
        }
    }
}

namespace TiaMcp.Tests.NativeWrites
{
    internal static class SdRestore
    {
        private sealed class UnknownContainer { public PlcBlockGroup Blocks { get; } = new(); }

        private static object Discover(string scenario)
        {
            Exception fault = scenario switch
            {
                "sd-restore-discovery-disposed" => new ObjectDisposedException(nameof(MappedObject)),
                "sd-restore-discovery-qualified" => new ObjectDisposedException(typeof(MappedObject).FullName),
                "sd-restore-discovery-session" => new ObjectDisposedException("TiaPortal"),
                _ => new EndOfStreamException("Session disconnected")
            };
            var first = new MappedObject { Status = () => throw fault };
            var second = new MappedObject { Status = () => CompareState.Unequal };
            var states = new List<string>();
            var errors = new List<string?>();
            string? stopped = null;
            try
            {
                foreach (var row in new[] { first, second })
                {
                    states.Add(OpennessVersionControl.ReadMappedState(row, out var error).ToString());
                    errors.Add(error);
                }
            }
            catch (Exception error) when (TiaMcp.Adapters.Contracts.NativeRefusalProbe.UncertainConnection(error)) { stopped = error.GetType().Name; }
            return new { states, errors, stopped, firstReads = first.StatusReads, secondReads = second.StatusReads };
        }

        internal static object Run(string scenario, string directory)
        {
            if (scenario.StartsWith("sd-restore-discovery-", StringComparison.Ordinal)) return Discover(scenario);
            PlcBatchImportRecovery.Root = directory;
            var group = new PlcBlockGroup();
            const string startupPrefix = "sd-restore-startup-";
            const string missingPrefix = "sd-restore-missing-";
            var startup = scenario.StartsWith(startupPrefix, StringComparison.Ordinal) || scenario is "sd-restore-preview" or "sd-restore-refused";
            PlcBlock block = startup ? new OB { Name = "MCP_RC_OBStart", Number = 151, SecondaryType = "Startup" }
                : new GlobalDB { Name = "MCP_RC_DBAttr", Number = 9810, ProgrammingLanguage = "DB" };
            if (scenario == startupPrefix + "SimaticML") block.Name = "Main";
            if (scenario == "sd-restore-liblink") block.Name = "MCP_B6F_DSrc";
            block.Parent = group;
            group.Blocks.Add(block.Name, block);
            var mapping = new MappedObject { EngineeringObject = block };
            // V20 raw-D/314 and V21 raw-E/328 and 339 record these stems and exact format names.
            if (block.Name == "Main") mapping.FileNameWithoutExtension = "MCP_B5_PLC_Main";
            if (block.Name == "MCP_B6F_DSrc") mapping.FileNameWithoutExtension = "MCP_B6F_PLC_MCP_B6F_DSrc";
            if (scenario.StartsWith(startupPrefix, StringComparison.Ordinal)) mapping.FileFormat = scenario.Substring(startupPrefix.Length);
            if (scenario.StartsWith(missingPrefix, StringComparison.Ordinal)) mapping.FileFormat = scenario.Substring(missingPrefix.Length);
            string folder = Directory.CreateDirectory(Path.Combine(directory, "sources")).FullName;
            string path = Path.Combine(folder, mapping.FileNameWithoutExtension + ".s7dcl");
            string kind = startup ? "ORGANIZATION_BLOCK" : "DATA_BLOCK";
            string declared = scenario == "sd-restore-mismatch" ? mapping.FileNameWithoutExtension : block.Name;
            string declaration = scenario == "sd-restore-nonblock" ? "TYPE\n    UDT_Status : STRUCT\n        Ready : Bool;\n    END_STRUCT;\nEND_TYPE\n"
                : $"{kind} \"{declared}\"\nVAR\n    Value : Int;\nEND_VAR\nEND_{kind}\n";
            File.WriteAllText(path, declaration);
            if (scenario == "sd-restore-absolute") mapping.DirectoryPath = new DirectoryInfo(folder);
            if (scenario == "sd-restore-xml") mapping.FileFormat = "xml";
            if (scenario == "sd-restore-unreadable-format") mapping.FormatError = new InvalidOperationException("Mapped file format unreadable");
            if (scenario == "sd-restore-format") mapping.FileFormat = "";
            if (scenario == "sd-restore-missing" || scenario.StartsWith(missingPrefix, StringComparison.Ordinal)) mapping.FileNameWithoutExtension = "Missing";
            if (scenario == "sd-restore-target") mapping.EngineeringObject = null;
            if (scenario == "sd-restore-orphan") block.Parent = null;
            if (scenario == "sd-restore-nonblock")
            {
                mapping.EngineeringObject = new Siemens.Engineering.SW.Types.PlcType();
                mapping.FileFormat = "UnrecognizedFormat";
            }
            if (scenario.StartsWith("sd-restore-aggregate", StringComparison.Ordinal))
            {
                mapping.EngineeringObject = group;
                if (scenario == "sd-restore-aggregate-known") mapping.FileFormat = "SimaticML";
                if (scenario == "sd-restore-aggregate-near-match") mapping.FileFormat = "SimaticML2";
            }
            if (scenario == "sd-restore-software") mapping.EngineeringObject = new Siemens.Engineering.SW.PlcSoftware();
            if (scenario == "sd-restore-unit") mapping.EngineeringObject = new Siemens.Engineering.SW.Units.PlcUnit();
            if (scenario == "sd-restore-device") mapping.EngineeringObject = new Siemens.Engineering.HW.Device();
            if (scenario == "sd-restore-device-item") mapping.EngineeringObject = new Siemens.Engineering.HW.DeviceItem();
            if (scenario == "sd-restore-container") { mapping.EngineeringObject = new UnknownContainer(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-type-group") { mapping.EngineeringObject = new Siemens.Engineering.SW.Types.PlcTypeGroup(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-tag-group") { mapping.EngineeringObject = new Siemens.Engineering.SW.Tags.PlcTagTableGroup(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-tags") { mapping.EngineeringObject = new Siemens.Engineering.SW.Tags.PlcTagTable(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-hmi") { mapping.EngineeringObject = new Siemens.Engineering.Hmi.HmiTarget(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-hmi-unified")
            {
                mapping.EngineeringObject = new Siemens.Engineering.HmiUnified.UI.HmiScreen();
                mapping.FileFormat = "";
            }
            if (scenario == "sd-restore-liblink") mapping.FileFormat = "liblink";
            if (scenario == "sd-restore-format-loss") mapping.FormatError = new ObjectDisposedException("TiaPortal");
            if (scenario == "sd-restore-format-disposed") mapping.FormatError = new ObjectDisposedException(nameof(MappedObject));
            if (scenario == "sd-restore-backup-loss") block.AfterExport = () => throw new InvalidOperationException("Export lost", new EndOfStreamException());
            if (scenario == "sd-restore-db") block.Attributes["DBAccessibleFromOPCUA"] = false;
            if (scenario == "sd-restore-changed") block.AfterExport = () => File.AppendAllText(path, "// changed");
            if (scenario is "sd-restore-opaque" or "sd-restore-to-null") block.Attributes["VendorValue"] = new object();
            if (scenario == "sd-restore-from-null") block.Attributes["VendorValue"] = null;
            if (scenario == "sd-restore-elsewhere")
            {
                var sibling = new PlcBlockGroup { Parent = group };
                group.Groups.Add(sibling);
                sibling.Blocks.Add(block.Name, new GlobalDB { Name = block.Name, Parent = sibling });
            }
            const string statusPrefix = "sd-restore-status-";
            var statusCase = scenario.StartsWith(statusPrefix, StringComparison.Ordinal)
                ? scenario.Substring(statusPrefix.Length).Split('-') : Array.Empty<string>();
            string? properties = statusCase.ElementAtOrDefault(1);
            mapping.OnSync = () =>
            {
                using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (scenario == "sd-restore-unknown" || properties == "unverified") { block.Unreadable = true; block.Attributes["MemoryReserve"] = 100; }
                else if (scenario == "sd-restore-failure" || properties == "difference") block.Attributes["DBAccessibleFromOPCUA"] = true;
                else if (scenario == "sd-restore-to-null") block.Attributes["VendorValue"] = null;
                else if (scenario == "sd-restore-from-null") block.Attributes["VendorValue"] = new object();
                else block.Number = 1;
            };
            int? numberAtStatus = null;
            mapping.Status = () =>
            {
                numberAtStatus = block.Number;
                return statusCase.FirstOrDefault() switch
                {
                    "unequal" => CompareState.Unequal,
                    "missing" => CompareState.WorkspaceFileMissing,
                    "unknown" => CompareState.Unknown,
                    "unreadable" => throw new IOException("GetStatus unavailable"),
                    _ => CompareState.Equal
                };
            };
            var item = OpennessVersionControl.RestoreSdMapping(mapping, directory, mapping.FileNameWithoutExtension,
                scenario is "sd-restore-preview" or "sd-restore-plan" or "sd-restore-aggregate-preview", out bool connectionLost);
            return new { item, mapping.Calls, mapping.FormatReads, mapping.StatusReads, numberAtStatus, connectionLost,
                block.Number, block.Exports, block.Setters };
        }
    }
}
