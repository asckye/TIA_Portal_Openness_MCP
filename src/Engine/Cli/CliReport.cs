using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Cli
{
    internal sealed class CliReport
    {
        private readonly List<Envelope> steps = new List<Envelope>();

        internal T Capture<T>(string tool, Func<T> operation, bool writes = false)
        {
            T value = default!;
            Exception? failure = null;
            object Call()
            {
                var output = Console.Out;
                try { Console.SetOut(Console.Error); value = operation(); return value!; }
                catch (Exception error) { failure = error; throw; }
                finally { Console.SetOut(output); }
            }
            CallToolResult mapped = tool == "ProbeGlobalLibrary" ? LibraryToolContract.Run(tool, writes, true, Call)
                : tool == "ValidateAutomationContext" ? HardwareContract.Run(tool, () => (ResponseMessage)Call(), false, false)
                : tool == "ListPlcWatchTables" || tool == "ExportPlcWatchTablesToDirectory"
                    || tool == "ProbePlcMonitorOnlineCapabilities" || tool == "GetPlcWatchTableCurrentValuesReadOnly"
                    ? PlcToolContract.Run(tool, writes, true, Call)
                : SessionToolContract.Run(tool, writes, writes, Call);
            steps.Add(CliToolExecution.Read(mapped));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return value;
        }

        internal void Save(string tool, JsonObject body, string jsonPath, string markdownPath, string markdown)
        {
            var basis = steps.Count > 0 ? CliBoundary.Combine(tool, steps)
                : Envelope.Create<object?>(null, null, new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool,
                    Meta.Correlate(null), Outcome.Succeeded, Execution.ReadOnly, false, BehaviorPolicy.NotApplicable,
                    Completeness.Complete, null, Array.Empty<Warning>()));
            body.Remove("ok");
            body["jsonPath"] = jsonPath;
            body["markdownPath"] = markdownPath;
            body["items"] = JsonNode.Parse(V4Json.Serialize(steps.Select((s, i) => new BatchItem(i, s.Meta.Tool, s)).ToArray()));
            var m = basis.Meta;
            var result = Envelope.Create(body, basis.Error, new Meta(m.Timestamp, m.ReleaseKey, tool, m.RequestId,
                m.Outcome, m.Execution, m.RequiresSessionReset, m.BehaviorPolicy, m.Completeness, m.Paging, m.Warnings));
            string outcome = V4Json.Deserialize<string>(V4Json.Serialize(m.Outcome));
            markdown += "\n- Outcome: " + outcome + "\n";
            try
            {
                File.WriteAllText(jsonPath, V4Json.Serialize(result), new UTF8Encoding(false));
                File.WriteAllText(markdownPath, markdown, new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("ERROR: " + error.Message);
                var warnings = m.Warnings.Concat(new[] { new Warning(WarningCode.DiagnosticWriteFailed,
                    "The requested report files could not both be written.", new Dictionary<string, JsonElement>()) }).ToArray();
                result = Envelope.Create(body, basis.Error ?? new Error("Report output failed.", new IoFailedDetails("write report", null)),
                    new Meta(m.Timestamp, m.ReleaseKey, tool, m.RequestId, basis.Ok ? Outcome.ReadFailed : m.Outcome,
                        basis.Ok ? Execution.ReadOnly : m.Execution, m.RequiresSessionReset, m.BehaviorPolicy,
                        m.Completeness, m.Paging, warnings));
            }
            Environment.ExitCode = CliBoundary.Write(result, Console.Out);
        }
    }
}
