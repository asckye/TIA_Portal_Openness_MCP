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
        [McpServerTool(Name = "RenderPlcVisualDiff"), Description("[L2][Validation][FILE] Compare two single-block SimaticML XML exports with an interface/structural diff and side-by-side LAD graphics highlighting added/removed/changed/rewired components. Writes a NEW absolute .html report. Other languages retain structural diff without claiming LAD rendering. Uses the MIT TiaGitAddIn.Core parser and layout. Offline only; no TIA query, save, compile or download.")]
        public static ResponseMessage RenderPlcVisualDiff([Description("Existing absolute before-export SimaticML XML path.")] string leftFilePath, [Description("Existing absolute after-export SimaticML XML path.")] string rightFilePath, [Description("New absolute output file; existing files are refused.")] string outputPath)
            => RunOfflineAnalysisTool("RenderPlcVisualDiff", meta =>
            {
                if (!outputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end in .html.");
                var output = NativeFileOutput.Plan(outputPath);
                var result = PlcVisualComparison.Compare(leftFilePath, rightFilePath, out var html);
                foreach (var item in result) meta[item.Key] = item.Value?.DeepClone();
                using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(html);
                meta["output"] = NativeFileOutput.Verify(output); meta["mayHaveWrittenFiles"] = true;
                return "PLC visual and structural comparison written.";
            });
    }
}
