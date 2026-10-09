using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class DeveloperChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private sealed class FakeAddress { public string Address { get; set; } = ""; }
    private sealed class FakeTarget
    {
        public string Name { get; set; } = "";
        public List<FakeAddress> Addresses { get; } = new List<FakeAddress>();
    }
    private sealed class FakePcInterface
    {
        public string Name { get; set; } = "";
        public int Number { get; set; }
        public List<FakeAddress> Addresses { get; } = new List<FakeAddress>();
        public List<FakeTarget> TargetInterfaces { get; } = new List<FakeTarget>();
    }
    private sealed class FakeMode
    {
        public string Name { get; set; } = "PN/IE";
        public List<FakePcInterface> PcInterfaces { get; } = new List<FakePcInterface>();
    }
    private sealed class FakeConnectionConfiguration
    {
        public List<FakeMode> Modes { get; } = new List<FakeMode>();
    }

    private static int checks;
    private static int failures;

    private static void Check(bool condition, string description)
    {
        checks++;
        if (condition) Console.WriteLine("PASS " + description);
        else { failures++; Console.Error.WriteLine("FAIL " + description); }
    }

    internal static int MatchPlcName(Assembly server)
    {
        Reset();
        var type = FindType(server, "TiaMcpServer.Siemens.Guard");
        var method = type.GetMethod("MatchPlcName", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, "MatchPlcName");
        void Match(string label, string[] available, string token, string? expected)
        {
            var got = method.Invoke(null, new object[] { available, token }) as string;
            Check(string.Equals(got, expected, StringComparison.Ordinal),
                $"{label,-22} '{token}' -> {(got == null ? "<null>" : got)}");
        }

        Match("single exact", new[] { "PLC_1" }, "PLC_1", "PLC_1");
        Match("single case", new[] { "PLC_1" }, "plc_1", "PLC_1");
        Match("single trim", new[] { "PLC_1" }, " PLC_1 ", "PLC_1");
        Match("single wrong name", new[] { "PLC_1" }, "garbage", null);
        Match("single substr", new[] { "PLC_1" }, "plc", null);
        Match("single omitted", new[] { "PLC_1" }, "", "PLC_1");

        var multi = new[] { "MainPLC", "SafetyPLC", "PLC_1" };
        Match("multi exact", multi, "SafetyPLC", "SafetyPLC");
        Match("multi case", multi, "safetyplc", "SafetyPLC");
        Match("multi trim", multi, " SafetyPLC ", "SafetyPLC");
        Match("multi substr", multi, "Safety", null);
        Match("multi ambiguous", multi, "PLC", null);
        Match("multi not-found", multi, "NoSuch", null);
        Match("multi empty", multi, "", null);

        var two = new[] { "PLC_1", "PLC_2" };
        Match("two exact", two, "PLC_2", "PLC_2");
        Match("two digit", two, "1", null);
        Match("two ambiguous", two, "PLC", null);
        Match("empty list", Array.Empty<string>(), "PLC_1", null);
        Console.WriteLine($"RESULT: {checks} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    internal static int DownloadRoute(Assembly server, string publicApiDirectory)
    {
        Reset();
        AddResolver(server, publicApiDirectory);
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var serviceType = server.GetType("TiaMcpServer.Siemens.Services.OnlineDownloadService", true)!;
        var session = FormatterServices.GetUninitializedObject(portal);
        var constructor = serviceType.GetConstructors(All).FirstOrDefault()
            ?? throw new MissingMethodException(serviceType.FullName, ".ctor");
        var service = constructor.Invoke(new[] { session });
        var enumMethod = serviceType.GetMethod("EnumerateDownloadRoutes", All) ?? throw new MissingMethodException("EnumerateDownloadRoutes");
        var scoreMethod = serviceType.GetMethod("ScoreDownloadRoutes", All) ?? throw new MissingMethodException("ScoreDownloadRoutes");
        var selectMethod = serviceType.GetMethod("SelectDownloadRoute", All) ?? throw new MissingMethodException("SelectDownloadRoute");

        var cpu = Target("PROFINET interface_1", "192.168.0.1");
        var multiNic = Config(
            Pc("Realtek WiFi 6", 1, "192.168.31.77", cpu),
            Pc("Meta Tunnel (FlClash)", 2, "198.18.0.1", cpu),
            Pc("PLCSIM Virtual Ethernet Adapter", 3, "192.168.0.241", cpu));
        object? Run(string label, string expected, object config, string? filter, string? targetIp)
        {
            var routes = enumMethod.Invoke(service, new[] { config })!;
            scoreMethod.Invoke(null, new object?[] { routes, targetIp });
            foreach (var route in (IEnumerable)routes)
            {
                var routeType = route!.GetType();
                var score = routeType.GetField("Score", All)?.GetValue(route);
                var description = routeType.GetMethod("Describe", All)?.Invoke(route, null);
                Console.WriteLine($"  score={score,-2} {description}");
            }
            var selected = selectMethod.Invoke(service, new object?[] { config, filter, targetIp })!;
            var type = selected.GetType();
            var error = type.GetField("Error", All)!.GetValue(selected) as string;
            var result = error == null ? type.GetField("Description", All)!.GetValue(selected) as string : "ERROR: " + error;
            var pattern = "^" + Regex.Escape(expected).Replace("\\*", ".*") + "$";
            Check(result != null && Regex.IsMatch(result, pattern, RegexOptions.IgnoreCase), label);
            return result;
        }

        Run("auto-pick takes the adapter in the CPU subnet", "*PLCSIM*", multiNic, null, null);
        Run("explicit targetIpAddress keeps that pick", "*PLCSIM*", multiNic, null, "192.168.0.1");
        Run("explicit pgPcInterface overrides the ranking", "*Realtek*", multiNic, "realtek", null);
        Run("unknown pgPcInterface lists what IS available", "ERROR: No PG/PC interface matches 'eth42'.*PLCSIM*", multiNic, "eth42", null);
        Run("unreachable target IP lists what IS available", "ERROR: No download route reaches target IP '10.0.0.9'.*PLCSIM*", multiNic, null, "10.0.0.9");

        var noMatch = Config(Pc("Realtek WiFi 6", 1, "192.168.31.77", cpu), Pc("Meta Tunnel", 2, "198.18.0.1", cpu));
        Run("nothing to distinguish -> first-enumerated wins", "*Realtek*", noMatch, null, null);

        Console.WriteLine("  -- sentinel (must FAIL) --");
        var beforeFailures = failures;
        Run("sentinel: impossible expectation", "*NoSuchAdapter*", multiNic, null, null);
        if (failures == beforeFailures + 1)
        {
            checks++;
            failures--;
            Console.WriteLine("PASS sentinel failed as required");
        }
        else
        {
            Check(false, "sentinel did not fail — harness is broken");
        }
        Console.WriteLine($"{checks - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    internal static int MigrationReadAssembly(Assembly server, string publicApiDirectory)
    {
        Reset();
        AddResolver(server, publicApiDirectory);
        var catalogType = server.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var catalog = catalogType.GetProperty("Engine", All)!.GetValue(null)!;
        var methods = (IEnumerable)catalogType.GetProperty("Methods", All)!.GetValue(catalog)!;
        var names = new[] { "ListUnifiedGlobalScripts", "GetUnifiedGlobalScript", "ListUnifiedTagDefinitions", "GetUnifiedScreenBranch", "GetUnifiedLibraryType", "GetUnifiedFaceplateInstance", "ListUnifiedLibraryFolderEntries", "ReleaseUnifiedReadCursor" };
        foreach (var name in names)
        {
            var found = methods.Cast<object>().Where(entry => string.Equals(Get(entry, "Key") as string, name, StringComparison.Ordinal)).ToArray();
            Check(found.Length == 1, "exactly one catalog entry: " + name);
            if (found.Length != 1) continue;
            var method = Get(found[0], "Value") as MethodInfo;
            Check(method != null, "catalog method resolves: " + name);
            if (method == null) continue;
            var hasAttribute = CustomAttributeData.GetCustomAttributes(method).Any(attribute => attribute.AttributeType.Name == "McpServerToolAttribute");
            Check(hasAttribute, "MCP registration is present: " + name);
        }

        var reader = FindType(server, "TiaMcpServer.Siemens.JavaScriptEvidence", false);
        if (reader == null)
        {
            var logic = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(server.Location)!, "TiaMcp.Logic.dll"));
            reader = logic.GetType("TiaMcpServer.Siemens.JavaScriptEvidence", true)!;
        }
        var analyze = reader.GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)!;
        const string source = "import * as C from 'Colors'; export function f(x) { return Tags(x+'.v').Read(); }";
        var result = analyze.Invoke(null, new object[] { source, "fixture.js" })!;
        var nodeText = (string)result.GetType().GetMethod("ToJsonString", new[] { typeof(JsonSerializerOptions) })!.Invoke(result, new object?[] { null })!;
        var data = JsonDocument.Parse(nodeText).RootElement;
        Check(data.GetProperty("parseComplete").GetBoolean() && data.GetProperty("bodyReadSuccess").GetBoolean()
            && data.GetProperty("rawText").GetString() == source && data.GetProperty("functions").GetArrayLength() == 1,
            "compiled EXE preserves and parses the original script body");
        Check(data.GetProperty("references")[0].GetProperty("resolution").GetString() == "runtimeExpressionUnresolved",
            "compiled EXE keeps dynamic references unresolved");
        Console.WriteLine($"PASS actual EXE: {checks} migration assembly checks passed under .NET Framework. FileVersion={FileVersionInfo.GetVersionInfo(server.Location).FileVersion}");
        return failures == 0 ? 0 : 1;
    }

    internal static int EcosystemAssembly(Assembly server, string publicApiDirectory, string[] arguments)
    {
        Reset();
        var skipPdf = arguments.Contains("--skip-pdf", StringComparer.OrdinalIgnoreCase);
        var skipCompanion = arguments.Contains("--skip-companion", StringComparer.OrdinalIgnoreCase);
        AddResolver(server, publicApiDirectory);
        var runtime = Path.GetDirectoryName(server.Location)!;
        var repo = FindRepositoryRoot(runtime);
        var previousBundleRoot = Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT");
        var previousDiagnostics = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", repo);
        var scratch = Path.Combine(repo, "bin-build", "ecosystem-assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var diagnostics = Path.Combine(scratch, "diagnostics");
        Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", diagnostics);
        try
        {
            var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
            Check(typeof(IDisposable).IsAssignableFrom(portal), "Portal cleanup is registered with IDisposable");
            var engineType = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
            var logicPath = Path.Combine(runtime, "TiaMcp.Logic.dll");
            var logic = Assembly.LoadFrom(logicPath);
            var bridge = engineType.GetMethods(All).First(m => m.Name == "DispatchNestedTool");
            var argumentType = bridge.GetParameters()[1].ParameterType.GetGenericArguments()[0];
            var parse = logic.GetType("TiaMcp.Logic.V4.V4Json", true)!.GetMethod("ParseInput", BindingFlags.NonPublic | BindingFlags.Static)!;

            JsonObject Call(string name, object args)
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(args);
                if (EngineSurface.IsHostTool(server, name)) return HostPortRunner.Call(server, name, JsonNode.Parse(json)!.AsObject());
                var parsed = parse.Invoke(null, new object[] { json });
                var typed = Activator.CreateInstance(argumentType, new[] { parsed })!;
                var result = bridge.Invoke(null, new[] { (object)name, typed })!;
                var textValues = ((IEnumerable)Get(result, "Content")!).Cast<object>()
                    .Select(block => Get(block, "Text") as string).Where(text => text != null).ToArray();
                if (textValues.Length != 1) throw new InvalidDataException("Expected one V4 TextContent");
                var decoded = JsonNode.Parse(textValues[0]!)!.AsObject();
                var ok = decoded["ok"]?.GetValue<bool>() ?? false;
                if (decoded["schemaVersion"]?.GetValue<int>() != 4 || decoded["meta"] == null ||
                    !decoded.ContainsKey("data") || !decoded.ContainsKey("error") ||
                    (ok && decoded["error"] != null) || (!ok && decoded["error"] == null) ||
                    Convert.ToBoolean(Get(result, "IsError")) == ok)
                    throw new InvalidDataException("Invalid V4 envelope");
                File.WriteAllText(Path.Combine(scratch, "response-" + checks + "-" + name + ".json"), decoded.ToJsonString(), new UTF8Encoding(false));
                return decoded;
            }

            JsonNode Data(JsonObject value) => value["data"]!;
            JsonNode Meta(JsonObject value) => value["meta"]!;
            bool Ok(JsonObject value) => value["ok"]!.GetValue<bool>();
            void CheckOk(bool value, string description)
            {
                Check(value, description);
                if (!value) throw new InvalidOperationException(description);
            }

            var guidance = Call("GetOpennessGuidance", new Dictionary<string, object>());
            CheckOk(Ok(guidance) && Data(guidance)["total"]!.GetValue<int>() >= 32, "Guidance lookup succeeds with at least 32 entries");
            var batch = Call("RunReadOnlyToolBatch", new { operations = new[] { new { name = "GetOpennessGuidance", arguments = new { query = "threading" } } } });
            CheckOk(Ok(batch) && Data(batch)["items"]!.AsArray().Count == 1, "Read batch retains its inner result");
            var bad = Call("RunReadOnlyToolBatch", new { operations = new[] { new { name = "SaveProject", arguments = new { } } } });
            CheckOk(!Ok(bad), "Read batch refuses a write");
            var native = Call("RunReadOnlyToolBatch", new { operations = new[] { new { name = "GetPlcCrossReferences", arguments = new { } } } });
            CheckOk(!Ok(native), "Native cross references are refused by the read batch");
            var apply = Call("ApplyToolBatch", new { token = "not-a-real-token" });
            CheckOk(!Ok(apply), "Unknown write token is refused");
            var official = Call("GetToolUsage", new { query = "", limit = 1 });
            CheckOk(Ok(official) && Data(official)["totalMatches"]!.GetValue<int>() > 0 && Data(official)["referenceOnly"]!.GetValue<bool>(), "Official references are reachable through the unified library");
            foreach (var tool in new[] { "SaveProject", "CompilePlcSoftware", "ApplyToolBatch", "RunPlcCompanionTool", "ManagePlcGitRepository" })
            {
                var refused = Call("RunToolTransaction", new { calls = new[] { new { name = tool, arguments = new { } } }, text = "Offline refusal", dryRun = false, confirmChange = true });
                CheckOk(!Ok(refused) && Meta(refused)["execution"]!.GetValue<string>() == "not-started", "Transaction refuses " + tool + " before acquiring native state");
            }
            var validTransaction = Call("RunToolTransaction", new { calls = new[] { new { name = "CreatePlcTypeGroup", arguments = new { softwarePath = "PLC_Offline", groupPath = "Test" } } }, text = "Preview only" });
            CheckOk(Ok(validTransaction) && !(Data(validTransaction)["mayHaveChanged"]?.GetValue<bool>() ?? false), "Supported transaction preview does not write");
            var badTransaction = Call("RunToolTransaction", new { calls = new[] { new { name = "CreatePlcTypeGroup", arguments = new { softwarePath = "PLC_Offline" } } }, text = "Missing argument" });
            CheckOk(!Ok(badTransaction) && badTransaction["error"]?["code"]?.GetValue<string>() == "INVALID_ARGUMENT", "Transaction preflights every call");
            var lad = Call("BuildPlcAliasAlarmLad", new { blockName = "FC_Offline", blockNumber = 701, rows = new[] { new { source = new[] { "Input" }, destination = new[] { "DB", "Output" } } } });
            CheckOk(Ok(lad) && Data(lad)["xml"]!.GetValue<string>().Contains("Contact"), "Boolean LAD composer returns contact XML");

            var left = Path.Combine(scratch, "before.xml");
            var right = Path.Combine(scratch, "after.xml");
            var xml = Data(lad)["xml"]!.GetValue<string>();
            File.WriteAllText(left, xml, new UTF8Encoding(false));
            File.WriteAllText(right, xml.Replace("Name=\"Input\"", "Name=\"OtherInput\""), new UTF8Encoding(false));
            var report = Path.Combine(scratch, "diff.html");
            var diff = Call("RenderPlcVisualDiff", new { leftFilePath = left, rightFilePath = right, outputPath = report });
            CheckOk(Ok(diff) && File.Exists(report) && Data(diff)["state"]!.GetValue<string>() == "Changed", "Visual diff detects a changed operand");
            var overwrite = Call("RenderPlcVisualDiff", new { leftFilePath = left, rightFilePath = right, outputPath = report });
            CheckOk(!Ok(overwrite), "Visual diff refuses to overwrite an existing report");
            var quality = Call("AuditEngineeringExports", new { directoryPath = scratch, blockNamePattern = "^Expected_" });
            CheckOk(Ok(quality) && !Data(quality)["qualityPassed"]!.GetValue<bool>(), "Quality failure is distinct from invocation success");
            if (skipPdf) Console.WriteLine("SKIP PDF renderer: explicit offline run without the companion ReportLab environment");
            else
            {
                var pdfPath = Path.Combine(scratch, "audit.pdf");
                var pdf = Call("AuditEngineeringExports", new { directoryPath = scratch, reportPath = pdfPath });
                CheckOk(Ok(pdf) && File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 1000, "Quality PDF report is generated");
            }

            var template = Path.Combine(scratch, "template.xml");
            File.WriteAllText(template, "<Document><Name>{{Name}}</Name></Document>", new UTF8Encoding(false));
            var templateRows = new[] { new { fileName = "generated.xml", values = new { Name = "A & B" } } };
            var preview = Call("InstantiatePlcTemplates", new { templatePath = template, rows = templateRows, outputDirectory = scratch });
            CheckOk(Ok(preview) && !File.Exists(Path.Combine(scratch, "generated.xml")), "Template preview does not write a file");
            var expand = Call("InstantiatePlcTemplates", new { templatePath = template, rows = templateRows, outputDirectory = scratch, dryRun = false });
            CheckOk(Ok(expand) && Data(expand)["files"]![0]!["written"]!.GetValue<bool>(), "Template execution writes output");

            var gitRoot = Path.Combine(scratch, "git");
            Directory.CreateDirectory(gitRoot);
            var init = Run("git", new[] { "init", "--quiet", gitRoot }, repo);
            if (init.ExitCode != 0) throw new InvalidOperationException("Fixture git init failed: " + init.Output);
            Run("git", new[] { "-C", gitRoot, "config", "user.name", "Offline Test" }, repo);
            Run("git", new[] { "-C", gitRoot, "config", "user.email", "offline-test@example.invalid" }, repo);
            File.WriteAllText(Path.Combine(gitRoot, "a.scl"), "FUNCTION A : VOID END_FUNCTION", new UTF8Encoding(false));
            var gitStatus = Call("ManagePlcGitRepository", new { repositoryPath = gitRoot });
            CheckOk(Ok(gitStatus) && Data(gitStatus)["stdout"]!.GetValue<string>().Contains("a.scl"), "Git status reports the fixture file");
            var gitPreview = Call("ManagePlcGitRepository", new { repositoryPath = gitRoot, action = "commit", files = new[] { "a.scl" }, message = "Offline fixture" });
            CheckOk(Ok(gitPreview) && !Data(gitPreview)["executed"]!.GetValue<bool>(), "Git commit preview does not execute");
            CheckOk(!File.Exists(Path.Combine(gitRoot, ".git", "refs", "heads", "master")) && !File.Exists(Path.Combine(gitRoot, ".git", "refs", "heads", "main")), "Git preview creates no commit");
            var history = Call("ManagePlcGitRepository", new { repositoryPath = gitRoot, action = "history" });
            CheckOk(!Ok(history) && Data(history)["exitCode"]!.GetValue<int>() != 0, "Empty fixture history reports missing commits");

            if (skipCompanion) Console.WriteLine("SKIP companion fixtures: explicit run without installed Python dependencies");
            else
            {
                var catalog = Call("RunPlcCompanionTool", new { workingDirectory = scratch });
                CheckOk(Ok(catalog), "Companion catalogue succeeds: " + Data(catalog)["stderr"]);
                var commands = JsonNode.Parse(Data(catalog)["stdout"]!.GetValue<string>())!.AsObject();
                CheckOk(commands["complete"]!.GetValue<bool>() && commands["commands"]!.AsArray().Count >= 49, "Companion command catalogue is complete");
                var help = Call("RunPlcCompanionTool", new { workingDirectory = scratch, mode = "help", arguments = new[] { "code", "lint" } });
                CheckOk(Ok(help) && Data(help)["stdout"]!.GetValue<string>().Contains("Usage:"), "Companion help is available");
                var plan = Call("RunPlcCompanionTool", new { workingDirectory = scratch, mode = "run", arguments = new[] { "trace", "start" } });
                CheckOk(Ok(plan) && !Data(plan)["executed"]!.GetValue<bool>(), "Online companion command remains a dry-run plan");
                var badCommand = Call("RunPlcCompanionTool", new { workingDirectory = scratch, mode = "run", arguments = new[] { "code", "does-not-exist" }, dryRun = false });
                CheckOk(!Ok(badCommand) && Data(badCommand)["exitCode"]!.GetValue<int>() != 0, "Companion reports a nonzero command exit");
            }

            var journal = Directory.GetFiles(diagnostics, "*.jsonl").FirstOrDefault() ?? throw new FileNotFoundException("Invocation journal not found");
            var entries = File.ReadAllLines(journal).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonDocument.Parse(line)).Select(document => document.RootElement.Clone()).ToArray();
            CheckOk(entries.Any(entry => entry.GetProperty("phase").GetString() == "BEFORE") && entries.Any(entry => entry.GetProperty("phase").GetString() == "RETURNED"), "Invocation breadcrumbs include before and returned phases");
            CheckOk(!File.ReadAllText(journal).Contains("offline-test@example.invalid"), "Invocation journal contains no argument data");
            Console.WriteLine($"COMPLETE: {checks} ecosystem assembly checks passed. Artifacts: {scratch}");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT", previousBundleRoot);
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previousDiagnostics);
            Directory.Delete(scratch, true);
        }

        (int ExitCode, string Output) Run(string program, string[] args, string workingDirectory)
        {
            var start = new ProcessStartInfo(program, string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout + stderr);
        }
    }

    internal static int GenerateToolsList(Assembly server, string publicApiDirectory, string outputPath, string packageName,
        string foundationHost, string bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(publicApiDirectory) || string.IsNullOrWhiteSpace(outputPath)
            || !File.Exists(foundationHost) || !Directory.Exists(bundleRoot))
            throw new ArgumentException("generate-tools-list requires PublicAPI directory, output file, FoundationHost and bundle root.");
        Reset();
        AddResolver(server, publicApiDirectory);
        var runtimePath = Path.GetDirectoryName(server.Location)!;
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        var logic = Assembly.LoadFrom(Path.Combine(runtimePath, "TiaMcp.Logic.dll"));
        var type = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var taxonomy = logic.GetType("TiaMcpServer.ModelContextProtocol.ToolTaxonomy", true)!;
        var categoryOf = taxonomy.GetMethod("CategoryOf", All)!;
        var parseTag = taxonomy.GetMethod("For", All)!;
        var operationOf = taxonomy.GetMethod("OperationOf", All)!;
        var categories = new JsonArray();
        foreach (var category in (IEnumerable)taxonomy.GetField("Categories", All)!.GetValue(null)!)
        {
            categories.Add(new JsonObject
            {
                ["key"] = Get(category!, "Key")!.ToString(),
                ["nameZh"] = Get(category!, "NameZh")!.ToString(),
                ["nameEn"] = Get(category!, "NameEn")!.ToString(),
                ["description"] = Get(category!, "Description")!.ToString(),
                ["domains"] = new JsonArray(((IEnumerable)Get(category!, "Domains")!).Cast<object>().Select(item => (JsonNode?)JsonValue.Create(item.ToString())).ToArray())
            });
        }

        // Keep validating the engine recipe library; product examples below use the
        // same schema-aware usage library as FoundationHost, excluding withdrawn tools.
        var engineDescriptors = (IDictionary)type.GetMethod("AllToolDescriptors", All)!.Invoke(null, new object[] { true })!;
        Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> recipeParameters = name => {
            if (engineDescriptors.Contains(name)) return ((IEnumerable)Get(engineDescriptors[name]!, "Parameters")!).Cast<object>()
                .Select(p => new KeyValuePair<string, bool>((string)Get(p, "Name")!, (bool)Get(p, "Required")!)).ToArray();
            if (!EngineSurface.IsHostTool(server, name)) return null;
            return HostPortRunner.Metadata(server, name)["parameters"]!.AsArray().Select(p =>
                new KeyValuePair<string, bool>((string)p!["name"]!, (bool)p["required"]!)).ToArray();
        };
        var recipeErrors = ((IEnumerable)logic.GetType("TiaMcpServer.ModelContextProtocol.ToolRecipes", true)!
            .GetMethod("ValidateAgainst", All)!.Invoke(null, new object[] { recipeParameters })!).Cast<string>().ToArray();
        Check(recipeErrors.Length == 0, recipeErrors.Length == 0 ? "ToolRecipes.ValidateAgainst accepts every recipe" : string.Join("; ", recipeErrors));

        using var product = ProductCatalog(server, foundationHost, bundleRoot, outputPath);
        var productTools = product.RootElement.GetProperty("tools");
        var disciplineRoot = product.RootElement.GetProperty("callDiscipline");
        var previousUndocumented = ReadPreviousUndocumented(outputPath, out var previousProduct);
        var undocumented = disciplineRoot.GetProperty("parametersUndocumented").GetInt32();
        // Compare like rosters during the migration from a raw engine manifest.
        var comparableUndocumented = previousProduct ? undocumented
            : ((JsonObject)type.GetMethod("SchemaHintStatistics", All)!.Invoke(null, null)!)["parametersUndocumented"]!.GetValue<int>();
        Check(previousUndocumented == null || comparableUndocumented <= previousUndocumented.Value,
            previousUndocumented == null || comparableUndocumented <= previousUndocumented.Value
                ? "undocumented parameter count did not increase"
                : $"Parameters without a [Description] went up from {previousUndocumented} to {comparableUndocumented}");

        var catalogType = server.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var catalog = catalogType.GetProperty("Engine", All)!.GetValue(null)!;
        var methods = ((IEnumerable)catalogType.GetProperty("Methods", All)!.GetValue(catalog)!).Cast<object>()
            .ToDictionary(entry => Get(entry, "Key")!.ToString()!, entry => (MethodInfo)Get(entry, "Value")!, StringComparer.Ordinal);
        var usageType = logic.GetType("TiaOpenness.Shared.ToolUsageCatalog", true)!;
        var profiles = (IEnumerable)usageType.GetMethod("ProfileEntries", All)!.Invoke(null, new object[] { release, 4, false })!;
        var profileRows = profiles.Cast<JsonObject>().ToDictionary(row => (string)row["currentName"]!, StringComparer.Ordinal);
        var productNames = productTools.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!).ToArray();
        var rows = new List<(string Name, JsonObject Value)>();
        foreach (var entry in productTools.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString()!;
            var description = entry.GetProperty("description").GetString()!;
            var schema = entry.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties").EnumerateObject().Select(parameter => parameter.Name).ToArray();
            var profile = profileRows[name];
            bool shared = profile["profiles"]!.AsArray().Any(value => (string?)value == "plc-foundation");
            var usage = (JsonObject)usageType.GetMethod("Describe", All)!.Invoke(null, new object?[] {
                name, release, shared ? "plc-foundation" : "full-engine", description,
                JsonNode.Parse(schema.GetRawText())!.AsObject(), null, null, "", productNames, null, null
            })!;
            var example = usage["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            Check(!example.Select(pair => pair.Key).Except(properties).Any()
                && (!schema.TryGetProperty("required", out var required) || required.EnumerateArray().All(parameter => example.ContainsKey(parameter.GetString()!))),
                name + " product usage example fits the advertised parameter roster");
            methods.TryGetValue(name, out var method);
            var tag = parseTag.Invoke(null, new object[] { name })!;
            var layer = Get(tag, "Item1")!.ToString()!;
            var domain = Get(tag, "Item2")!.ToString()!;
            var operation = operationOf.Invoke(null, new object[] { name, description })!;
            var opName = Get(operation, "Item1")!.ToString()!;
            var opInferred = Convert.ToBoolean(Get(operation, "Item2"));
            var category = categoryOf.Invoke(null, new object[] { domain })!.ToString()!;
            var parameters = new JsonArray(properties.Select(parameter => (JsonNode?)JsonValue.Create(parameter)).ToArray());
            rows.Add((name, new JsonObject
            {
                ["name"] = name,
                ["layer"] = layer,
                ["category"] = category,
                ["domain"] = domain,
                ["operation"] = opName,
                ["operationInferred"] = opInferred,
                ["method"] = shared ? "FoundationV4Tool.InvokeAsync" : method?.Name ?? HostPortRunner.Metadata(server, name)["method"]!.GetValue<string>(),
                ["returnType"] = shared ? "CallToolResult" : method?.ReturnType.Name ?? "CallToolResult",
                ["parameters"] = parameters,
                ["description"] = description,
                ["example"] = JsonValue.Create(example.ToJsonString())
            }));
        }
        var uncategorized = rows.Where(row => row.Value["category"]!.GetValue<string>() == "uncategorized").Select(row => row.Name).ToArray();
        Check(uncategorized.Length == 0, uncategorized.Length == 0 ? "all tool domains have registered taxonomy categories" : "Uncategorized tools: " + string.Join(", ", uncategorized));

        if (failures != 0)
        {
            Console.WriteLine($"Generator gates: {checks - failures} checks passed, {failures} failed; output was not written.");
            return 1;
        }

        var sortedRows = rows.Select(row => (JsonNode)row.Value).ToArray();
        // Keep the legacy generator's `Sort-Object name` ordering: PowerShell does not expose
        // OrderedDictionary keys through PSObject.Properties, so every sort key is null and
        // its unstable Array.Sort permutation is observable in manifest/tools-list.json.
        Array.Sort(sortedRows, Comparer<JsonNode>.Create((_, _) => 0));
        var recipeCount = ((IEnumerable)usageType.GetMethod("Sequences", All)!.Invoke(null, new object[] { false })!).Cast<object>().Count();
        var curatedCount = rows.Count(row => row.Value["example"] != null);
        var output = new JsonObject
        {
            ["package"] = packageName,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["source"] = "FoundationHost V" + release + " full product catalog; shared Foundation implementations and engine-only tools; no worker or TIA started",
            ["fileVersion"] = FileVersionInfo.GetVersionInfo(foundationHost).FileVersion,
            ["exeSha256"] = Sha256(foundationHost),
            ["workerSha256"] = Sha256(server.Location),
            ["toolCount"] = rows.Count,
            ["note"] = "Full product roster. Lite advertises the V19 shared tools plus the engine discovery bridge. Withdrawn lifecycle tools are absent. Runtime tools/list is authoritative.",
            ["callDiscipline"] = new JsonObject
            {
                ["parameters"] = disciplineRoot.GetProperty("parameters").GetInt32(),
                ["parametersUndocumented"] = undocumented,
                ["parametersFromVocabulary"] = disciplineRoot.GetProperty("parametersFromVocabulary").GetInt32(),
                ["toolsWithUndocumentedParameters"] = disciplineRoot.GetProperty("toolsWithUndocumentedParameters").GetInt32(),
                ["toolsWithEnumHints"] = disciplineRoot.GetProperty("toolsWithEnumHints").GetInt32(),
                ["enumHints"] = disciplineRoot.GetProperty("enumHints").GetInt32(),
                ["defaultHints"] = disciplineRoot.GetProperty("defaultHints").GetInt32(),
                ["examplesCurated"] = curatedCount,
                ["recipes"] = recipeCount
            },
            ["categories"] = categories,
            ["tools"] = new JsonArray(sortedRows)
        };
        var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, output.ToJsonString(serializerOptions), new UTF8Encoding(false));
        Console.WriteLine($"FoundationHost product metadata: {rows.Count} tools; {curatedCount} with a validated example; {output["callDiscipline"]!["enumHints"]} enum hints on {output["callDiscipline"]!["toolsWithEnumHints"]} tools; {undocumented} of {output["callDiscipline"]!["parameters"]} parameters without their own description ({output["callDiscipline"]!["parametersFromVocabulary"]} covered by the vocabulary; {output["callDiscipline"]!["toolsWithUndocumentedParameters"]} tools); recipes validated");
        Console.WriteLine($"Generator gates: {checks} checks passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static JsonDocument ProductCatalog(Assembly server, string foundationHost, string bundleRoot, string outputPath)
    {
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        var catalogPath = Path.Combine(directory, ".guard-engine-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Export descriptors only. The host's --catalog path overlays the same
            // Foundation registrations as STDIO/HTTP, without launching a worker.
            server.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", All)!.Invoke(null, new object[] { catalogPath });
            string Quote(string value) => "\"" + Regex.Replace(Regex.Replace(value, @"(\\*)""", "$1$1\\\""), @"\\+$", "$0$0") + "\"";
            var start = new ProcessStartInfo(Path.GetFullPath(foundationHost)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "--catalog --offline --profile full --release-key " + release + " --bundle-root " + Quote(Path.GetFullPath(bundleRoot))
                    + " --engine-worker " + Quote(server.Location) + " --engine-catalog " + Quote(catalogPath)
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Foundation product catalog timed out."); }
            var text = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException("Foundation product catalog failed: " + error);
            var product = JsonDocument.Parse(text);
            if (product.RootElement.GetProperty("releaseKey").GetString() != release
                || product.RootElement.GetProperty("profile").GetString() != "full-engine")
            { product.Dispose(); throw new InvalidDataException("Guard roster requires the matching full product catalog."); }
            return product;
        }
        finally { if (File.Exists(catalogPath)) File.Delete(catalogPath); }
    }

    private static void Reset() { checks = 0; failures = 0; }

    private static void AddResolver(Assembly server, string publicApiDirectory)
    {
        var runtime = Path.GetDirectoryName(server.Location)!;
        var api = string.IsNullOrWhiteSpace(publicApiDirectory) ? "" : Path.GetFullPath(publicApiDirectory);
        ResolveEventHandler resolver = (sender, args) =>
        {
            var file = new AssemblyName(args.Name).Name + ".dll";
            foreach (var directory in new[] { runtime, api })
            {
                if (string.IsNullOrEmpty(directory)) continue;
                var candidate = Path.Combine(directory, file);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
    }

    private static Type FindType(Assembly assembly, string name, bool throwOnError = true)
    {
        var found = assembly.GetType(name, false);
        if (found != null) return found;
        var logicPath = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "TiaMcp.Logic.dll");
        if (File.Exists(logicPath)) found = Assembly.LoadFrom(logicPath).GetType(name, false);
        return found ?? (throwOnError ? throw new TypeLoadException("Type not found: " + name) : null)!;
    }

    private static object? Get(object target, string name)
    {
        var type = target.GetType();
        return type.GetProperty(name, All)?.GetValue(target) ?? type.GetField(name, All)?.GetValue(target);
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found from " + start);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static FakeTarget Target(string name, string ip)
    {
        var target = new FakeTarget { Name = name };
        target.Addresses.Add(new FakeAddress { Address = ip });
        return target;
    }

    private static FakePcInterface Pc(string name, int number, string ip, FakeTarget target)
    {
        var pc = new FakePcInterface { Name = name, Number = number };
        pc.Addresses.Add(new FakeAddress { Address = ip });
        pc.TargetInterfaces.Add(target);
        return pc;
    }

    private static FakeConnectionConfiguration Config(params FakePcInterface[] pcs)
    {
        var mode = new FakeMode();
        mode.PcInterfaces.AddRange(pcs);
        var config = new FakeConnectionConfiguration();
        config.Modes.Add(mode);
        return config;
    }

    private static int? ReadPreviousUndocumented(string outputPath, out bool productRoster)
    {
        productRoster = false;
        if (!File.Exists(outputPath)) return null;
        try
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(outputPath));
            productRoster = previous.RootElement.GetProperty("source").GetString()?.StartsWith("FoundationHost V21 full product catalog", StringComparison.Ordinal) == true;
            return previous.RootElement.GetProperty("callDiscipline").GetProperty("parametersUndocumented").GetInt32();
        }
        catch { return null; }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
