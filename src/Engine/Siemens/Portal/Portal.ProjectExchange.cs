using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true)
            => sharedLifecycle != null && !sharedLifecycle.Executing
                ? sharedLifecycle.Engine(() => RetrieveProjectArchive(archivePath, destinationDirectory, upgrade, dryRun))
                : RunHmiStepTool("RetrieveProjectArchive", meta => {
                if (_portal == null) throw new InvalidOperationException("Connect to TIA first.");
                if (_project != null || _session != null || _portal.Projects.Any() || _portal.LocalSessions.Any()) throw new InvalidOperationException("Use a connected Portal with no open project/session. Existing projects will never be closed automatically.");
                var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(archivePath)); if (!file.Exists) throw new FileNotFoundException("Archive not found.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destination required.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory)); if (directory.Exists) throw new IOException("Destination must be new; merge/overwrite refused.");
                meta["dryRun"] = dryRun; meta["upgrade"] = upgrade; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName;
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    AdoptProject(upgrade ? _portal.Projects.RetrieveWithUpgrade(file, directory) : _portal.Projects.Retrieve(file, directory),
                        "Retrieve returned no project; inspect destination for partial files.");
                    meta["project"] = EngineeringScalarProperties.Read(_project!); meta["openedProject"] = true;
                }
                return dryRun ? "Archive retrieval preview; no project opened or files created." : "Archive retrieved and returned project bound. No compile/download; native retrieval may upgrade only when upgrade=true.";
            }, requiresProject: false);
    }
}
