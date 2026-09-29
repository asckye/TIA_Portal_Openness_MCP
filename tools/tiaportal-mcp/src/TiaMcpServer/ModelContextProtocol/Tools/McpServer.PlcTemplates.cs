using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ComposePlcAliasAlarmLad"), Description("[L2][PLC-Builders][OFFLINE] Independently generate a V21 LAD FC XML for Boolean aliases/simple alarm bits. rowsJson=[{source:[\"InputTag\"],destination:[\"DB\",\"Alarm\"],invert?:false,acknowledge?:[\"AckTag\"],title?:text,comment?:text}]. Without acknowledge: contact -> normal coil. With acknowledge: contact -> set coil, followed by acknowledge -> reset coil (RESET WINS if both true; level-triggered). Exact global symbol components; no string splitting, absolute addresses, indexed operands or type inference. Reject duplicate destinations. 1..500 rows. No tags/blocks imported, no safety certification; native import/compile remains unverified for this new builder. For arbitrary validated network layouts use InstantiatePlcXmlTemplates.")]
        public static ResponseMessage ComposePlcAliasAlarmLad([Description("Name of the generated LAD FC.")] string blockName, [Description("Positive FC number.")] int blockNumber, [Description("Structured JSON rows; exact shape is specified in the tool description.")] string rowsJson)
            => RunOfflineAnalysisTool("ComposePlcAliasAlarmLad", meta => { meta["xml"] = PlcAliasAlarmBuilder.Build(blockName, blockNumber, rowsJson); meta["targetVersion"] = "V21"; meta["nativeValidated"] = false; return "Boolean alias/alarm LAD XML composed."; });

        [McpServerTool(Name = "InstantiatePlcXmlTemplates"), Description("[L2][PLC-Builders][FILE] Expand an exported XML template into repeated PLC blocks/aliases/alarms without executing customization scripts. Template tokens {{Name}} in XML attributes/text are replaced structurally with XML escaping. rowsJson=[{fileName:\"FC_1.xml\",values:{Name:\"FC_1\"}}], 1..100 rows. Reject missing/unused keys, unsafe/duplicate filenames, DTD and any existing output. Default dryRun validates ALL rows/outputs and returns hashes/sizes; execution writes new files only. No TIA import or compile. Tokens do not edit element names, UIDs, wiring topology or faceplate internals automatically. Partial files reported if writing fails.")]
        public static ResponseMessage InstantiatePlcXmlTemplates([Description("Existing absolute XML template path with {{token}} placeholders in values.")] string templatePath, [Description("Structured JSON rows; exact shape is specified in the tool description.")] string rowsJson, [Description("Existing absolute directory where new expanded XML files will be written.")] string outputDirectory, [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true)
            => RunOfflineAnalysisTool("InstantiatePlcXmlTemplates", meta =>
            {
                if (!Path.IsPathRooted(outputDirectory) || !Directory.Exists(outputDirectory)) throw new ArgumentException("outputDirectory must be an existing absolute directory.");
                var documents = PlcTemplateExpansion.Expand(templatePath, rowsJson);
                var outputs = documents.Select(d => NativeFileOutput.Plan(Path.Combine(outputDirectory, d.Name))).ToList();
                var rows = new JsonArray(); meta["files"] = rows; meta["dryRun"] = dryRun;
                for (int i = 0; i < documents.Count; i++)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(documents[i].Xml);
                    string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    rows.Add(new JsonObject { ["path"] = outputs[i].FullName, ["bytes"] = bytes.Length, ["sha256"] = hash, ["written"] = false });
                }
                if (!dryRun)
                    for (int i = 0; i < documents.Count; i++)
                    {
                        meta["mayHaveWrittenFiles"] = true;
                        using (var stream = new FileStream(outputs[i].FullName, FileMode.CreateNew, FileAccess.Write))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(documents[i].Xml);
                        ((JsonObject)rows[i]!)["written"] = true; ((JsonObject)rows[i]!)["verified"] = NativeFileOutput.Verify(outputs[i]);
                    }
                return dryRun ? "Template expansion preview validated." : "Expanded XML files written; separate native import/compile validation required.";
            });
    }
}
