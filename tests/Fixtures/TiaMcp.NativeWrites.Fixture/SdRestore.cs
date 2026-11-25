using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Plc;
using TiaOpenness.Openness;

namespace Siemens.Engineering.SW.Tags { internal sealed class PlcTagTable { } }
namespace Siemens.Engineering.SW.Types { internal sealed class PlcType { } }

namespace Siemens.Engineering.VersionControl
{
    internal enum SynchronizationMode { WorkspaceToProject }
    internal sealed class MappedObject
    {
        internal object? EngineeringObject;
        internal string FileNameWithoutExtension = "PLC_1_Mapped_81f2e9af";
        internal string FileFormat = "s7dcl";
        internal DirectoryInfo DirectoryPath = new("sources");
        internal Action OnSync = () => { };
        internal int Calls;
    }
}

namespace TiaOpenness.Openness
{
    internal static class VersionControlPrimitives
    {
        internal static string Format(MappedObject mapped) => mapped.FileFormat;
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
            var startup = scenario is "sd-restore-preview" or "sd-restore-refused";
            PlcBlock block = startup ? new OB { Name = "MCP_RC_OBStart", Number = 151, SecondaryType = "Startup" }
                : new GlobalDB { Name = "MCP_RC_DBAttr", Number = 9810, ProgrammingLanguage = "DB" };
            block.Parent = group;
            group.Blocks.Add(block.Name, block);
            var mapping = new MappedObject { EngineeringObject = block };
            string folder = Directory.CreateDirectory(Path.Combine(directory, "sources")).FullName;
            string path = Path.Combine(folder, mapping.FileNameWithoutExtension + ".s7dcl");
            string kind = startup ? "ORGANIZATION_BLOCK" : "DATA_BLOCK";
            string declared = scenario == "sd-restore-mismatch" ? mapping.FileNameWithoutExtension : block.Name;
            string declaration = scenario == "sd-restore-nonblock" ? "TYPE\n    UDT_Status : STRUCT\n        Ready : Bool;\n    END_STRUCT;\nEND_TYPE\n"
                : $"{kind} \"{declared}\"\nVAR\n    Value : Int;\nEND_VAR\nEND_{kind}\n";
            File.WriteAllText(path, declaration);
            if (scenario == "sd-restore-absolute") mapping.DirectoryPath = new DirectoryInfo(folder);
            if (scenario == "sd-restore-xml") mapping.FileFormat = "xml";
            if (scenario == "sd-restore-format") mapping.FileFormat = "";
            if (scenario == "sd-restore-missing") mapping.FileNameWithoutExtension = "Missing";
            if (scenario == "sd-restore-target") mapping.EngineeringObject = null;
            if (scenario == "sd-restore-nonblock") mapping.EngineeringObject = new Siemens.Engineering.SW.Types.PlcType();
            if (scenario == "sd-restore-aggregate") mapping.EngineeringObject = group;
            if (scenario == "sd-restore-db") block.Attributes["DBAccessibleFromOPCUA"] = false;
            if (scenario == "sd-restore-changed") block.AfterExport = () => File.AppendAllText(path, "// changed");
            if (scenario == "sd-restore-opaque") block.Attributes["VendorValue"] = new object();
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
                else if (scenario == "sd-restore-failure") block.Attributes["DBAccessibleFromOPCUA"] = true;
                else block.Number = 1;
            };
            var item = OpennessVersionControl.RestoreSdMapping(mapping, directory, mapping.FileNameWithoutExtension,
                scenario is "sd-restore-preview" or "sd-restore-plan");
            return new { item, mapping.Calls, block.Number, block.Exports, block.Setters };
        }
    }
}
