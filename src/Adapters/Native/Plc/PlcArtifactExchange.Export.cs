using System;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private static string ResolveExportFile(string exportPath, string name, string groupPath, bool preservePath)
        {
            if (!preservePath && exportPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(exportPath))
                return exportPath;
            var fileName = Uri.EscapeDataString(name) + ".xml";
            return preservePath ? Path.Combine(exportPath, groupPath.Replace('/', '\\'), fileName) : Path.Combine(exportPath, fileName);
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {

            _session.RecordExportPath(null);

            try
            {
                if (_session.IsProjectNull())
                {
                    throw new PlcSoftwareException("InvalidState",
                            "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call " +
                            "OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
                }

                var block = _session.GetBlockForRead(softwarePath, blockPath);

                var blockGroupPath = block.Parent is PlcBlockGroup parentGroup ? _session.GetPlcBlockGroupPath(parentGroup) : "";
                exportPath = ResolveExportFile(exportPath, block.Name, blockGroupPath, preservePath);

                // TIA Portal never exports inconsistent blocks
                TiaOpenness.Shared.NativeExportPolicy
                    .RequireConsistent("blocks", block.IsConsistent ? Array.Empty<string>() : new[] { blockPath }, "blockPath");

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                PlcBlockPrimitives.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), ExportOptions.None);
                _session.RecordExportPath(exportPath);

                return block;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                //If the exception is already a PlcSoftwareException, use it; otherwise, wrap it in a new PlcSoftwareException
                var pex = ex as PlcSoftwareException ?? new PlcSoftwareException("ExportFailed", "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                throw pex;
            }
        }

    }
}
