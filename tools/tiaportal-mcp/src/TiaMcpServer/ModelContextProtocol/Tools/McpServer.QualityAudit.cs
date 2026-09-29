using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "AuditEngineeringExports"), Description("[L2][Validation][FILE] Independent offline engineering quality checks: block names (optional regex policy), duplicate names, block/network comments, metadata, network count, plus configurable XML XPath rules for hardware, library or other export metadata. rulesJson=[{id,files?:regex,xpath:element-selector,minCount?:0,maxCount?:number,valuePattern?:regex,severity?:info|warning|error}]. Use local-name() in XPath for namespaced XML. Unmatched rules are explicitly notEvaluated. Bounded 1000 files/2000 findings; failures and truncation mark dataComplete=false. Optional NEW absolute .html or .pdf report; PDF requires the companion Python environment (ReportLab). success means the audit executed; qualityPassed is the policy verdict. Does not query native cross references, connect to TIA, certify hardware or compile.")]
        public static ResponseMessage AuditEngineeringExports([Description("Existing absolute directory of exported XML/SCL/S7DCL/AML files.")] string directoryPath, [Description("JSON array of explicit XPath count/value policies; unmatched rules are unevaluated.")] string rulesJson = "[]", [Description("Optional regex naming policy; empty disables naming-policy findings.")] string blockNamePattern = "", [Description("Maximum networks per block before a warning, 1..10000.")] int maxNetworks = 100, [Description("Optional new absolute .html/.pdf report path; empty returns JSON only.")] string reportPath = "")
            => RunOfflineAnalysisTool("AuditEngineeringExports", meta =>
            {
                var output = string.IsNullOrEmpty(reportPath) ? null : NativeFileOutput.Plan(reportPath);
                bool pdf = reportPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (output != null && !pdf && !reportPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("reportPath must end in .html or .pdf.");
                var result = EngineeringQualityAudit.Audit(directoryPath, rulesJson, blockNamePattern, maxNetworks);
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                if (output != null)
                {
                    if (pdf)
                    {
                        string root = EcosystemFiles.RepositoryRoot();
                        string python = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON") ?? Path.Combine(root, "TiaMcp_Output", "ecosystem-python", "Scripts", "python.exe");
                        if (!Path.IsPathRooted(python) || !File.Exists(python)) throw new FileNotFoundException("PDF renderer requires Install-PlcTools.ps1 and its Python environment.");
                        var request = new JsonObject { ["mode"] = "audit-pdf", ["outputPath"] = output.FullName, ["report"] = result.DeepClone() };
                        var render = EcosystemFiles.Run(python, new[] { "-I", "-X", "utf8", Path.Combine(root, "scripts", "ecosystem", "plc_tools_bridge.py") }, output.DirectoryName!, request.ToJsonString(), 60).GetAwaiter().GetResult();
                        meta["pdfRenderer"] = render;
                        if (render["success"]?.GetValue<bool>() != true) throw new InvalidOperationException("PDF renderer failed: " + render["stderr"]);
                    }
                    else
                    {
                        using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(EngineeringQualityAudit.Html(result));
                    }
                    meta["report"] = NativeFileOutput.Verify(output);
                }
                return "Audit executed. Inspect qualityPassed, dataComplete, findings and notEvaluated rules.";
            });
    }
}
