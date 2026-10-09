using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Loader;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class NativeExchangeService
    {
        private readonly IEngineeringSession _session;

        public NativeExchangeService(IEngineeringSession session) => _session = session;





        public ResponseMessage GeneratePlcLoadableFile(string softwarePath, string objectPathsJson, string objectKind, string targetOption, string filePath, bool dryRun = true)
            => _session.RunHmiStepTool("GeneratePlcLoadableFile", meta => {
                if (objectKind == "block" || objectKind == "unit") objectKind += "s";
                if (objectKind != "blocks" && objectKind != "units") throw new ArgumentException("objectKind must be blocks/units.");
                var names = _session.ExactNameList(objectPathsJson); var file = NativeFileOutput.Plan(filePath);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                if (!Enum.GetNames(typeof(TargetOption)).Contains(targetOption)) throw new ArgumentException("targetOption must be one of: " + string.Join("/", Enum.GetNames(typeof(TargetOption))) + " (Siemens.Engineering.SW.Loader.TargetOption).");
                var option = (TargetOption)EngineeringScalarProperties.ConvertValue(JsonValue.Create(targetOption), typeof(TargetOption))!;
                var service = plc.GetService<LoadableProvider>() ?? throw new NotSupportedException("LoadableProvider unavailable.");
                var blocks = objectKind == "blocks" ? names.Select(n => (PlcBlock)_session.ExactMasterCopyPlcSource(softwarePath, n, true)).ToArray() : Array.Empty<PlcBlock>();
                var unitRoot = objectKind == "units" ? plc.GetService<PlcUnitProvider>()?.UnitGroup.Units : null;
                var units = objectKind == "units" ? names.Select(n => unitRoot?.Find(n) ?? throw new InvalidOperationException("Exact software unit not found: " + n)).ToArray() : Array.Empty<PlcUnit>();
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["targetOption"] = targetOption; meta["objectPaths"] = JsonNode.Parse(objectPathsJson);
                if (!dryRun)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    if (objectKind == "blocks") service.GenerateLoadable(file, blocks, option); else service.GenerateLoadable(file, units, option);
                    meta["file"] = NativeFileOutput.Verify(file); meta["apiCallSuccess"] = true;
                }
                return dryRun ? "Loadable generation preview; no PLC contacted." : "Loadable file generated and hashed. Not downloaded; deployment/readiness not asserted.";
            });








    }
}
