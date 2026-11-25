using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Vci;
using TiaOpenness.Openness;

namespace Siemens.Engineering.SW.Tags { internal sealed class PlcTagTable { } }
namespace Siemens.Engineering.SW.Types { internal sealed class PlcType { } }
namespace Siemens.Engineering.Hmi { internal sealed class HmiTarget { } }

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
        internal static object Run(string scenario, string directory)
        {
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
            if (scenario == "sd-restore-aggregate") mapping.EngineeringObject = group;
            if (scenario == "sd-restore-tags") { mapping.EngineeringObject = new Siemens.Engineering.SW.Tags.PlcTagTable(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-hmi") { mapping.EngineeringObject = new Siemens.Engineering.Hmi.HmiTarget(); mapping.FileFormat = ""; }
            if (scenario == "sd-restore-liblink") mapping.FileFormat = "liblink";
            if (scenario == "sd-restore-format-loss") mapping.FormatError = new ObjectDisposedException("MappedObject");
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
            mapping.OnSync = () =>
            {
                using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (scenario == "sd-restore-unknown") { block.Unreadable = true; block.Attributes["MemoryReserve"] = 100; }
                else if (scenario is "sd-restore-failure" or "sd-restore-status-unreadable-difference") block.Attributes["DBAccessibleFromOPCUA"] = true;
                else if (scenario == "sd-restore-to-null") block.Attributes["VendorValue"] = null;
                else if (scenario == "sd-restore-from-null") block.Attributes["VendorValue"] = new object();
                else block.Number = 1;
            };
            int? numberAtStatus = null;
            mapping.Status = () =>
            {
                numberAtStatus = block.Number;
                return scenario switch
                {
                    "sd-restore-status-unequal" => CompareState.Unequal,
                    "sd-restore-status-missing" => CompareState.WorkspaceFileMissing,
                    "sd-restore-status-unknown" => CompareState.Unknown,
                    "sd-restore-status-unreadable" or "sd-restore-status-unreadable-difference" => throw new IOException("GetStatus unavailable"),
                    _ => CompareState.Equal
                };
            };
            var item = OpennessVersionControl.RestoreSdMapping(mapping, directory, mapping.FileNameWithoutExtension,
                scenario is "sd-restore-preview" or "sd-restore-plan", out bool connectionLost);
            return new { item, mapping.Calls, mapping.FormatReads, mapping.StatusReads, numberAtStatus, connectionLost,
                block.Number, block.Exports, block.Setters };
        }
    }
}
