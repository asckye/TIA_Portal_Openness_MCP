using System;
using System.Collections.Generic;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Diagnostics;

namespace TiaMcp.PlcWorker;

internal sealed class ProjectTextProbeWorker(PlcFoundationEngine engine)
{
    public Dictionary<string, object?> ReadProbeProjectTexts(string plcPath, string sourceCulture, string targetCulture,
        string ownerColumn = "", string categoryColumn = "", string textListCategory = "", string alarmCategory = "", string sheetName = "")
        => ProjectTextProbe.Scan(Environment.GetEnvironmentVariable("TIA_MCP_CRASH_PROBES"), engine.ReleaseKey, plcPath, sheetName,
            ownerColumn, categoryColumn, textListCategory, alarmCategory, file => engine.ExecuteEngineeringServices(new EngineeringServicesRequest {
                Operation = "ExportProjectTexts", FilePath = file, SourceCulture = sourceCulture, TargetCulture = targetCulture, DryRun = false
            }, false), AdapterJournal.BeforeProbeStep, sourceCulture, targetCulture);
}
