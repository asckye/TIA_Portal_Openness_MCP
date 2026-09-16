using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ImportPlcWatchTableOffline(string softwarePath, string filePath, string groupPath = "", bool dryRun = true)
            => RunHmiStepTool("ImportPlcWatchTableOffline", meta => {
                var source = new FileInfo(filePath); if (!source.Exists) throw new FileNotFoundException("Watch table XML missing.", filePath);
                meta["expectedCount"] = WatchTableImportValidation.Validate(source.FullName);
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var plc = ExactPlcForEngineering(softwarePath, !dryRun);
                var group = (PlcWatchAndForceTableGroup)EngineeringGroupOperations.Group(plc.WatchAndForceTableGroup, groupPath);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun)
                {
                    meta["mayHaveChanged"] = true;
                    var imported = group.WatchTables.Import(source, ImportOptions.None).ToArray();
                    meta["actualCount"] = imported.Length;
                    meta["names"] = new JsonArray(imported.Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray());
                    meta["dataComplete"] = imported.Length == meta["expectedCount"]!.GetValue<int>();
                    if (!meta["dataComplete"]!.GetValue<bool>()) throw new InvalidOperationException("Import returned a different number of watch tables; inspect project before retry.");
                }
                return dryRun ? "Offline watch table import preview. No online monitoring or force operations." : "Offline watch table import completed without overwrite, save, online action or download.";
            });
    }
}
