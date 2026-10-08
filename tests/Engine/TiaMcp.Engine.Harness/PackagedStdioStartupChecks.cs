using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class PackagedStdioStartupChecks
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

    internal static void Run(Assembly server, int major, Action<string> pass)
    {
        if (!NoTiaInstalled(server))
        {
            Console.WriteLine("SKIP: packaged no-TIA startup requires a machine with no detected TIA installation.");
            return;
        }
        using (var layout = PackagedEngineLayout.Create(server, major))
        {
            RunHost(layout, major, false, pass);
            RunHost(layout, major, true, pass);
        }
    }

    private static bool NoTiaInstalled(Assembly server)
    {
        var engineering = Program.FindReferencedType(server, "TiaMcpServer.Siemens.Engineering");
        string? old = Environment.GetEnvironmentVariable("TiaPortalLocation");
        try
        {
            Environment.SetEnvironmentVariable("TiaPortalLocation", null);
            return engineering.GetMethod("DetectTiaMajorVersion", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null) == null;
        }
        finally { Environment.SetEnvironmentVariable("TiaPortalLocation", old); }
    }

    private static void RunHost(PackagedEngineLayout layout, int major, bool isolate, Action<string> pass)
    {
        string data = Path.Combine(layout.Root, "data-stdio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(layout.EnginePath)
        {
            Arguments = "--transport stdio --profile full --tia-major-version " + major + " --logging 0 "
                + (isolate ? "--isolate-openness" : "--no-isolate-openness"),
            WorkingDirectory = layout.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        start.EnvironmentVariables.Remove("TiaPortalLocation");
        start.EnvironmentVariables.Remove("TIA_MCP_PROFILE");
        start.EnvironmentVariables["TIA_MCP_DATA_DIRECTORY"] = data;
        start.EnvironmentVariables["TEMP"] = layout.Root;
        start.EnvironmentVariables["TMP"] = layout.Root;
        start.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(layout.Root, "local");
        start.EnvironmentVariables["APPDATA"] = Path.Combine(layout.Root, "roaming");
        Directory.CreateDirectory(start.EnvironmentVariables["LOCALAPPDATA"]!);
        Directory.CreateDirectory(start.EnvironmentVariables["APPDATA"]!);

        var errors = new StringBuilder();
        try
        {
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start staged engine."))
            {
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                try
                {
                    process.StandardInput.WriteLine(Json.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize",
                        @params = new { protocolVersion = "2024-11-05", capabilities = new { },
                            clientInfo = new { name = "PackagedNoTiaChecks", version = "1" } } }));
                    var initialized = Reply(process, 1, errors);
                    Check(initialized.ContainsKey("result"), "Packaged STDIO initialize failed: " + Json.Serialize(initialized));
                    process.StandardInput.WriteLine(Json.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }));

                    int count = 0, id = 2;
                    string? cursor = null;
                    do
                    {
                        object parameters = cursor == null ? new { } : new { cursor };
                        process.StandardInput.WriteLine(Json.Serialize(new { jsonrpc = "2.0", id, method = "tools/list", @params = parameters }));
                        var response = Reply(process, id++, errors);
                        var result = AsObject(response["result"]);
                        count += AsArray(result["tools"]).Count;
                        cursor = result.ContainsKey("nextCursor") ? Convert.ToString(result["nextCursor"]) : null;
                    } while (!String.IsNullOrEmpty(cursor));
                    Check(count == (Program.ExpectedFullToolCount(major)), "Packaged V" + major + " STDIO roster has " + count + " tools.");

                    var bootstrap = Call(process, id++, "InitializeEnvironment", null, errors);
                    var bootstrapData = AsObject(bootstrap["data"]);
                    Check(bootstrapData.TryGetValue("ready", out var bootstrapReady) && bootstrapReady is bool ready && !ready
                        && Convert.ToString(bootstrapData["recommendedReason"])?.IndexOf("no TIA Portal V", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Packaged InitializeEnvironment omitted the no-TIA cause: " + Json.Serialize(bootstrap)
                        + "\nEngine stderr: " + errors);
                    Check(Program.HasChinese(Convert.ToString(bootstrapData["recommendedFixZh"])),
                        "Packaged InitializeEnvironment omitted its Chinese recommendedFixZh: " + Json.Serialize(bootstrap));

                    var diagnostics = Call(process, id++, "GetEnvironmentDiagnostics", new { fix = false }, errors);
                    var doctor = AsObject(diagnostics["data"]);
                    Check(doctor["ready"] is bool doctorReady && !doctorReady
                        && Json.Serialize(doctor).IndexOf("no TIA Portal V", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Packaged GetEnvironmentDiagnostics omitted the no-TIA cause: " + Json.Serialize(diagnostics));
                    Check(Program.HasChinese(Convert.ToString(doctor["recommendedFixZh"])),
                        "Packaged GetEnvironmentDiagnostics omitted its Chinese recommendedFixZh: " + Json.Serialize(diagnostics));

                    var refusal = Call(process, id++, "GetSessionState", null, errors);
                    Check(IsReadinessRefusal(refusal), "Packaged GetSessionState was not refused before dispatch: " + Json.Serialize(refusal));
                    var refusalEnvironment = AsObject(AsObject(refusal["data"])["environment"]);
                    Check(Program.HasChinese(Convert.ToString(refusalEnvironment["recommendedFixZh"])),
                        "Packaged readiness refusal omitted its Chinese recommendedFixZh: " + Json.Serialize(refusal));
                    var workerRefusal = Call(process, id++, "RestartOpennessWorker", null, errors);
                    Check(IsReadinessRefusal(workerRefusal), "Packaged worker control was not refused before dispatch: " + Json.Serialize(workerRefusal));
                    var safeLookup = Call(process, id++, "FindTools", new { query = "BuildPlcUdt", limit = 1 }, errors);
                    Check(AsObject(safeLookup["data"]).ContainsKey("items"), "Packaged tool discovery failed without TIA.");
                    var usage = Call(process, id++, "GetToolUsage", new { toolName = "BuildPlcUdt" }, errors);
                    var usageData = AsObject(usage["data"]);
                    var buildArguments = AsObject(AsObject(AsObject(AsObject(usageData["example"])["request"])
                        ["params"])["arguments"]);
                    var offlineBuild = Call(process, id++, "BuildPlcUdt", buildArguments, errors);
                    Check(offlineBuild["ok"] is bool buildOk && buildOk,
                        "Packaged offline builder failed without TIA: " + Json.Serialize(offlineBuild));

                    if (isolate)
                    {
                        var worker = Call(process, id++, "GetOpennessWorkerStatus", null, errors);
                        string state = Convert.ToString(AsObject(AsObject(worker["data"])["evidence"])["worker"] is Dictionary<string, object> status
                            ? status["state"] : null) ?? "";
                        Check(state == "NotStarted", "No-TIA local calls unexpectedly started the isolated worker: " + Json.Serialize(worker));
                    }

                    Check(!process.HasExited, "Packaged STDIO engine exited after calls.");
                    pass("V" + major + " packaged STDIO " + (isolate ? "isolated" : "in-process")
                        + ": no Siemens DLLs, full roster, bootstrap, diagnostics, safe discovery and readiness refusal");
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(data, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, object> Call(Process process, int id, string name, object? arguments, StringBuilder errors)
    {
        process.StandardInput.WriteLine(Json.Serialize(new { jsonrpc = "2.0", id, method = "tools/call",
            @params = new { name, arguments = arguments ?? new { } } }));
        var reply = Reply(process, id, errors);
        var result = AsObject(reply["result"]);
        if (result.TryGetValue("structuredContent", out var structured) && structured is Dictionary<string, object> body) return body;
        foreach (var item in AsArray(result["content"]))
        {
            var block = AsObject(item);
            if (block.TryGetValue("text", out var text) && text is string json)
                return Json.Deserialize<Dictionary<string, object>>(json)!;
        }
        throw new InvalidOperationException(name + " returned no V4 result: " + Json.Serialize(reply));
    }

    private static Dictionary<string, object> Reply(Process process, int id, StringBuilder errors)
    {
        var read = process.StandardOutput.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Packaged STDIO request timed out. stderr: " + errors);
        string? line = read.GetAwaiter().GetResult();
        if (line == null) throw new InvalidOperationException("Packaged STDIO closed its protocol stream. stderr: " + errors);
        var reply = Json.Deserialize<Dictionary<string, object>>(line)
            ?? throw new InvalidDataException("Packaged STDIO returned invalid JSON: " + line);
        if (Convert.ToInt32(reply["id"]) != id) throw new InvalidDataException("Packaged STDIO returned an unexpected request ID.");
        return reply;
    }

    private static bool IsReadinessRefusal(Dictionary<string, object> response)
    {
        var error = AsObject(response["error"]);
        var details = AsObject(error["details"]);
        var meta = AsObject(response["meta"]);
        return Convert.ToString(error["code"]) == "RESOURCE_UNAVAILABLE"
            && Convert.ToString(details["resource"]) == "tia-openness-environment"
            && Convert.ToString(meta["outcome"]) == "rejected-before-operation"
            && Convert.ToString(meta["execution"]) == "not-started";
    }

    private static Dictionary<string, object> AsObject(object value)
        => value as Dictionary<string, object> ?? throw new InvalidOperationException("Expected an object result.");

    private static ArrayList AsArray(object value)
        => value as ArrayList ?? (value is object[] array ? new ArrayList(array) : throw new InvalidOperationException("Expected an array."));

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
