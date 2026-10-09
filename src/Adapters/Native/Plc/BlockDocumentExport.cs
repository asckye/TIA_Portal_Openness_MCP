using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<object, string>? EngineeringBlockGroupPath { get; set; }
        public Action<string?>? EngineeringRecordExportPath { get; set; }

        // Retains the original ExportBlockToTemp/ExportBlock sequence. Engineering
        // hooks only resolve the existing session; export belongs to this adapter.
        public BlockDocumentExportReply ExportBlockDocument(string softwarePath, string blockPath)
        {
            Check();
            var session = EngineeringPlcOrganisationSession ?? new OrganisationSession(this);
            if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("softwarePath is required for block-path mode.");
            if (string.IsNullOrWhiteSpace(blockPath)) throw new ArgumentException("blockPath is required for block-path mode.");
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            EngineeringRecordExportPath?.Invoke(null);
            try
            {
                if (session.IsProjectNull())
                    return new BlockDocumentExportReply { Status = "InvalidState", Message = "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)" };
                var block = session.GetBlock(softwarePath, blockPath);
                if (block == null) return new BlockDocumentExportReply { Status = "NotFound", Message = "Block not found: '" + blockPath + "'" };
                var groupPath = block.Parent is PlcBlockGroup parent ? session.GetPlcBlockGroupPath(parent) : "";
                var exportPath = Path.Combine(tempDir, block.Name + ".xml");
                TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks", block.IsConsistent ? Array.Empty<string>() : new[] { blockPath }, "blockPath");
                if (File.Exists(exportPath)) File.Delete(exportPath);
                block.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), ExportOptions.None);
                EngineeringRecordExportPath?.Invoke(exportPath);
                var xml = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList()
                    .FirstOrDefault(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                return xml == null
                    ? new BlockDocumentExportReply { Status = "ExportFailed", Message = "Block export produced no .xml for '" + blockPath + "' in " + tempDir }
                    : new BlockDocumentExportReply { TempDir = tempDir, XmlPath = xml };
            }
            catch (AdapterPreconditionException) { throw; }
            catch (Exception error)
            {
                // Preserve authored engineering errors from the existing resolver.
                var status = error.GetType().GetProperty("Code")?.GetValue(error)?.ToString();
                return new BlockDocumentExportReply { Status = status ?? "ExportFailed", Message = status == null ? "Export failed" : error.Message };
            }
        }
    }
}
