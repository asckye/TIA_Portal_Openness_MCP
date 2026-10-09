using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static string WorkerQuote(string value)
    {
        var result=new System.Text.StringBuilder("\""); int slashes=0;
        foreach(char ch in value) { if(ch=='\\'){slashes++;continue;} result.Append('\\',ch=='"'?slashes*2+1:slashes).Append(ch); slashes=0; }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    private static async Task ChildStdinTests()
    {
        int before = Passed, failures = 0;
        var previous = Console.InputEncoding;
        async Task Run(string label, Func<Task> test)
        {
            try { await Test(label, async () => {
                var encoding = Console.InputEncoding;
                await test();
                Check(Console.InputEncoding.CodePage == encoding.CodePage &&
                    Console.InputEncoding.GetPreamble().SequenceEqual(encoding.GetPreamble()), "Console input encoding was not restored");
                if (encoding.GetPreamble().Length == 0)
                    Check(ReferenceEquals(Console.InputEncoding, encoding), "BOM-free console encoding was unnecessarily replaced");
            }); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + label + ": " + ex); }
        }
        try {
            // Deliberately undo the host workaround: both producers must own their encoding.
            foreach (var encoding in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.GetEncoding(936), new System.Text.UTF8Encoding(false) }) {
                Console.InputEncoding = encoding;
                foreach (string? input in new[] { "{\"value\":\"中文🟦\"}\nsecond\n", "", null }) {
                    await Run("LocalProcess stdin/EOF under CP" + encoding.CodePage + " (" + (input == null ? "null" : input.Length.ToString()) + ")", async () => {
                        string hex = await LocalProcessInput(input);
                        Check(hex == BitConverter.ToString(new System.Text.UTF8Encoding(false).GetBytes(input ?? "")), "Child bytes differ: " + hex);
                    });
                }
                await Run("failed child start restores CP" + encoding.CodePage + " and preserves the start error", async () => {
                    string missing = Path.Combine(Environment.CurrentDirectory, "missing-child-" + Guid.NewGuid().ToString("N") + ".exe");
                    await Fault(LocalProcessInput("", missing), typeof(System.ComponentModel.Win32Exception));
                });
                await Run("concurrent LocalProcess starts preserve CP" + encoding.CodePage, async () => {
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () => {
                        string hex = await LocalProcessInput("中文🟦\n");
                        Check(hex == "E4-B8-AD-E6-96-87-F0-9F-9F-A6-0A", "Concurrent child bytes differ: " + hex);
                    })));
                });
            }
        }
        finally { Console.InputEncoding = previous; }
        Console.WriteLine("COMPLETE: " + (Passed - before) + " child stdin checks passed; " + failures + " failed");
        Check(failures == 0, "Child stdin regression failed");
    }

    private static async Task<string> LocalProcessInput(string? input, string? executable = null)
    {
        var type = FindServerType(Server, "TiaOpenness.Shared.LocalProcess");
        var task = (Task)type.GetMethod("Run")!.Invoke(null, new object?[] {
            executable ?? Assembly.GetExecutingAssembly().Location, new[] { "stdin-hex-fixture" },
            Environment.CurrentDirectory, input, 5, 1024 * 1024, null })!;
        await Bounded(AsResult(task), 10000);
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Check((bool)result.GetType().GetProperty("Success")!.GetValue(result)!, "Hex child failed or did not receive EOF");
        return ((string)result.GetType().GetProperty("Stdout")!.GetValue(result)!).Trim();
    }

    private sealed class SdkGuardFixture : ModelContextProtocol.Server.McpServerTool
    {
        private readonly bool write;
        private readonly bool legacy;
        internal int Calls;
        internal SdkGuardFixture(bool write, bool legacy = false) { this.write = write; this.legacy = legacy; }
        public override ModelContextProtocol.Protocol.Tool ProtocolTool => new ModelContextProtocol.Protocol.Tool {
            Name = write ? "RestartOpennessWorker" : "GetSessionState" };
        public override ValueTask<ModelContextProtocol.Protocol.CallToolResult> InvokeAsync(
            ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (legacy) return new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(new ModelContextProtocol.Protocol.CallToolResult {
                Content = new[] { new ModelContextProtocol.Protocol.TextContentBlock { Text = "{\"message\":\"old\",\"meta\":{\"success\":true}}" } } });
            throw new ModelContextProtocol.McpException("SECRET-fixture-argument", ModelContextProtocol.McpErrorCode.InternalError);
        }
    }

    private static async Task SerializedCallGuardTests()
    {
        int before = Passed;
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        string data = Path.Combine(Path.GetTempPath(), "tia-sdk-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "config"));
        File.WriteAllText(Path.Combine(data, "config", "approval.settings"), "enabled=false\ntimeoutSeconds=120\n");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", data);
            foreach (var scenario in new[] { "read-exception", "write-exception", "legacy-result", "cancel-before-call" })
                await Test("direct SDK dispatch guard returns V4: " + scenario, async () => {
                    // Each scenario is its own session: write-exception leaves its session reset-required (P6-60).
                    var session = new object();
                    Server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetProperty("ApprovalSessionKeyForTests", All)!
                        .SetValue(null, (Func<object?>)(() => session));
                    var fixture = new SdkGuardFixture(scenario == "write-exception", scenario == "legacy-result");
                    var wrapper = Server.GetType("TiaMcpServer.ModelContextProtocol.SerializedCallTool", true)!;
                    var tool = (ModelContextProtocol.Server.McpServerTool)Activator.CreateInstance(wrapper, All, null, new object[] { fixture }, null)!;
                    using var cancellation = new CancellationTokenSource();
                    if (scenario == "cancel-before-call") cancellation.Cancel();
                    var result = await tool.InvokeAsync(null!, cancellation.Token);
                    var text = result.Content.Cast<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
                    var body = Parse(text);
                    Check(Json.Serialize(Parse(result.StructuredContent!.ToString())) == Json.Serialize(body)
                        && (int)body["schemaVersion"] == 4 && !(bool)body["ok"] && result.IsError == true, "SDK guard is not a matching V4 error");
                    var error = (Dictionary<string, object>)body["error"];
                    var meta = (Dictionary<string, object>)body["meta"];
                    bool unknown = scenario == "write-exception", cancelled = scenario == "cancel-before-call";
                    Check((string)error["code"] == (unknown ? "OUTCOME_UNKNOWN" : cancelled ? "CANCELLED" : "INTERNAL_ERROR")
                        && (string)meta["outcome"] == (unknown ? "unknown" : cancelled ? "rejected-before-operation" : "read-failed")
                        && (string)meta["execution"] == (unknown ? "unknown" : cancelled ? "not-started" : "read-only")
                        && (bool)meta["requiresSessionReset"] == unknown, "SDK guard lost typed outcome details: " + scenario + " " + text);
                    Check(fixture.Calls == (cancelled ? 0 : 1) && !text.Contains("SECRET") && !body.ContainsKey("message"), "SDK guard leaked arguments or dispatched cancellation");
                    if (unknown)
                    {
                        var warnings = (IList)meta["warnings"];
                        Check(warnings.Cast<Dictionary<string, object>>().Count(w => (string)w["code"] == "APPROVAL_DISABLED") == 1,
                            "SDK guard omitted or duplicated the disabled approval warning");
                    }
                });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previous);
            try { Directory.Delete(data, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine("COMPLETE: " + (Passed-before) + " direct SDK guard checks passed; no native call executed");
    }

}
