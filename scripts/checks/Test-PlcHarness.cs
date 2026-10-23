#!/usr/bin/env dotnet
// Kept host metadata and managed codecs for the PLC harness; never loads an engine.
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../src/FoundationHost/TiaMcp.FoundationHost.csproj

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;

return PlcHostShapes.Run(args);

internal static class PlcHostShapes
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Assembly Host = typeof(EngineHostPipeline).Assembly;
    private static readonly Assembly Logic = typeof(InputSchema).Assembly;
    private static readonly Assembly Foundation = Assembly.Load("TiaMcp.FoundationHost");
    private static Type T(string name) => Host.GetType(name, false) ?? Logic.GetType(name, false) ?? Foundation.GetType(name, true)!;
    private static object Get(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value)!;
    private static MethodInfo Parser => Logic.GetType("TiaMcp.Logic.V4.V4Json", true)!.GetMethod("ParseInput", All)!;
    // Identity tools seam of SharedReleaseHost.CreateSession: it returns the host's shared implementations unchanged
    // and only records which tools the host implements itself.
    private static IReadOnlyList<McpServerTool> shared = Array.Empty<McpServerTool>();
    private static IReadOnlyList<McpServerTool> RecordShared<TWorker>(TWorker worker, IReadOnlyList<McpServerTool> tools) { shared = tools; return tools; }
    private static bool CoverageMatches(string[] complete, string[] host, IDictionary execution)
    {
        var expected = complete.Concat(host).Order(StringComparer.Ordinal).ToArray();
        return expected.Distinct(StringComparer.Ordinal).Count() == expected.Length && execution.Count == expected.Length
            && execution.Keys.Cast<string>().Order(StringComparer.Ordinal).SequenceEqual(expected);
    }

    internal static int Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Expected a host-shape command.");
        if (args[0] == "coverage-self-test")
        {
            var valid = new Hashtable { ["EngineTool"] = "worker", ["HostTool"] = "host" };
            if (!CoverageMatches(new[] { "EngineTool" }, new[] { "HostTool" }, valid)) throw new InvalidOperationException("Valid coverage refused");
            foreach (var invalid in new[] { new Hashtable { ["HostTool"] = "host" },
                new Hashtable { ["EngineTool"] = "worker", ["HostTool"] = "host", ["Extra"] = "host" },
                new Hashtable { ["EngineTool"] = "worker", ["Extra"] = "host" } })
                if (CoverageMatches(new[] { "EngineTool" }, new[] { "HostTool" }, invalid)) throw new InvalidOperationException("Invalid coverage accepted");
            Console.WriteLine("COMPLETE: 4 descriptor coverage self-tests passed"); return 0;
        }
        if (args[0] == "rpc")
        {
            var request = JsonNode.Parse(Console.In.ReadToEnd())!.AsObject();
            var input = request["arguments"]!;
            try
            {
                object? value = (string)request["command"]! switch
                {
                    "Compare" => T("TiaMcpServer.Siemens.Services.UnifiedGraphicComparison").GetMethod("Compare", All)!
                        .Invoke(null, new object[] { (string)input["left"]!, (string)input["right"]! }),
                    "Decode" => T("TiaMcpServer.Siemens.Services.UnifiedInputCodec").GetMethod("Decode", All)!
                        .Invoke(null, new[] { Parser.Invoke(null, new object[] { (string)input["value"]! }) }),
                    _ => throw new ArgumentException("Unknown kept-code RPC")
                };
                Console.WriteLine(new JsonObject { ["value"] = JsonSerializer.SerializeToNode(value) }.ToJsonString());
            }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException)
            { Console.WriteLine(new JsonObject { ["exception"] = error.InnerException.Message }.ToJsonString()); }
            return 0;
        }
        if (args[0] == "parser-self-test")
        {
            foreach (var text in new[] { "null", "{" })
            {
                try { TiaMcp.Logic.V4.V4Json.Deserialize<JsonObject>(text); throw new InvalidOperationException("Invalid JSON accepted"); }
                catch (JsonException error)
                {
                    string expected = text == "null" ? "System.Text.Json.JsonException" : "System.Text.Json.JsonReaderException";
                    if (error.GetType().FullName != expected) throw;
                }
            }
            Console.WriteLine("COMPLETE: 2 pre-dispatch JSON rejection checks passed"); return 0;
        }
        if (args.Length != 6 || args[0] is not ("metadata" or "guards")) throw new ArgumentException("Expected metadata/guards release served-tools.json output.json worker api");
        string release = args[1];
        T("TiaMcp.FoundationHost.EngineHostConfiguration").GetField("ReleaseKey", All)!.SetValue(null, release);
        var data = (IDictionary)T("TiaMcp.FoundationHost.ReleaseCatalogData").GetProperty("Releases", All)!.GetValue(null)!;
        var declarations = ((IEnumerable)data[release]!.GetType().GetMethod("Declarations", All)!.Invoke(data[release], new object[] { release })!)
            .Cast<MethodInfo>().ToArray();
        var methods = declarations.ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name, StringComparer.Ordinal);
        var options = T("TiaMcp.FoundationHost.HostOptions").GetMethod("Parse", All)!.Invoke(null, new object[] {
            new[] { "--bundle-root", Directory.GetCurrentDirectory(), "--release-key", release, "--plc-worker", "--worker-exe", args[4], "--public-api", args[5], "--profile", "full" },
            Path.GetDirectoryName(Path.GetFullPath(args[4]))!
        })!;
        // The production session: no catalog seam, and the tools seam returns the shared implementations unchanged.
        var create = T("TiaMcp.FoundationHost.SharedReleaseHost").GetMethod("CreateSession", All)!;
        var seam = create.GetParameters()[2].ParameterType;
        var record = Delegate.CreateDelegate(Nullable.GetUnderlyingType(seam) ?? seam,
            typeof(PlcHostShapes).GetMethod(nameof(RecordShared), All)!.MakeGenericMethod(seam.GetGenericArguments()[0]));
        using var session = (IDisposable)create.Invoke(null, new object?[] { options, null, record })!;
        var pipeline = (EngineHostPipeline)Get(session, "Pipeline");
        using var context = (IDisposable)typeof(EngineHostPipeline).GetMethod("EnterSession", All)!.Invoke(pipeline, null)!;
        var catalog = T("TiaMcpServer.ModelContextProtocol.McpServer").GetProperty("CatalogView", All)!.GetValue(null)!;
        if (args[0] == "guards") return Guards();
        var complete = (IDictionary)Get(catalog, "IncludingUnavailable");
        var available = (IDictionary)Get(catalog, "All");
        var execution = (IDictionary)T("TiaMcpServer.ModelContextProtocol.ToolExecution").GetField("Table", All)!.GetValue(null)!;
        var served = JsonNode.Parse(File.ReadAllText(args[2]))!.AsArray();
        var names = served.Select(t => (string)t!["name"]!).ToArray();
        int passed = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); passed++; }
        // Check the same complete declaration/ownership equality as the old
        // descriptor harness, before transport-specific bridge implementations.
        var removed = (ISet<string>)T("TiaMcp.FoundationHost.SharedToolPolicy").GetField("Removed", All)!.GetValue(null)!;
        // The engine harness required the execution table to be exactly the complete engine catalog plus the host
        // declarations. On the kept host that catalog is this release's declarations (ReleaseCatalogData.Declarations)
        // plus the retired alias; every other complete-catalog entry is a shared Foundation implementation of the host.
        // Shared implementations that the engine never had (Workbench, generation, Foundation tag tools) have no
        // table entry; every declaration must have one, and no table entry may be missing from the catalog.
        var declared = methods.Keys.ToArray();
        Check(declared.All(complete.Contains), "A release declaration is missing from the complete catalog: "
            + string.Join(", ", declared.Where(name => !complete.Contains(name))));
        var sharedOnly = complete.Keys.Cast<string>().Where(name => !methods.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
        // P8-81d (2026-10-20 decision): a shared Foundation implementation that PortedFamilies withholds on this release (the V21-format
        // composers on V20) is neither served nor declared here, so its release-independent execution-table row is outside this
        // release's coverage. Every other row is compared exactly as before.
        var withheld = TiaMcp.Adapters.Contracts.PortedFamilies.Withheld(release).ToHashSet(StringComparer.Ordinal);
        var releaseExecution = execution.Keys.Cast<string>().Where(name => !(withheld.Contains(name) && !complete.Contains(name)))
            .ToDictionary(name => name, name => execution[name], StringComparer.Ordinal);
        var reviewed = complete.Keys.Cast<string>().Where(name => !sharedOnly.Contains(name) || releaseExecution.ContainsKey(name)).ToArray();
        var coverage = reviewed.Concat(removed).ToHashSet(StringComparer.Ordinal);
        Check(CoverageMatches(reviewed, removed.ToArray(), releaseExecution), "Descriptor/execution coverage mismatch; missing: "
            + string.Join(", ", coverage.Except(releaseExecution.Keys).Order(StringComparer.Ordinal)) + "; extra: "
            + string.Join(", ", releaseExecution.Keys.Except(coverage).Order(StringComparer.Ordinal)));
        var implemented = shared.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        Check(implemented.Count > 0 && sharedOnly.All(implemented.Contains), "An undeclared catalog entry is not a shared host implementation: "
            + string.Join(", ", sharedOnly.Where(name => !implemented.Contains(name))));
        foreach (var name in sharedOnly)
            Check((string)Get(complete[name]!, "Execution") == "foundation", name + " shared host implementation is not Foundation-owned");
        Check(removed.SetEquals(new[] { "ConnectProject" }) && removed.All(name => !complete.Contains(name) && execution.Contains(name)), "Retired engine alias changed without D5 review");
        foreach (var name in releaseExecution.Keys.Where(name => (string)execution[name]! == "host"))
            Check(complete.Contains(name), name + " host declaration is missing");
        var metadata = new JsonObject();
        foreach (DictionaryEntry entry in complete)
        {
            string name = (string)entry.Key;
            var descriptor = entry.Value!;
            Check(sharedOnly.Contains(name) || execution.Contains(name), name + " has no execution entry");
            // A declaration keeps its reviewed owner unless the host implements the tool itself: then it is
            // Foundation-owned, and a migrated declaration keeps a reviewed host owner (SharedToolCatalog).
            string expectedOwner = !implemented.Contains(name) ? (string)execution[name]!
                : methods.ContainsKey(name) && execution.Contains(name) && TiaMcp.Adapters.Contracts.PortedFamilies.IsMigratedDeclaration(name)
                    && !TiaMcp.Adapters.Contracts.PortedFamilies.UsesFoundationCandidate(name) && (string)execution[name]! == "host" ? "host" : "foundation";
            Check((string)Get(descriptor, "Execution") == expectedOwner, name + " changed ownership: " + Get(descriptor, "Execution") + " instead of " + expectedOwner
                + (sharedOnly.Contains(name) ? " (shared implementation)" : " (declaration)"));
            Check((string)Get(descriptor, "Name") == name, name + " changed canonical name");
            if (!methods.TryGetValue(name, out var method))
                method = Host.GetTypes().SelectMany(type => type.GetMethods(All | BindingFlags.DeclaredOnly))
                    .SingleOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } attribute && (attribute.Name ?? m.Name) == name);
            if (method == null)
            {
                // A table-driven shared implementation (FoundationTools) has no attributed method; its served
                // registration and descriptor parameters are the subject.
                Check(sharedOnly.Contains(name), name + " declaration has no attributed method");
                metadata[name] = new JsonObject {
                    ["method"] = null, ["registered"] = pipeline.AllTools.Any(t => t.ProtocolTool.Name == name),
                    ["parameters"] = new JsonArray(((IEnumerable)Get(descriptor, "Parameters")).Cast<object>().Select(p => (JsonNode)new JsonObject {
                        ["name"] = (string)Get(p, "Name"), ["required"] = (bool)Get(p, "Required"),
                        ["default"] = p.GetType().GetProperty("DefaultJson", All)!.GetValue(p) is string json ? JsonNode.Parse(json) : null
                    }).ToArray())
                };
                continue;
            }
            metadata[name] = new JsonObject {
                ["method"] = method.Name, ["registered"] = method.GetCustomAttribute<McpServerToolAttribute>() != null,
                ["parameters"] = new JsonArray(method.GetParameters().Select(p => (JsonNode)new JsonObject {
                    ["name"] = p.Name, ["required"] = !p.IsOptional,
                    ["default"] = p.IsOptional && p.DefaultValue is not DBNull && p.DefaultValue != Missing.Value ? JsonSerializer.SerializeToNode(p.DefaultValue) : null
                }).ToArray())
            };
        }
        // The real FoundationHost is the publication oracle, including bridge
        // registrations which aren't declarations in the base catalog.
        Check(names.Length == names.Distinct(StringComparer.Ordinal).Count(), "Duplicate published tool");
        Check(names.Length == available.Count && pipeline.AllTools.Count == available.Count, "Full profile differs from release descriptors");
        foreach (string name in available.Keys)
        {
            Check(names.Contains(name, StringComparer.Ordinal), name + " was not published by FoundationHost");
            var published = served.Single(t => (string)t!["name"]! == name)!;
            var tool = Get(available[name]!, "Tool");
            var protocol = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name).ProtocolTool;
            Check(ReferenceEquals(protocol, tool) && JsonNode.DeepEquals(published["inputSchema"], JsonSerializer.SerializeToNode(Get(tool, "InputSchema"))), name + " published another decorated SDK Tool/schema");
        }
        var lite = ((IEnumerable)Get(catalog, "Lite")).Cast<object>().Select(d => (string)Get(d, "Name")).ToArray();
        var publishedLite = ((IEnumerable)T("TiaMcpServer.ModelContextProtocol.McpServer").GetMethod("GetLiteTools", All)!.Invoke(null, null)!).Cast<object>().Count();
        Check(lite.All(name => available.Contains(name)) && lite.Length == publishedLite, "Lite profile differs from release descriptors");
        File.WriteAllText(args[3], metadata.ToJsonString());
        Console.WriteLine($"COMPLETE: {passed} descriptor catalog checks passed");
        return 0;
    }

    private static int Guards()
    {
        int passed = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        foreach (var scenario in new[] { "read-exception", "write-exception", "legacy-result", "cancel-before-call" })
        {
            var session = new object();
            T("TiaMcpServer.ModelContextProtocol.McpServer").GetProperty("ApprovalSessionKeyForTests", All)!.SetValue(null, (Func<object?>)(() => session));
            var fixture = new GuardFixture(scenario == "write-exception", scenario == "legacy-result");
            var wrapper = T("TiaMcpServer.ModelContextProtocol.SerializedCallTool");
            var tool = (McpServerTool)Activator.CreateInstance(wrapper, All, null, new object[] { fixture }, null)!;
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancel-before-call") cancellation.Cancel();
            var result = tool.InvokeAsync(null!, cancellation.Token).AsTask().GetAwaiter().GetResult();
            var text = result.Content.Cast<TextContentBlock>().Single().Text;
            var body = JsonNode.Parse(text)!;
            Check(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(result.StructuredContent)), body)
                && (int)body["schemaVersion"]! == 4 && !(bool)body["ok"]! && result.IsError == true, "SDK guard lost its matching V4 envelope");
            var error = body["error"]!; var meta = body["meta"]!;
            bool unknown = scenario == "write-exception", cancelled = scenario == "cancel-before-call";
            Check((string)error["code"]! == (unknown ? "OUTCOME_UNKNOWN" : cancelled ? "CANCELLED" : "INTERNAL_ERROR")
                && (string)meta["outcome"]! == (unknown ? "unknown" : cancelled ? "rejected-before-operation" : "read-failed")
                && (string)meta["execution"]! == (unknown ? "unknown" : cancelled ? "not-started" : "read-only")
                && !(bool)meta["requiresSessionReset"]!, "SDK guard lost typed outcome details: " + scenario + " " + text);
            Check(fixture.Calls == (cancelled ? 0 : 1) && !text.Contains("SECRET", StringComparison.Ordinal) && body.AsObject().ContainsKey("message") == false,
                "SDK guard leaked arguments or dispatched cancellation");
            if (unknown) Check(meta["warnings"]!.AsArray().Count(w => (string)w!["code"]! == "APPROVAL_DISABLED") == 1, "SDK guard disabled-approval warning");
            passed++;
        }
        Console.WriteLine($"COMPLETE: {passed} direct SDK guard checks passed; no native call executed"); return 0;
    }

    private sealed class GuardFixture(bool write, bool legacy) : McpServerTool
    {
        internal int Calls;
        public override Tool ProtocolTool => new() { Name = write ? "RestartOpennessWorker" : "GetSessionState" };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (legacy) return new(new CallToolResult { Content = new[] { new TextContentBlock { Text = "{\"message\":\"old\",\"meta\":{\"success\":true}}" } } });
            throw new ModelContextProtocol.McpException("SECRET-fixture-argument", ModelContextProtocol.McpErrorCode.InternalError);
        }
    }
}
