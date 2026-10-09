using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class HttpStartupNoTiaChecks
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

    internal static void Run(Assembly server, int major, Action<string> pass)
    {
        var engineering = Program.FindReferencedType(server, "TiaMcpServer.Siemens.Engineering");
        string? previousLocation = Environment.GetEnvironmentVariable("TiaPortalLocation");
        try
        {
            Environment.SetEnvironmentVariable("TiaPortalLocation", null);
            if (engineering.GetMethod("DetectTiaMajorVersion", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null) != null)
            {
                Console.WriteLine("SKIP: real HTTP startup without TIA requires a machine with no detected TIA installation.");
                return;
            }
        }
        finally { Environment.SetEnvironmentVariable("TiaPortalLocation", previousLocation); }

        int port;
        var socket = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }
        finally { socket.Stop(); }
        string prefix = "http://127.0.0.1:" + port + "/";
        try
        {
            using (var probe = new HttpListener())
            {
                probe.Prefixes.Add(prefix);
                probe.Start();
            }
        }
        catch (HttpListenerException error)
        {
            Console.WriteLine("SKIP: this platform cannot open a local HTTP listener (" + error.GetType().Name + ").");
            return;
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine("SKIP: this platform does not support HttpListener.");
            return;
        }

        using (var layout = PackagedEngineLayout.Create(server, major))
            foreach (bool isolate in new[] { false, true }) RunHost(layout, major, prefix, isolate, pass);
    }

    private static void RunHost(PackagedEngineLayout layout, int major, string prefix, bool isolate, Action<string> pass)
    {
        string key = Guid.NewGuid().ToString("N");
        string data = Path.Combine(layout.Root, "data-http-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(layout.Root, "local-" + Guid.NewGuid().ToString("N"));
        string roaming = Path.Combine(layout.Root, "roaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(roaming);
        var start = new ProcessStartInfo(layout.EnginePath)
        {
            Arguments = String.Join(" ", new[] { "--transport", "http", "--http-prefix", prefix,
                "--http-api-key", key, "--profile", "full", "--tia-major-version", major.ToString(),
                "--logging", "1", isolate ? "--isolate-openness" : "--no-isolate-openness" }),
            WorkingDirectory = layout.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.EnvironmentVariables.Remove("TiaPortalLocation");
        start.EnvironmentVariables.Remove("TIA_MCP_PROFILE");
        start.EnvironmentVariables["TIA_MCP_DATA_DIRECTORY"] = data;
        start.EnvironmentVariables["TEMP"] = layout.Root;
        start.EnvironmentVariables["TMP"] = layout.Root;
        start.EnvironmentVariables["LOCALAPPDATA"] = local;
        start.EnvironmentVariables["APPDATA"] = roaming;
        var output = new StringBuilder();
        var errors = new StringBuilder();
        try
        {
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the real engine EXE."))
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    WaitForReady(process, prefix, key, errors);
                    var initialized = Post(prefix, key, new { jsonrpc = "2.0", id = 1, method = "initialize",
                        @params = new { protocolVersion = "2024-11-05", capabilities = new { },
                            clientInfo = new { name = "TiaMcp.Engine.Harness", version = "1" } } });
                    Check(initialized.ContainsKey("result"), "HTTP MCP initialize failed: " + Json.Serialize(initialized));
                    Post(prefix, key, new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } });

                    int count = 0;
                    string? cursor = null;
                    int page = 0;
                    do
                    {
                        object parameters = cursor == null ? new { } : new { cursor };
                        var listed = Post(prefix, key, new { jsonrpc = "2.0", id = 2 + page, method = "tools/list", @params = parameters });
                        var result = AsObject(listed["result"]);
                        count += AsArray(result["tools"]).Count;
                        cursor = result.ContainsKey("nextCursor") ? Convert.ToString(result["nextCursor"]) : null;
                        page++;
                    } while (!String.IsNullOrEmpty(cursor));

                    int expected = Program.ExpectedEngineToolCount(major);
                    Check(count == expected, "V" + major + " HTTP " + (isolate ? "isolated" : "direct")
                        + " roster has " + count + " tools; expected " + expected + ".");

                    int nextCallId = 20 + page;
                    var initializedEnvironment = Call(prefix, key, nextCallId++, "InitializeEnvironment");
                    var environment = AsObject(initializedEnvironment["data"]);
                    Check(environment["ready"] is bool ready && !ready
                        && Convert.ToString(environment["recommendedReason"])?.IndexOf("no TIA Portal V", StringComparison.OrdinalIgnoreCase) >= 0,
                        "InitializeEnvironment omitted the no-TIA readiness cause: " + Json.Serialize(initializedEnvironment));
                    Check(Program.HasChinese(Convert.ToString(environment["recommendedFixZh"])),
                        "InitializeEnvironment omitted its Chinese recommendedFixZh: " + Json.Serialize(initializedEnvironment));

                    var diagnostics = Call(prefix, key, nextCallId++, "GetEnvironmentDiagnostics", new { fix = false });
                    var doctor = AsObject(diagnostics["data"]);
                    var checks = AsArray(doctor["checks"]);
                    Check(doctor["ready"] is bool diagnosticsReady && !diagnosticsReady
                        && Json.Serialize(checks).IndexOf("no TIA Portal V", StringComparison.OrdinalIgnoreCase) >= 0,
                        "GetEnvironmentDiagnostics omitted the no-TIA readiness cause: " + Json.Serialize(diagnostics));
                    Check(Program.HasChinese(Convert.ToString(doctor["recommendedFixZh"])),
                        "GetEnvironmentDiagnostics omitted its Chinese recommendedFixZh: " + Json.Serialize(diagnostics));

                    var refusal = Call(prefix, key, nextCallId++, "SaveProject");
                    Check(IsReadinessRefusal(refusal),
                        "No-TIA HTTP host did not refuse SaveProject before dispatch: " + Json.Serialize(refusal));
                    var refusalEnvironment = AsObject(AsObject(refusal["data"])["environment"]);
                    Check(Program.HasChinese(Convert.ToString(refusalEnvironment["recommendedFixZh"])),
                        "Readiness refusal omitted its Chinese recommendedFixZh: " + Json.Serialize(refusal));
                    var discovery = Call(prefix, key, nextCallId++, "FindTools", new { query = "BuildPlcUdt", limit = 1 });
                    Check(AsObject(discovery["data"]).ContainsKey("items"), "Packaged HTTP discovery failed without TIA.");
                    var usage = Call(prefix, key, nextCallId++, "GetToolUsage", new { toolName = "BuildPlcUdt" });
                    var usageData = AsObject(usage["data"]);
                    var buildArguments = AsObject(AsObject(AsObject(AsObject(usageData["example"])["request"])
                        ["params"])["arguments"]);
                    var offlineBuild = Call(prefix, key, nextCallId++, "BuildPlcUdt", buildArguments);
                    Check(offlineBuild["ok"] is bool buildOk && buildOk,
                        "Packaged HTTP offline builder failed without TIA: " + Json.Serialize(offlineBuild));
                    if (isolate)
                    {
                        var workerRefusal = Call(prefix, key, nextCallId++, "RestartOpennessWorker");
                        Check(IsReadinessRefusal(workerRefusal),
                            "No-TIA HTTP host did not refuse worker control before dispatch: " + Json.Serialize(workerRefusal));
                        var worker = AsObject(AsObject(Call(prefix, key, nextCallId++, "GetOpennessWorkerStatus")["data"])["evidence"])["worker"];
                        Check(Convert.ToString(AsObject(worker)["state"]) == "NotStarted",
                            "A refused no-TIA HTTP worker control started the worker.");
                    }
                    Check(!process.HasExited, "HTTP host exited after an expected readiness refusal.");
                    pass("V" + major + " HTTP real EXE " + (isolate ? "with" : "without")
                        + " isolation: roster, readiness cause and pre-dispatch refusal");
                }
                catch
                {
                    if (process.HasExited)
                        throw new InvalidOperationException("Real HTTP engine exited with " + process.ExitCode
                            + ". stderr: " + errors + " stdout: " + output);
                    throw;
                }
                finally
                {
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

    private static void WaitForReady(Process process, string prefix, string key, StringBuilder errors)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException("Real HTTP engine exited with " + process.ExitCode + ". stderr: " + errors);
            try
            {
                var ready = Get(prefix + "mcp/ready", key);
                if (ready["mcpHostReady"] is bool value && value) return;
            }
            catch (WebException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Real HTTP engine did not become ready. stderr: " + errors);
    }

    private static Dictionary<string, object> Call(string prefix, string key, int id, string name, object? arguments = null)
    {
        var reply = Post(prefix, key, new { jsonrpc = "2.0", id, method = "tools/call",
            @params = new { name, arguments = arguments ?? new { } } });
        var result = AsObject(reply["result"]);
        if (result.TryGetValue("structuredContent", out var structured) && structured is Dictionary<string, object> body)
            return body;
        foreach (var item in AsArray(result["content"]))
        {
            var block = AsObject(item);
            if (block.TryGetValue("text", out var value) && value is string text)
                return Json.Deserialize<Dictionary<string, object>>(text)!;
        }
        throw new InvalidOperationException(name + " returned no structured V4 result: " + Json.Serialize(reply));
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

    private static Dictionary<string, object> Get(string url, string key)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Proxy = null;
        request.Headers["X-API-Key"] = key;
        request.Timeout = 1000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd())!;
    }

    private static Dictionary<string, object> Post(string prefix, string key, object body)
    {
        var request = (HttpWebRequest)WebRequest.Create(prefix + "mcp");
        request.Method = "POST";
        request.Proxy = null;
        request.ContentType = "application/json";
        request.Accept = "application/json";
        request.Headers["X-API-Key"] = key;
        request.Timeout = 15000;
        byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(body));
        request.ContentLength = bytes.Length;
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            string text = reader.ReadToEnd();
            return String.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>()
                : Json.Deserialize<Dictionary<string, object>>(text)!;
        }
    }

    private static Dictionary<string, object> AsObject(object value)
        => value as Dictionary<string, object> ?? throw new InvalidOperationException("Expected an object result.");

    private static ArrayList AsArray(object value)
        => value as ArrayList ?? (value is object[] array ? new ArrayList(array) : throw new InvalidOperationException("Expected an array result."));

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
