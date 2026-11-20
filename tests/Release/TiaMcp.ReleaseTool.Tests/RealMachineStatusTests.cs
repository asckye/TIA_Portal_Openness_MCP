using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;
using static TiaMcp.ReleaseTool.RealMachineStatusGenerator;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "Generators")]
public sealed class RealMachineStatusTests
{
    private static void Batch(StaticTree tree, string suffix, string run, string rows)
    {
        tree.Write($"input/candidate/run-{suffix}.json", run);
        tree.Write($"input/candidate/results-{suffix}.jsonl", rows);
    }

    [Fact]
    public void ReadsPairedBatchesAndOnlyThePublishableFields()
    {
        using var tree = new StaticTree();
        tree.Copy(Roster);
        // Selected fields from beta5-v20/C2, beta6-v21/R and rc-full-v17/A.unreachable.
        Batch(tree, "C2", """{"release":"20","package":"4.0.0-beta.5","commit":"52d2d417","batch":"C2","date":"2026-11-17T17:01:11Z"}""",
            """{"tool":"GetSessionState","release":"20","severity":"none","outcome":"passed","utc":"2026-11-17T17:01:37Z","error":null}""");
        Batch(tree, "R", """{"release":"21","package":"beta6-candidate","commit":"ae05c25d","batch":"R","date":"2026-11-18T14:23:19Z"}""",
            """{"tool":"ListPortalProcessProjects","outcome":"passed","utc":"2026-11-18T14:23:19Z"}""");
        Batch(tree, "A.unreachable", """{"release":"17","package":"rc-candidate","commit":"d89d0152","batch":"A","date":"2026-11-19T17:19:02Z"}""",
            """{"tool":"ListPortalProcessProjects","release":"17","outcome":"not-run","utc":"2026-11-19T17:19:11Z"}""");
        // Malformed files make opening either excluded folder observable.
        foreach (var excluded in new[] { "beta4-v21", "beta2-20261031" })
        {
            tree.Write($"input/{excluded}/results-A.jsonl", "invalid");
            tree.Write($"input/{excluded}/run-A.json", "invalid");
            tree.Write($"input/{excluded}/results.jsonl", "invalid");
            var error = Assert.Throws<ReleaseException>(() => Import(tree.Root, Path.Combine(tree.Root, "input", excluded)));
            Assert.Contains("Excluded real-machine input", error.Message);
        }
        Batch(tree, "A2", """{"release":"V21","package":"rc-candidate","commit":"9a4274f2","date":"2026-11-20T11:43:41Z"}""",
            """
            {"tool":"GetSessionState","release":"14sp1","outcome":"passed","helper":false}
            {"tool":"GetSessionState","release":"15.1","outcome":"failed","error":{"code":"INVALID_ARGUMENT"}}
            {"tool":"GetToolUsage","release":"V21","outcome":"known","error":"PRECONDITION_FAILED"}
            {"tool":"GetSessionState","outcome":"pending","error":"C:\\Users\\private-test\\192.168.1.2"}
            {"tool":"NotRegisteredHere","outcome":"failed","severity":"HIGH"}
            """);
        var table = Import(tree.Root, Path.Combine(tree.Root, "input"));
        Assert.Empty(table.Cells("14sp1"));
        Assert.Equal(5, table.Runs.Length);
        Assert.Equal("blocked", table.Cells("17")["ListPortalProcessProjects"].Result.Status);
        Assert.Equal("52d2d417", table.Cells("20")["GetSessionState"].Run.Commit);
        Assert.Equal("ae05c25d", table.Cells("21")["ListPortalProcessProjects"].Run.Commit);
        Assert.Equal(new Result("GetSessionState", "failed", "MEDIUM", "INVALID_ARGUMENT"), table.Cells("15.1")["GetSessionState"].Result);
        Assert.Equal(new Result("GetToolUsage", "known", "MEDIUM", "PRECONDITION_FAILED"), table.Cells("21")["GetToolUsage"].Result);
        Assert.Equal(new Result("GetSessionState", "not-run", "None"), table.Cells("21")["GetSessionState"].Result);
        Assert.DoesNotContain("NotRegisteredHere", table.Cells("21").Keys);
        Assert.Equal(2, table.Summarize("21").Recorded);
        foreach (var name in new[] { "README.md", "README.zh-CN.md" }) tree.Write(name, $"{Begin}\nstale\n{End}\n");
        var outputs = Generate(tree.Root, Path.Combine(tree.Root, "input"));
        Assert.Contains(Manifest, outputs.Keys);
        AssertPublicOutputs(outputs);
        var import = Options.Parse(["-Root", tree.Root, "-Input", Path.Combine(tree.Root, "input")], Program.BooleanOptions);
        Assert.Equal(0, ReleaseCommands.GeneratorCommand("generate-real-machine-status", import));
        Assert.Equal(0, CatalogJson.Write(tree.Root, outputs, true));
    }

    [Theory]
    [InlineData("failed", "MEDIUM", "failed", "LOW", "❌")]
    [InlineData("known", "HIGH", "failed", "MEDIUM", "❌")]
    [InlineData("failed", "LOW", "passed", "LOW", "⚠")]
    [InlineData("passed", "LOW", "refused-as-designed", "None", "⚠")]
    [InlineData("refused-as-designed", "LOW", "passed", "None", "⚠")]
    [InlineData("refused-as-designed", "None", "passed", "None", "⛔")]
    [InlineData("passed", "None", "skipped", "None", "✅")]
    [InlineData("passed", "None", "not-run", "LOW", "✅")]
    public void WorstExecutedResultWinsRegardlessOfFileOrder(string status, string severity, string other, string otherSeverity, string symbol)
    {
        using var tree = new StaticTree();
        tree.Copy(Roster);
        const string run = """{"release":"17","commit":"9a4274f2","package":"rc-candidate","date":"2026-11-20"}""";
        string Row(string outcome, string level) => new JsonObject { ["tool"] = "GetToolUsage", ["outcome"] = outcome, ["severity"] = level }.ToJsonString();
        foreach (bool reverse in new[] { true, false })
        {
            Batch(tree, "C", run, reverse ? Row(status, severity) : Row(other, otherSeverity));
            Batch(tree, "C2", run, reverse ? Row(other, otherSeverity) : Row(status, severity));
            var cell = Import(tree.Root, Path.Combine(tree.Root, "input")).Cells("17")["GetToolUsage"];
            Assert.Equal(status, cell.Result.Status);
            Assert.Equal(severity, cell.Result.Severity);
            Assert.Equal(symbol, cell.Result.Symbol);
        }
    }

    [Fact]
    public void EarliestRunDatesEachCommitAndCheckReplaysTheManifestAlone()
    {
        using var tree = new StaticTree();
        tree.Copy(Roster);
        foreach (var name in new[] { "README.md", "README.zh-CN.md" }) tree.Write(name, $"intro\n{Begin}\nstale\n{End}\ntail\n");
        const string oldRows = """
            {"tool":"GetToolUsage","outcome":"failed"}
            {"tool":"GetSessionState","outcome":"passed"}
            """;
        Batch(tree, "A", """{"release":"20","commit":"aaaaaaaa","package":"same","date":"2026-01-01"}""", oldRows);
        Batch(tree, "A2", """{"release":"21","commit":"aaaaaaaa","package":"same","date":"2026-01-03"}""", oldRows);
        Batch(tree, "R", """{"release":"21","commit":"bbbbbbbb","package":"same","date":"2026-01-02"}""",
            """
            {"tool":"GetToolUsage","outcome":"passed"}
            {"tool":"GetSessionState","outcome":"skipped"}
            """);
        var imported = Generate(tree.Root, Path.Combine(tree.Root, "input"));
        Assert.Equal(0, CatalogJson.Write(tree.Root, imported, false));
        var table = Read(tree.Root);
        Assert.Equal("bbbbbbbb", table.Summarize("21").Latest!.Commit);
        Assert.Equal("passed", table.Cells("21")["GetToolUsage"].Result.Status);
        Assert.Equal("aaaaaaaa", table.Cells("21")["GetSessionState"].Run.Commit);
        Assert.Contains("✅ᵒ", imported[Page]);
        Assert.Contains("· | · | · | · | · | · | ❌ | ✅ | ❌ |", imported[Page]);
        Assert.Contains("intro\n" + Begin, imported["README.md"]);
        Assert.EndsWith(End + "\ntail\n", imported["README.zh-CN.md"]);
        var options = Options.Parse(["-Root", tree.Root, "-Input", "missing", "-Check"], Program.BooleanOptions);
        Assert.Equal(0, ReleaseCommands.GeneratorCommand("generate-real-machine-status", options));
        var page = Path.Combine(tree.Root, Page);
        File.WriteAllText(page, imported[Page], new UTF8Encoding(true));
        Assert.Equal(1, ReleaseCommands.GeneratorCommand("generate-real-machine-status", options));
        Assert.Equal((byte)0xef, File.ReadAllBytes(page)[0]);
    }

    [Fact]
    public void DefaultReplayRendersWithoutVmFilesAndNamesToolsRemovedFromOneRelease()
    {
        using var tree = new StaticTree();
        tree.Copy(Manifest);
        tree.Copy(Roster);
        var original = File.ReadAllBytes(Path.Combine(tree.Root, Manifest));
        const string tool = "GetSessionState";
        var before = Read(tree.Root);
        Assert.True(before.Cells("21")[tool].Result.Ran);
        var roster = CatalogJson.Read(tree.Root, Roster);
        var tools = roster["releases"]!["21"]!["tools"]!.AsArray();
        tools.RemoveAt(tools.IndexOf(tools.Single(t => (string?)t == tool)));
        tree.Write(Roster, CatalogJson.Encode(roster));

        using var diagnostics = new StringWriter();
        var table = Read(tree.Root, diagnostics);
        Assert.Equal("Dropped unregistered tool: 21/GetSessionState", diagnostics.ToString().TrimEnd());
        Assert.DoesNotContain(tool, table.Cells("21").Keys);
        Assert.Equal(before.Summarize("21").Recorded - 1, table.Summarize("21").Recorded);
        Assert.Equal(before.Cells("20")[tool].Result, table.Cells("20")[tool].Result);
        Assert.Equal(before.Cells("20")[tool].Run.Commit, table.Cells("20")[tool].Run.Commit);
        foreach (var name in new[] { "README.md", "README.zh-CN.md" }) tree.Write(name, $"{Begin}\nstale\n{End}\n");
        Assert.False(Directory.Exists(Path.Combine(tree.Root, "bin-build", "vm-test")));
        var options = Options.Parse(["-Root", tree.Root], Program.BooleanOptions);
        Assert.Equal(0, ReleaseCommands.GeneratorCommand("generate-real-machine-status", options));
        var page = File.ReadAllText(Path.Combine(tree.Root, Page));
        var row = page.Split('\n').Single(line => line.StartsWith("| GetSessionState |", StringComparison.Ordinal));
        Assert.Equal("—", row.Split('|')[10].Trim());
        Assert.Contains("older candidate, marked ᵒ", File.ReadAllText(Path.Combine(tree.Root, "README.md")));
        Assert.Contains("较早的候选版本，在完整表中以 ᵒ 标出", File.ReadAllText(Path.Combine(tree.Root, "README.zh-CN.md")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(tree.Root, Manifest)));
        Assert.Equal(0, ReleaseCommands.GeneratorCommand("generate-real-machine-status",
            Options.Parse(["-Root", tree.Root, "-Check"], Program.BooleanOptions)));
        var compact = CatalogJson.Read(tree.Root, Manifest).ToJsonString();
        tree.Write(Manifest, compact);
        Assert.Equal(0, ReleaseCommands.GeneratorCommand("generate-real-machine-status", options));
        Assert.Equal(compact, File.ReadAllText(Path.Combine(tree.Root, Manifest)));
        Assert.Throws<ReleaseException>(() => Generate(tree.Root, null, checkManifest: true));
    }

    [Fact]
    public void ServedExamplesUseTheTargetReleaseAndTheRefusedToolName()
    {
        foreach (var key in CatalogJson.Keys)
        {
            var schemas = CatalogJson.Read(Repository.FindRoot(), $"manifest/contracts/v4/baseline/{key}.json")["tools"]!.AsArray()
                .ToDictionary(t => (string)t!["name"]!, t => t!["inputSchema"]!.AsObject());
            JsonObject Args(string tool)
            {
                var usage = TiaOpenness.Shared.ToolUsageCatalog.Describe(tool, key, "plc-foundation", "", schemas[tool]);
                var request = usage["example"]!["request"]!["params"]!;
                Assert.Equal(tool, (string?)request["name"]);
                return request["arguments"]!.AsObject();
            }
            Assert.True((bool?)Args("PreviewToolBatch")["operations"]![0]!["arguments"]!["confirm"]);
            Assert.Single(Args("ApplyToolBatch"));
            Assert.Contains("ListPlcTagTables", (string?)Args("GetPlcTagTableConstants")["tablePath"]);
            Assert.Contains("GetProjectTree", (string?)Args("CreateLibraryMasterCopy")["softwarePath"]);
            Assert.Equal(key == "21" ? "fc" : "udt", (string?)Args("BuildAndImportPlcArtifact")["kind"]);
            Assert.Equal("Motor", (string?)Args("PrefillWorkbenchForm")["fields"]!["filter"]);
            foreach (var name in new[] { "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcTagTables", "ExportPlcBlocksDocuments" }.Where(schemas.ContainsKey))
                Assert.Contains("GetProjectTree", (string?)Args(name)["softwarePath"]);
            if (schemas.ContainsKey("CreateVersionControlWorkspace"))
                Assert.Equal("C:/Examples/VCI/Engineering", (string?)Args("CreateVersionControlWorkspace")["folderPath"]);
            if (key == "16")
            {
                Assert.Equal("GetAttribute", (string?)Args("InvokeObject")["methodName"]);
                Assert.Equal("ToString", (string?)Args("InvokeService")["methodName"]);
            }
            if (key == "17")
                foreach (var field in new[] { "includeBlocks", "includeLibraries", "includePins" })
                    Assert.False((bool?)Args("ListDccCharts")[field]);
            if (key == "18")
                Assert.Equal("OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0", (string?)Args("CreateHardwareCatalogDevice")["keyword"]);
        }
    }

    [Fact]
    public void PublishedManifestAndPagesOnlyContainPublicEvidence()
    {
        string root = Repository.FindRoot();
        var outputs = Generate(root, null, checkManifest: true);
        Assert.Equal(0, CatalogJson.Write(root, outputs, true));
        Assert.DoesNotContain(Manifest, outputs.Keys);
        outputs[Manifest] = File.ReadAllText(Path.Combine(root, Manifest));
        foreach (var candidate in JsonNode.Parse(outputs[Manifest])!.AsArray())
        {
            var run = Assert.IsType<JsonObject>(candidate);
            Assert.Equal(new[] { "commit", "date", "package", "records", "release" }, run.Select(p => p.Key).Order());
            Assert.Matches(@"^(14sp1|15\.1|1[6-9]|2[01])$", (string)run["release"]!);
            Assert.Matches("^[0-9a-f]{8}$", (string)run["commit"]!);
            Assert.Matches(@"^[\w.\-]+$", (string)run["package"]!);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", (string)run["date"]!);
            foreach (var item in run["records"]!.AsArray())
            {
                var row = Assert.IsType<JsonObject>(item);
                string[] fields = row.ContainsKey("code") ? ["code", "severity", "status", "tool"] : ["severity", "status", "tool"];
                Assert.Equal(fields, row.Select(p => p.Key).Order());
                Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", (string)row["tool"]!);
                Assert.Matches("^(passed|failed|known|refused-as-designed|not-run|skipped|blocked)$", (string)row["status"]!);
                Assert.Matches("^(None|LOW|MEDIUM|HIGH)$", (string)row["severity"]!);
                if (row.ContainsKey("code")) Assert.Matches("^[A-Z][A-Z0-9_]*$", (string)row["code"]!);
            }
        }
        AssertPublicOutputs(outputs);
    }

    private static void AssertPublicOutputs(Dictionary<string, string> outputs)
    {
        foreach (var (path, content) in outputs)
        {
            var published = path.StartsWith("README", StringComparison.Ordinal) ? content.Split(Begin)[1].Split(End)[0] : content;
            // The registered tool ManageDeviceUsers is public. A Users path segment is private.
            Assert.DoesNotMatch(@"(?i):\\|192\.168\.|\bUsers\b|private-test|\b(arguments|envelope|stderr|requestId)\b", published);
            Assert.DoesNotContain(Environment.UserName, published, StringComparison.OrdinalIgnoreCase);
        }
    }
}
