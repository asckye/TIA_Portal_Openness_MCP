using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.WriteGuard;
using Xunit;

namespace TiaMcp.ShippedTools.Tests;

public sealed class WriteGuardPortTests
{
    [Fact]
    public void Product_roster_includes_reviewed_Foundation_tools_and_refuses_withdrawn_names()
    {
        using var fixture = new GuardFixture();
        var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.RepositoryRoot, "manifest", "contracts", "v4", "baseline", "21.json")))!.AsObject();
        var tools = contract["tools"]!.AsArray().Select(node => node!.AsObject()).ToArray();
        Assert.Equal(tools.Length, (int)fixture.Manifest["toolCount"]!);
        Assert.Equal(tools.Select(tool => (string)tool["name"]!).Order(),
            fixture.Manifest["tools"]!.AsArray().Select(tool => (string)tool!["name"]!).Order());
        foreach (var tool in tools)
            Assert.Equal(tool["inputSchema"]!["properties"]!.AsObject().Select(pair => pair.Key).Order(),
                fixture.Entry((string)tool["name"]!)["parameters"]!.AsArray().Select(parameter => (string)parameter!).Order());
        var additions = new Dictionary<string, string> {
            ["CreatePlcTag"] = "WRITE", ["CreatePlcTagTable"] = "WRITE", ["CreatePlcUserConstant"] = "WRITE",
            ["GetPortalConnectionReadiness"] = "READ", ["ListPlcSystemConstants"] = "READ", ["ListPlcTags"] = "READ",
            ["ListPlcUserConstants"] = "READ", ["PlanPlcExternalSourceImport"] = "OFFLINE"
        };
        foreach (var (name, operation) in additions)
        {
            Assert.Equal(operation, (string?)fixture.Entry(name)["operation"]);
            fixture.ExpectAllow(fixture.Call(name, new JsonObject { ["dryRun"] = true }));
            fixture.ExpectAllow(fixture.Call("CallTool", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["dryRun"] = true } }));
            if (operation == "WRITE")
                fixture.ExpectDeny(fixture.Call(name, new JsonObject { ["dryRun"] = false }, ("TIA_MCP_GUARD_DENY_OPERATIONS", "WRITE")), name);
        }
        var restored = new Dictionary<string, string> {
            ["ConnectIsolatedPortal"] = "SESSION", ["BuildProjectScaffold"] = "WRITE", ["RetrieveProjectArchive"] = "WRITE",
            ["SaveProjectCopy"] = "FILE", ["ManageMultiuserSession"] = "WRITE"
        };
        foreach (var (name, operation) in restored)
        {
            Assert.Equal(operation, (string?)fixture.Entry(name)["operation"]);
            fixture.ExpectAllow(fixture.Call(name, new JsonObject()));
            fixture.ExpectAllow(fixture.Call("CallTool", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject() }));
            var arguments = new JsonObject();
            if (fixture.Entry(name)["parameters"]!.AsArray().Any(parameter => (string?)parameter == "dryRun"))
                arguments["dryRun"] = false;
            fixture.ExpectDeny(fixture.Call(name, arguments, ("TIA_MCP_GUARD_DENY_OPERATIONS", operation)), name);
            fixture.ExpectDeny(fixture.Call("CallTool", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() },
                ("TIA_MCP_GUARD_DENY_OPERATIONS", operation)), name);
        }
        foreach (var name in new[] { "ConnectProject", "CreatePlcFuture" })
        {
            fixture.ExpectDeny(fixture.Call(name, new JsonObject(), ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")), "tool list is out of date");
            fixture.ExpectDeny(fixture.Call("CallTool", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject() }), name);
        }
    }

    [Fact]
    public void Current_roster_and_preview_cases_match_the_pretooluse_policy()
    {
        using var fixture = new GuardFixture();
        var profiles = fixture.Profiles;
        string watch = profiles.Single("SetWatchTableModifyValue")["currentName"]!.GetValue<string>();
        string write = profiles.Single("WritePlcWebVars")["currentName"]!.GetValue<string>();
        string renamedWrite = profiles.Single("UnifiedOpenPipeRequest")["currentName"]!.GetValue<string>();
        string bridge = profiles.Single("CallTool")["currentName"]!.GetValue<string>();
        string previewTool = profiles.Single("PreflightToolCall")["currentName"]!.GetValue<string>();
        string readBatch = profiles.Single("ReadToolBatch")["currentName"]!.GetValue<string>();
        string previewBatch = profiles.Single("PreviewToolBatch")["currentName"]!.GetValue<string>();
        string applyBatch = profiles.Single("ApplyToolBatch")["currentName"]!.GetValue<string>();
        string session = profiles.Single("GetState")["currentName"]!.GetValue<string>();
        string execute = profiles.Single("CompileSoftware")["currentName"]!.GetValue<string>();

        foreach (var row in fixture.OnlineRows)
        {
            string target = row["currentName"]!.GetValue<string>();
            Assert.Equal("ONLINE-WRITE", fixture.Entry(target)["operation"]!.GetValue<string>());
            fixture.ExpectDeny(fixture.Call(target, new JsonObject { ["dryRun"] = false }), target);
            fixture.ExpectAllow(fixture.Call(target, new JsonObject { ["dryRun"] = false }, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        }

        Assert.NotEqual("UnifiedOpenPipeRequest", renamedWrite);
        foreach (bool dryRun in new[] { false, true })
        {
            var arguments = new JsonObject { ["dryRun"] = dryRun };
            string direct = fixture.Call(renamedWrite, arguments);
            string bridged = fixture.Call(bridge, new JsonObject { ["name"] = renamedWrite, ["arguments"] = arguments.DeepClone() });
            if (dryRun)
            {
                fixture.ExpectAllow(direct);
                fixture.ExpectAllow(bridged);
            }
            else
            {
                fixture.ExpectDeny(direct, renamedWrite);
                fixture.ExpectDeny(bridged, renamedWrite);
            }
            fixture.ExpectDeny(fixture.Call(applyBatch, new JsonObject
            {
                ["operations"] = new JsonArray(new JsonObject { ["name"] = renamedWrite, ["arguments"] = arguments.DeepClone() })
            }), renamedWrite);
            fixture.ExpectAllow(fixture.Call(previewBatch, new JsonObject
            {
                ["operations"] = new JsonArray(new JsonObject { ["name"] = renamedWrite, ["arguments"] = arguments.DeepClone() })
            }));
        }

        fixture.ExpectDeny(fixture.Call(watch, new JsonObject()), watch);
        fixture.ExpectDeny(fixture.Call(watch, new JsonObject { ["dryRun"] = true }), watch);
        fixture.ExpectAllow(fixture.Call(watch, new JsonObject(), ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject { ["name"] = watch, ["arguments"] = new JsonObject { ["dryRun"] = true } }), watch);

        var secretArguments = new JsonObject
        {
            ["dryRun"] = false,
            ["password"] = "hidden-password-123",
            ["nested"] = new JsonObject { ["credentials"] = "hidden-nested-456", ["items"] = new JsonArray(new JsonObject { ["token"] = "hidden-token-789" }) }
        };
        fixture.ExpectDeny(fixture.Call(write, secretArguments), write);
        fixture.ExpectAllow(fixture.Call(write, secretArguments, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        int auditCount = fixture.AuditLines().Count;
        fixture.ExpectAllow(fixture.Call(write, secretArguments, ("TIA_MCP_WRITE_GUARD", "0")));
        Assert.Equal(auditCount, fixture.AuditLines().Count);
        fixture.ExpectAllow(fixture.Call(write, new JsonObject { ["dryRun"] = true }));
        fixture.ExpectAllow(fixture.Call(write, new JsonObject()));
        foreach (JsonNode representation in new JsonNode[] { JsonValue.Create(false)!, JsonValue.Create("false")!, JsonValue.Create(0)! })
            fixture.ExpectDeny(fixture.Call(write, new JsonObject { ["dryRun"] = representation.DeepClone() }), write);

        foreach (JsonObject arguments in new[] { new JsonObject { ["dryRun"] = false }, new JsonObject { ["dryRun"] = true }, new JsonObject() })
        {
            string result = fixture.Call(bridge, new JsonObject { ["name"] = write, ["arguments"] = arguments.DeepClone() });
            if (arguments["dryRun"]?.GetValueKind() == System.Text.Json.JsonValueKind.False) fixture.ExpectDeny(result, write);
            else fixture.ExpectAllow(result);
        }
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject
        {
            ["name"] = write, ["arguments"] = new JsonObject { ["dryRun"] = false }, ["argumentsJson"] = "{\"dryRun\":true}"
        }), write);
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject { ["name"] = write, ["arguments"] = "{\"dryRun\":true}" }), "arguments must be a V4 object");
        fixture.ExpectAllow(fixture.Call(bridge, new JsonObject { ["name"] = write, ["arguments"] = secretArguments.DeepClone() }, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        fixture.ExpectAllow(fixture.Call(previewTool, new JsonObject { ["name"] = write, ["arguments"] = secretArguments.DeepClone() }));

        var reads = new JsonArray(new JsonObject { ["name"] = session, ["arguments"] = new JsonObject() });
        var writeOperation = new JsonObject { ["name"] = write, ["arguments"] = secretArguments.DeepClone() };
        fixture.ExpectAllow(fixture.Call(readBatch, new JsonObject { ["operations"] = reads.DeepClone() }));
        fixture.ExpectDeny(fixture.Call(readBatch, new JsonObject { ["operations"] = new JsonArray(reads[0]!.DeepClone(), writeOperation.DeepClone()), ["dryRun"] = true }), write);
        fixture.ExpectAllow(fixture.Call(readBatch, new JsonObject { ["operations"] = new JsonArray(reads[0]!.DeepClone(), writeOperation.DeepClone()) }, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        fixture.ExpectAllow(fixture.Call(previewBatch, new JsonObject { ["operations"] = new JsonArray(reads[0]!.DeepClone(), writeOperation.DeepClone()) }));
        fixture.ExpectDeny(fixture.Call(applyBatch, new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["name"] = write, ["arguments"] = new JsonObject { ["dryRun"] = true } }) }), write);
        fixture.ExpectDeny(fixture.Call(applyBatch, new JsonObject { ["token"] = "hidden-plan-012" }), "opaque stored plan");
        fixture.ExpectAllow(fixture.Call(applyBatch, new JsonObject { ["token"] = "hidden-plan-012" }, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject { ["name"] = applyBatch, ["arguments"] = new JsonObject { ["token"] = "hidden-plan-012" } }), "opaque stored plan");
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject { ["name"] = readBatch, ["arguments"] = new JsonObject { ["operations"] = new JsonArray(reads[0]!.DeepClone(), writeOperation.DeepClone()) } }), write);
        fixture.ExpectDeny(fixture.Call(execute, new JsonObject { ["dryRun"] = false }, ("TIA_MCP_GUARD_DENY_OPERATIONS", "EXECUTE")), execute);

        var lines = fixture.AuditLines();
        Assert.True(lines.Count >= 20);
        string joined = string.Join("\n", lines);
        Assert.DoesNotContain("hidden-password-", joined);
        Assert.DoesNotContain("hidden-nested-", joined);
        Assert.DoesNotContain("hidden-token-", joined);
        Assert.DoesNotContain("hidden-plan-", joined);
        Assert.DoesNotContain(lines.Select(line => line.RootElement.GetProperty("tool").GetString()), name => name == session);
        Assert.Contains(lines, line => line.RootElement.GetProperty("tool").GetString() == write && line.RootElement.GetProperty("operation").GetString() == "ONLINE-WRITE" && !line.RootElement.GetProperty("preview").GetBoolean());
        Assert.Contains(lines, line => line.RootElement.GetProperty("tool").GetString() == write && line.RootElement.GetProperty("preview").GetBoolean());
        byte[] bytes = File.ReadAllBytes(fixture.AuditPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf);
    }

    [Fact]
    public void Bridge_unknown_categories_and_manifest_failures_keep_fail_closed_behavior()
    {
        using var fixture = new GuardFixture();
        string bridge = fixture.Profiles.Single("CallTool")["currentName"]!.GetValue<string>();
        string preview = fixture.Profiles.Single("PreflightToolCall")["currentName"]!.GetValue<string>();
        string readBatch = fixture.Profiles.Single("ReadToolBatch")["currentName"]!.GetValue<string>();
        string previewBatch = fixture.Profiles.Single("PreviewToolBatch")["currentName"]!.GetValue<string>();
        string applyBatch = fixture.Profiles.Single("ApplyToolBatch")["currentName"]!.GetValue<string>();
        string session = fixture.Profiles.Single("GetState")["currentName"]!.GetValue<string>();
        string write = fixture.Profiles.Single("WritePlcWebVars")["currentName"]!.GetValue<string>();
        const string unknown = "UnknownV4Tool";
        foreach (var allow in new[] { Array.Empty<(string, string)>(), new[] { ("TIA_MCP_ALLOW_ONLINE_WRITE", "1") } })
        {
            string result = fixture.Call(unknown, new JsonObject(), allow);
            fixture.ExpectDeny(result, unknown);
            Assert.Contains("tool list is out of date", fixture.Reason(result));
        }
        fixture.ExpectAllow(fixture.Call(unknown, new JsonObject(), ("TIA_MCP_WRITE_GUARD", "0")));
        fixture.ExpectDeny(fixture.Call(bridge, new JsonObject { ["name"] = unknown, ["arguments"] = new JsonObject() }), unknown);
        fixture.ExpectDeny(fixture.Call(preview, new JsonObject { ["name"] = unknown, ["arguments"] = new JsonObject() }), unknown);
        foreach (string batch in new[] { readBatch, previewBatch, applyBatch })
            fixture.ExpectDeny(fixture.Call(batch, new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["name"] = unknown, ["arguments"] = new JsonObject() }) }, ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")), unknown);
        fixture.ExpectAllow(fixture.Call(session, new JsonObject()));
        fixture.ExpectAllow(GuardHost.ProcessInput("{\"tool_name\":\"mcp__other__Thing\",\"tool_input\":{}}", fixture.Environment, fixture.RepositoryRoot));
        fixture.ExpectAllow(GuardHost.ProcessInput("not json", fixture.Environment, fixture.RepositoryRoot));

        var oldNames = fixture.ReadManifest();
        var finalManifest = (JsonObject)oldNames.DeepClone();
        foreach (var row in fixture.Profiles.Rows)
        {
            var entry = fixture.Entry(row["currentName"]!.GetValue<string>());
            entry["name"] = row["name"]!.DeepClone();
        }
        string finalRoot = Path.Combine(fixture.Work, "final-plugin");
        Directory.CreateDirectory(Path.Combine(finalRoot, "manifest"));
        File.WriteAllText(Path.Combine(finalRoot, "manifest", "tools-list.json"), finalManifest.ToJsonString());
        foreach (var row in fixture.OnlineRows)
        {
            string target = row["name"]!.GetValue<string>();
            fixture.ExpectDeny(fixture.Call(target, new JsonObject { ["dryRun"] = false }, ("CLAUDE_PLUGIN_ROOT", finalRoot)), target);
            fixture.ExpectAllow(fixture.Call(target, new JsonObject { ["dryRun"] = false }, ("CLAUDE_PLUGIN_ROOT", finalRoot), ("TIA_MCP_ALLOW_ONLINE_WRITE", "1")));
        }

        string broken = Path.Combine(fixture.Work, "broken-plugin");
        Directory.CreateDirectory(Path.Combine(broken, "manifest"));
        File.WriteAllText(Path.Combine(broken, "manifest", "tools-list.json"), "not json");
        string failed = fixture.Call(write, new JsonObject { ["dryRun"] = false }, ("CLAUDE_PLUGIN_ROOT", broken));
        fixture.ExpectDeny(failed, write);
        Assert.Contains("cannot be classified", fixture.Reason(failed));
        fixture.ExpectDeny(fixture.Call(write, new JsonObject { ["dryRun"] = false }, ("CLAUDE_PLUGIN_ROOT", Path.Combine(fixture.Work, "missing"))), write);
        fixture.ExpectDeny(fixture.Call(write, new JsonObject { ["dryRun"] = false }, ("TIA_MCP_AUDIT_LOG", fixture.Work)), write);
    }

    [Fact]
    public void Built_executable_reads_real_stdin_and_writes_the_hook_protocol_to_stdout()
    {
        using var fixture = new GuardFixture();
        string tool = fixture.Profiles.Single("WritePlcWebVars")["currentName"]!.GetValue<string>();
        string executable = Path.Combine(fixture.RepositoryRoot, "src", "Tools", "WriteGuard", "bin", "Release", "net10.0", "TiaMcp.WriteGuard.exe");
        Assert.True(File.Exists(executable), "Build the referenced hook project before running the suite: " + executable);
        var payload = JsonSerializer.Serialize(new
        {
            session_id = "guard-test", cwd = "C:/w", tool_name = "mcp__tia-portal__" + tool,
            tool_input = new { dryRun = false }
        });
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = fixture.RepositoryRoot,
        };
        foreach (var pair in fixture.Environment) start.Environment[pair.Key] = pair.Value;
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.Write(payload);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000), "The write guard exceeded its 15 second startup/decision budget.");
        watch.Stop();
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(stderr);
        fixture.ExpectDeny(stdout, tool);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Cold hook start exceeded the Claude Code 20 second timeout: " + watch.Elapsed);
        Console.WriteLine("WRITE_GUARD_COLD_START_MS=" + (long)watch.Elapsed.TotalMilliseconds);
        File.WriteAllText(Path.Combine(fixture.Work, "cold-start-ms.txt"), ((long)watch.Elapsed.TotalMilliseconds).ToString());
    }

    private sealed class GuardFixture : IDisposable
    {
        public string RepositoryRoot { get; } = FindRoot();
        public string Work { get; } = Path.Combine(FindRoot(), "bin-build", "P6-50", "write-guard-tests", Guid.NewGuid().ToString("N"));
        public string AuditPath { get; }
        public IReadOnlyDictionary<string, string?> Environment { get; }
        public JsonObject Manifest { get; }
        public ProfileCatalog Profiles { get; }
        public JsonArray OnlineRows { get; }

        public GuardFixture()
        {
            Directory.CreateDirectory(Work);
            AuditPath = Path.Combine(Work, "audit.jsonl");
            Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CLAUDE_PLUGIN_ROOT"] = RepositoryRoot,
                ["TIA_MCP_AUDIT_LOG"] = AuditPath,
                ["TIA_MCP_GUARD_DEBUG"] = "",
                ["TIA_MCP_ALLOW_ONLINE_WRITE"] = "",
                ["TIA_MCP_WRITE_GUARD"] = "",
                ["TIA_MCP_GUARD_DENY_OPERATIONS"] = "",
                ["LOCALAPPDATA"] = Work,
            };
            Manifest = ReadManifest();
            Profiles = ProfileCatalog.Read(RepositoryRoot);
            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "checks", "write-guard-operations-v3.3.0.json")));
            var releases = Profiles.Rows;
            OnlineRows = new JsonArray(releases.Where(row => baseline.RootElement.GetProperty("operations").TryGetProperty(row["sourceName"]!.GetValue<string>(), out var operation) && operation.GetString() == "ONLINE-WRITE")
                .Select(row => row.DeepClone()).ToArray());
            Assert.NotEmpty(OnlineRows);
        }

        public JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "manifest", "tools-list.json")))!.AsObject();
        public JsonObject Entry(string name) => Manifest["tools"]!.AsArray().Select(node => node!.AsObject()).Single(entry => entry["name"]!.GetValue<string>().Equals(name, StringComparison.OrdinalIgnoreCase));
        public string Call(string name, JsonNode arguments, params (string Key, string Value)[] overrides)
        {
            var env = Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in overrides) env[key] = value;
            string input = JsonSerializer.Serialize(new
            {
                session_id = "guard-test", cwd = "C:/w", tool_name = "mcp__tia-portal__" + name,
                tool_input = arguments,
            });
            return GuardHost.ProcessInput(input, env, RepositoryRoot);
        }
        public IReadOnlyList<JsonDocument> AuditLines() => File.Exists(AuditPath)
            ? File.ReadAllLines(AuditPath, Encoding.UTF8).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line)).ToArray()
            : Array.Empty<JsonDocument>();
        public string Reason(string output) => JsonNode.Parse(output)?["hookSpecificOutput"]?["permissionDecisionReason"]?.GetValue<string>() ?? "";
        public void ExpectDeny(string output, string reasonPart)
        {
            Assert.NotEmpty(output);
            Assert.Equal("deny", JsonNode.Parse(output)?["hookSpecificOutput"]?["permissionDecision"]?.GetValue<string>());
            Assert.Contains(reasonPart, Reason(output), StringComparison.OrdinalIgnoreCase);
        }
        public void ExpectAllow(string output) => Assert.Empty(output);
        public void Dispose()
        {
            foreach (var line in AuditLines()) line.Dispose();
            string resolved = Path.GetFullPath(Work);
            string root = Path.GetFullPath(Path.Combine(RepositoryRoot, "bin-build")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private sealed class ProfileCatalog
    {
        public JsonArray Rows { get; }
        private ProfileCatalog(JsonArray rows) => Rows = rows;
        public JsonObject Single(string sourceName) => Rows.Select(node => node!.AsObject()).Single(row => row["sourceName"]!.GetValue<string>() == sourceName);
        public static ProfileCatalog Read(string root)
        {
            var document = XDocument.Load(Path.Combine(root, "src", "Logic", "ModelContextProtocol", "ToolProfiles.resx"));
            string json = document.Descendants("data").Single(item => (string?)item.Attribute("name") == "Catalog").Element("value")!.Value;
            var catalog = JsonNode.Parse(json)!.AsObject();
            return new ProfileCatalog(catalog["releases"]!["21"]!.AsArray());
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Version.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
