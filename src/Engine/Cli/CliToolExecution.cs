using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Cli
{
    internal sealed class CliToolExecution
    {
        private sealed class Stopped : Exception { }
        private readonly List<Envelope> results = new List<Envelope>();

        internal static Envelope Read(CallToolResult result)
            => V4Json.Deserialize<Envelope>(result.StructuredContent?.ToJsonString()
                ?? throw new InvalidOperationException("The tool did not return a V4 envelope."));

        // Retain the original exception control flow: most verbs abort, while the
        // document import loop continues after an individual document failure.
        private void Invoke(Func<object> action, Func<Func<object>, CallToolResult> boundary,
            bool preparation = false, bool continueOnException = false, string? resultTool = null)
        {
            bool threw = false;
            var result = Read(boundary(() =>
            {
                try { return action(); }
                catch (Exception error) { threw = true; Console.Error.WriteLine("ERROR: " + error.Message); throw; }
            }));
            if (resultTool != null) result = Rename(result, resultTool);
            if (!preparation || !result.Ok) results.Add(result);
            if (threw && !continueOnException) throw new Stopped();
        }

        private void Session(string tool, Func<object> action, bool write, bool preparation = false, string? resultTool = null)
            => Invoke(action, call => SessionToolContract.Run(tool, write, true, call), preparation, resultTool: resultTool);

        private void Plc(string tool, Func<object> action, bool write)
            => Invoke(action, call => PlcToolContract.Run(tool, write, true, call));

        private void Documents(string tool, Func<object> action, bool continueOnException = false)
            => Invoke(action, call => PlcExchangeContract.Run(tool, () => (ResponseMessage)call(), true, true),
                continueOnException: continueOnException);

        private void Open(string path)
        {
            if (!EngineServices.Get<Siemens.Portal>().IsConnected())
                Session("ConnectPortal", () => EngineServices.Get<SessionTools>().Connect(), true, preparation: true);
            Session("OpenProject", () => EngineServices.Get<ProjectSessionTools>().OpenProject(Path.GetFullPath(path)), true, preparation: true);
        }

        internal static int Run(string[] args)
        {
            var execution = new CliToolExecution();
            string verb = args[0].ToLowerInvariant();
            var output = Console.Out;
            // Legacy implementations may log to stdout. The sole public output is
            // written only after the tool and its diagnostics have completed.
            Console.SetOut(Console.Error);
            try { execution.Execute(args); }
            catch (Stopped) { if (execution.results.Count == 0) throw; }
            catch (Exception error)
            {
                Console.Error.WriteLine("ERROR: " + error.Message);
                var detail = error is IOException || error is UnauthorizedAccessException
                    ? (ErrorDetails)new IoFailedDetails("CLI input/output", null) : new InternalErrorDetails(null);
                execution.results.Add(Envelope.Create<object?>(null, new Error("The CLI operation could not be completed.", detail),
                    new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, verb, Meta.Correlate(null),
                        Outcome.ReadFailed, Execution.ReadOnly, false, BehaviorPolicy.NotApplicable,
                        Completeness.None, null, Array.Empty<Warning>())));
            }
            finally { Console.SetOut(output); }
            return CliBoundary.Write(CliBoundary.Combine(verb, execution.results), output);
        }

        private void Execute(string[] args)
        {
            string verb = args[0].ToLowerInvariant();
            string path = CliBoundary.PathArgument(args);
            bool Flag(string name) => args.Skip(1).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            string? Option(string name)
            {
                for (int i = 1; i < args.Length - 1; i++)
                    if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            if (verb == "gen" || verb == "patch")
            {
                string json = SpecLoader.LoadAsJson(path);
                bool dryRun = Flag("--dry-run");
                // Patch uses the scaffold's existing evidence rules, including
                // failed native steps whose post-state cannot be established.
                Session("BuildProjectScaffold", () => verb == "gen"
                    ? EngineServices.Get<ProjectSessionTools>().ScaffoldProject(json, dryRun)
                    : McpServer.PatchProject(json, dryRun, Flag("--no-overwrite")), !dryRun,
                    resultTool: verb == "patch" ? "PatchProject" : null);
                return;
            }
            Open(path);
            string plc = Option("--plc") ?? "PLC_1";
            switch (verb)
            {
                case "compile":
                    Plc("CompilePlcDiagnostics", () => EngineServices.Get<PlcBlocksTools>().CompileAndDiagnosePlc(plc), true);
                    break;
                case "describe":
                    Invoke(() => EngineServices.Get<DevicesTools>().GetProjectTree(),
                        call => HardwareContract.Run("GetProjectTree", () => (ResponseMessage)call(), false, false));
                    if (!Flag("--json") && !string.IsNullOrWhiteSpace(Option("--plc")))
                        Plc("ListPlcBlocks", () => EngineServices.Get<PlcBlocksTools>().GetBlocks(plc, ""), false);
                    break;
                case "export":
                    string directory = Option("--out")!;
                    string block = Option("--block")!;
                    Directory.CreateDirectory(directory);
                    if (Flag("--scl")) Documents("ExportPlcBlockDocuments", () => EngineServices.Get<DocumentsTools>().ExportAsDocuments(plc, block, directory));
                    else Plc("ExportPlcBlock", () => EngineServices.Get<PlcBlocksTools>().ExportBlock(plc, block, directory), true);
                    break;
                case "import":
                    string from = Option("--from")!;
                    bool overwrite = !Flag("--no-overwrite");
                    var xml = Directory.GetFiles(from, "*.xml");
                    if (xml.Length > 0)
                        Plc("ImportPlcBlocksFromDirectory", () => EngineServices.Get<PlcBlocksTools>().ImportBlocksFromDirectory(plc, "", from, "", overwrite), true);
                    var documents = Directory.GetFiles(from, "*.s7dcl");
                    foreach (var file in documents)
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        Documents("ImportPlcBlockDocuments", () => EngineServices.Get<DocumentsTools>().ImportFromDocuments(plc, "", from, name, overwrite ? "Override" : "None"), continueOnException: true);
                    }
                    if (xml.Length == 0 && documents.Length == 0)
                        results.Add(Read(McpServer.V4Reject("ImportPlcBlocksFromDirectory",
                            new Error("No .xml or .s7dcl files were found.", new NotFoundDetails(from)))));
                    break;
            }
        }

        private static Envelope Rename(Envelope value, string tool)
        {
            var m = value.Meta;
            return Envelope.Create(value.Data, value.Error, new Meta(m.Timestamp, m.ReleaseKey, tool, m.RequestId,
                m.Outcome, m.Execution, m.RequiresSessionReset, m.BehaviorPolicy, m.Completeness, m.Paging, m.Warnings));
        }


    }
}
