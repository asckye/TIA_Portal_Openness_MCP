using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseRecordConsumerTests
{
    [Theory]
    [Trait("Category", "BundleParity")]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveryBindingAndRepositoryInventoryAcceptEitherRecord(bool multiOnly)
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        var relative = multiOnly ? ReleaseRecords.MultiPath : ReleaseRecords.EnginePath;
        tree.Write(relative, (multiOnly ? multi : engine).ToJsonString());
        Assert.Equal(relative, ReleaseRecords.SelectPath(tree.Root));
        using var delivery = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        { [ReleaseRecords.BindingProperty(relative)] = ReleaseRecords.HashFile(Path.Combine(tree.Root, relative)) }));
        ReleaseRecords.AssertBinding(tree.Root, delivery.RootElement, relative);
        Assert.Equal(multiOnly ? "files" : "runtimeFiles", ReleaseRecords.InventoryProperty(relative));
        Assert.Equal(6, ReleaseRecords.DeliveryFiles(tree.Root).Count);
        tree.Write(relative, (multiOnly ? multi : engine).ToJsonString() + " ");
        Assert.Throws<ReleaseException>(() => ReleaseRecords.AssertBinding(tree.Root, delivery.RootElement, relative));
    }

    [Theory]
    [InlineData("3.3.0", false)]
    [InlineData("4.0.0-beta.1", true)]
    [InlineData("4.0.0", true)]
    [InlineData("", true)]
    public void OnlyRecordsBefore4RequireNoPlcProof(string release, bool required)
    {
        using var record = JsonDocument.Parse(new JsonObject { ["release"] = release }.ToJsonString());
        Assert.Equal(required, ReleaseRecords.RequiresPlcProof(record.RootElement));
    }

    // CI's validate-bundle step (-Strict -NoBinaries -SkipSourceHashes) reads the committed records; between 3.3.0 and the first
    // 4.0 release they are the shipped 3.3.0 records (P8-04j2 broke this once).
    [Fact]
    public void CommittedRecordsValidateWithoutBinaries()
        => ReleaseCommands.ValidateBuildRecords(TiaMcp.BuildCommon.Repository.FindRoot(), package: false, noBinaries: true, skipSourceHashes: true);

    [Fact]
    public void PreferredMultiDoesNotFallBackWhenItsRecordIsDamaged()
    {
        using var tree = new StaticTree();
        var (engine, _) = Records(tree);
        tree.Write(ReleaseRecords.EnginePath, engine.ToJsonString());
        tree.Write(ReleaseRecords.MultiPath, "{bad");
        Assert.Equal(ReleaseRecords.MultiPath, ReleaseRecords.SelectPath(tree.Root));
        // JsonDocument reports a malformed record with a JsonException subclass.
        Assert.ThrowsAny<JsonException>(() => ReleaseRecords.DeliveryFiles(tree.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumeRejectsNonFullEvidenceForEitherRecordShape(bool multiOnly)
    {
        using var tree = new StaticTree();
        tree.Write("Version.props", "<Project />");
        var (engine, multi) = Records(tree);
        tree.Write(multiOnly ? ReleaseRecords.MultiPath : ReleaseRecords.EnginePath, (multiOnly ? multi : engine).ToJsonString());
        Assert.Contains("full", ReleaseRecords.ResumeReason(tree.Root, "4.0.0"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Category", "BundleParity")]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareDeliveryAndValidateBundleKeepVersionAndRecordHashGates(bool multiOnly)
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        var relative = multiOnly ? ReleaseRecords.MultiPath : ReleaseRecords.EnginePath;
        tree.Write(relative, (multiOnly ? multi : engine).ToJsonString());
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        Assert.Equal(Path.Combine(tree.Root, ReleaseRecords.MultiPath), ReleaseRecords.DeliveryBuildPath(tree.Root, "4.0.0"));
        Assert.Throws<ReleaseException>(() => ReleaseRecords.DeliveryBuildPath(tree.Root, "4.0.1"));
        tree.Write("CHANGELOG.md", "## [4.0.0]\n");
        tree.Write("manifest/configurator-build.json", "{\"testsPassed\":1,\"executable\":{\"path\":\"TiaOpenness.exe\",\"sha256\":\"unused in no-binaries\"}}");
        tree.Write("manifest/package-manifest.json", "{\"bundleVersion\":\"4.0.0\",\"packageName\":\"TIA_MCP_Delivery_v4.0.0_20261018\"}");
        var delivery = new JsonObject
        {
            ["release"] = "4.0.0", ["engineRelease"] = "4.0.0", ["fileVersion"] = "4.0.0.0",
            [ReleaseRecords.BindingProperty(relative)] = ReleaseRecords.HashFile(Path.Combine(tree.Root, relative)),
            ["configuratorBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, "manifest/configurator-build.json"))
        };
        delivery["multiVersionBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, ReleaseRecords.MultiPath));
        tree.Write("manifest/delivery.json", delivery.ToJsonString());
        ReleaseCommands.ValidateBuildRecords(tree.Root, package: true, noBinaries: true, skipSourceHashes: true);
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString() + " ");
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ValidateBuildRecords(tree.Root, true, true, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryPackageConsumerRequiresRecordedFilesForEitherShape(bool multiOnly)
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        tree.Write(multiOnly ? ReleaseRecords.MultiPath : ReleaseRecords.EnginePath, (multiOnly ? multi : engine).ToJsonString());
        tree.Write("scripts/operations/delivery-files.json", "{\"schemaVersion\":1,\"include\":{\"files\":[\"README.md\"],\"prefixes\":[\"scripts/operations/\",\"manifest/\",\"templates/\",\"runtime/\"]},\"exclude\":{\"files\":[],\"prefixes\":[]},\"legacyCleanup\":{\"files\":[],\"prefixes\":[]},\"requiredFiles\":[\"README.md\"]}");
        tree.Write("README.md", "Synthetic package");
        var oldEntry = "runtime/v21/TiaMcp" + "Server.exe";
        tree.Write(oldEntry, "old package entry");
        tree.Write("manifest/package-manifest.json", new JsonObject
        {
            ["entrypoints"] = new JsonObject { ["mcpServer"] = oldEntry, ["mcpServerArgs"] = new JsonArray() },
            ["cli"] = new JsonObject { ["exe"] = oldEntry }, ["capabilities"] = new JsonObject { ["mcpToolCount"] = 0 }
        }.ToJsonString());
        tree.Write("manifest/tools-list.json", "{\"toolCount\":0,\"tools\":[]}");
        tree.Write("templates/project-blueprints/full_plc_hmi_project.json", "{\"requiredBundleFiles\":[]}");
        Assert.Empty(RepositoryChecks.Check(tree.Root, false, true).Errors);
        File.Delete(Path.Combine(tree.Root, "runtime/v20/worker/TiaMcp.PlcWorker.20.exe"));
        Assert.Contains(RepositoryChecks.Check(tree.Root, false, true).Errors, error => error.Contains("recorded runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheEngineRecordMayBeAbsentFromTheDeliveredRecordSlots()
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        tree.Write("scripts/operations/delivery-files.json", "{\"schemaVersion\":1,\"include\":{\"files\":[\"README.md\",\"" + ReleaseRecords.EnginePath + "\",\"" + ReleaseRecords.MultiPath + "\"],\"prefixes\":[\"scripts/operations/\",\"manifest/\",\"templates/\",\"runtime/\"]},\"exclude\":{\"files\":[],\"prefixes\":[]},\"legacyCleanup\":{\"files\":[],\"prefixes\":[]},\"requiredFiles\":[\"README.md\"]}");
        tree.Write("README.md", "Synthetic package");
        var oldEntry = "runtime/v21/TiaMcp" + "Server.exe";
        tree.Write(oldEntry, "old package entry");
        tree.Write("manifest/package-manifest.json", new JsonObject
        {
            ["entrypoints"] = new JsonObject { ["mcpServer"] = oldEntry, ["mcpServerArgs"] = new JsonArray() },
            ["cli"] = new JsonObject { ["exe"] = oldEntry }, ["capabilities"] = new JsonObject { ["mcpToolCount"] = 0 }
        }.ToJsonString());
        tree.Write("manifest/tools-list.json", "{\"toolCount\":0,\"tools\":[]}");
        tree.Write("templates/project-blueprints/full_plc_hmi_project.json", "{\"requiredBundleFiles\":[]}");
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        Assert.Empty(RepositoryChecks.Check(tree.Root, false, true).Errors);
        // Before P8-04j1b both slots were required; the engine record alone still does not complete the delivery.
        File.Delete(Path.Combine(tree.Root, ReleaseRecords.MultiPath));
        tree.Write(ReleaseRecords.EnginePath, engine.ToJsonString());
        Assert.Contains(RepositoryChecks.Check(tree.Root, false, true).Errors, error => error.Contains(ReleaseRecords.MultiPath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("adapter")]
    [InlineData("host")]
    public void StandaloneMultiRecordRejectsChangedOrUnrecordedPlcArtifacts(string field)
    {
        using var tree = new StaticTree();
        var (_, multi) = Records(tree);
        Validate(tree.Root, multi);
        var artifact = multi["plcRuntimes"]!["V20"]![field]!;
        tree.Write(artifact["path"]!.GetValue<string>(), "changed");
        Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi));
        artifact["sha256"] = new string('0', 64);
        Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi, false));
    }

    [Fact]
    public void MultiRecordCarriesNoEngineCopyAndComparisonLabelsReleaseBuildEvidence()
    {
        // P8-04l: the eight-release record no longer embeds release-build.json; its release-wide counts stay in that record.
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        engine["validation"] = new JsonObject { ["offlinePassed"] = 123 };
        Assert.False(multi.ContainsKey("engineCompatibility"));
        Validate(tree.Root, multi);
        var rows = Compare(engine, multi);
        Assert.DoesNotContain(rows, row => row.Status is "missing" or "different");
        var offline = Assert.Single(rows.Where(row => row.EngineField == "validation.offlinePassed"));
        Assert.Null(offline.MultiField);
        Assert.Contains("release-build.json evidence", offline.Status, StringComparison.Ordinal);
        Assert.Contains(rows, row => row.EngineField == "release" && row.MultiField == "release" && row.Status == "equal");
    }

    [Fact]
    public void PlcRecordWriterHashesProductionSdkHostCatalogAndToolsListIndependently()
    {
        using var tree = new StaticTree();
        _ = Records(tree);
        var logs = Path.Combine(tree.Root, "logs");
        tree.Write("manifest/tools-list.json", "{\"tools\":[]}");
        foreach (var key in new[] { "20", "21" })
        {
            tree.Write("logs/tools-" + key + ".json", "{\"tools\":[]}");
            var worker = new PlcWorkerFixtureBuild(key, "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));
            tree.Write(Path.GetRelativePath(tree.Root, worker), "SDK fixture " + key);
        }
        var proof = ReleaseRecords.PlcProof(tree.Root, logs, new("package", [], [], []));
        foreach (var key in new[] { "20", "21" })
        {
            Assert.NotEqual(proof["V" + key]!["worker"]!["sha256"]!.GetValue<string>(), proof["V" + key]!["sdkWorker"]!["sha256"]!.GetValue<string>());
            Assert.Equal(ReleaseRecords.HashFile(Path.Combine(tree.Root, "manifest/tools-list.json")), proof["V" + key]!["toolsList"]!["sha256"]!.GetValue<string>());
            Assert.Contains("TiaMcp.PlcWorker.", proof["V" + key]!["worker"]!["path"]!.GetValue<string>());
        }
    }

    [Fact]
    public void PythonAndCSharpRequireTheSameConcretePlcReceipts()
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var code = "import importlib.util,json,sys; s=importlib.util.spec_from_file_location('layout',sys.argv[1]); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); print(json.dumps({k:m.plc_record_jobs(k) for k in ('20','21')}))";
        var result = ProcessRunner.Run(OperatingSystem.IsWindows() ? "python" : "python3", ["-c", code, Path.Combine(root, "scripts/checks/Check-BundleLayout.py")], root,
            new Dictionary<string, string?> { ["PYTHONDONTWRITEBYTECODE"] = "1" });
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var python = JsonDocument.Parse(result.StandardOutput);
        var jobs = ReleaseCommands.PlcReleaseJobs(root, "", "", "", "dotnet", "python");
        Assert.Equal(78, jobs.Count);
        foreach (var key in new[] { "20", "21" })
        {
            var expected = jobs.Where(job => job.Release == key).ToDictionary(job => job.Name, job => job.Check);
            var actual = python.RootElement.GetProperty(key).EnumerateObject().ToDictionary(row => row.Name, row => row.Value.GetString()!);
            Assert.Equal(expected.OrderBy(row => row.Key), actual.OrderBy(row => row.Key));
        }
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("changed-receipt")]
    [InlineData("unbound-receipt")]
    [InlineData("receipt-without-counts")]
    [InlineData("local-path-in-counts")]
    [InlineData("wrong-sdk-runtime")]
    [InlineData("engine-worker-model")]
    [InlineData("missing-matrix-run")]
    public void PlcStabilityReceiptsKeepTheirRuntimeAndMatrixGates(string mutation)
    {
        using var tree = new StaticTree();
        var (_, multi) = Records(tree);
        var plan = new ReleaseCheckPlan("quick", ["plc-stability"], [], []);
        multi["checkPlan"] = JsonSerializer.SerializeToNode(plan, JsonOptions);
        multi["tier"] = "quick";
        foreach (var key in new[] { "20", "21" })
        {
            var runtime = multi["plcRuntimes"]!["V" + key]!;
            var proof = new JsonObject
            {
                ["status"] = "passed", ["rounds"] = 50, ["isolatedWorker"] = true, ["workerModel"] = "foundation-host-plc-worker",
                ["runtimeSha256"] = runtime["sdkWorker"]!["sha256"]!.DeepClone(), ["sdkWorkerSha256"] = runtime["sdkWorker"]!["sha256"]!.DeepClone(),
                ["productionWorkerSha256"] = runtime["worker"]!["sha256"]!.DeepClone(), ["hostSha256"] = runtime["host"]!["sha256"]!.DeepClone(),
                ["runs"] = JsonSerializer.SerializeToNode(new[] { "stdio", "http" }.SelectMany(transport => new[] { "full", "lite" }.Select(profile =>
                    new { transport, profile, unexpectedExits = 0, unexpectedFailures = 0, unmatchedJournalEntries = 0, measuredToolCalls = 1, tiaConnected = false })))
            };
            runtime["localStability"] = proof;
            runtime["checks"]!["stability-c8-v" + key + "-plc"] = Receipt(multi, "stability-c8-v" + key + "-plc", "COMPLETE: V" + key + " 5504 local tool calls passed\n");
        }
        Validate(tree.Root, multi);
        var v20 = multi["plcRuntimes"]!["V20"]!;
        switch (mutation)
        {
            case "missing-receipt": v20["checks"]!.AsObject().Remove("stability-c8-v20-plc"); break;
            case "changed-receipt": v20["checks"]!["stability-c8-v20-plc"]!["sha256"] = Hash("changed log"); break;
            case "unbound-receipt": multi["validationArtifacts"]!.AsArray().RemoveAt(0); break;
            case "receipt-without-counts": v20["checks"]!["stability-c8-v20-plc"]!.AsObject().Remove("counts"); break;
            case "local-path-in-counts": v20["checks"]!["stability-c8-v20-plc"]!["counts"] = new JsonArray("COMPLETE: 4 runs; evidence: " + Path.Combine(tree.Root, "bin-build")); break;
            case "wrong-sdk-runtime": v20["localStability"]!["runtimeSha256"] = new string('0', 64); break;
            case "engine-worker-model": v20["localStability"]!["workerModel"] = "combined-host-supervised-worker"; break;
            case "missing-matrix-run": v20["localStability"]!["runs"]!.AsArray().RemoveAt(0); break;
        }
        Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi));
    }

    [Fact]
    public void ComparisonCommandReportsTheExactMissingFieldAndFails()
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        var enginePath = tree.Write(ReleaseRecords.EnginePath, engine.ToJsonString());
        var multiPath = tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        var options = Options.Parse(["-EngineRecord", enginePath, "-MultiRecord", multiPath], new HashSet<string>());
        Assert.Equal(0, ReleaseCommands.Run("compare-release-records", options));
        multi["plcRuntimes"]!["V21"]!["worker"]!.AsObject().Remove("sha256");
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        using var left = JsonDocument.Parse(engine.ToJsonString());
        using var right = JsonDocument.Parse(multi.ToJsonString());
        Assert.Contains(ReleaseRecords.Compare(left.RootElement, right.RootElement), row => row.MultiField == "plcRuntimes.V21.worker.sha256" && row.Status == "missing");
        Assert.Equal(1, ReleaseCommands.Run("compare-release-records", options));
    }

    [Fact]
    public void ComparisonRequiresTheSameReleasePlanUpdaterAndRuntimeFiles()
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        Assert.DoesNotContain(Compare(engine, multi), row => row.Status is "missing" or "different");
        foreach (var field in new[] { "release", "fileVersion", "tier" })
        {
            var changed = (JsonObject)engine.DeepClone(); changed[field] = "other";
            Assert.Contains(Compare(changed, multi), row => row.EngineField == field && row.Status == "different");
        }
        var plan = (JsonObject)engine.DeepClone();
        plan["checkPlan"] = JsonSerializer.SerializeToNode(new ReleaseCheckPlan("quick", ["plc-functional"], [], []), JsonOptions);
        Assert.Contains(Compare(plan, multi), row => row.EngineField.StartsWith("checkPlan.", StringComparison.Ordinal) && row.Status == "different");
        var updater = new JsonArray(new JsonObject { ["path"] = "runtime/tools/TiaMcp.Updater.exe", ["sha256"] = Hash("updater"), ["length"] = 7 });
        var withUpdater = (JsonObject)engine.DeepClone(); withUpdater["updaterFiles"] = updater.DeepClone();
        Assert.Contains(Compare(withUpdater, multi), row => row.EngineField.StartsWith("updaterFiles[", StringComparison.Ordinal) && row.Status == "missing");
        multi["updaterFiles"] = updater.DeepClone();
        Assert.DoesNotContain(Compare(withUpdater, multi), row => row.Status is "missing" or "different");
        // A release-build runtime file must be in the eight-release inventory with the same bytes.
        var file = (JsonObject)engine["runtimeFiles"]![0]!;
        file["sha256"] = new string('0', 64);
        Assert.Contains(Compare(engine, multi), row => row.EngineField == "runtimeFiles[0].sha256" && row.Status == "different");
        file["path"] = "runtime/tools/Unrecorded.exe";
        Assert.Contains(Compare(engine, multi), row => row.EngineField == "runtimeFiles[0].path" && row.Status == "missing");
    }

    [Fact]
    public void CompletionBindsTheToolsListThatBuildReleaseShips()
    {
        using var tree = new StaticTree();
        var (_, multi) = Records(tree);
        tree.Write("manifest/tools-list.json", "{\"generatedAt\":\"after build-release\",\"tools\":[]}");
        using var runtimes = JsonDocument.Parse(multi["plcRuntimes"]!.ToJsonString());
        var bound = ReleaseRecords.WithShippedToolsList(tree.Root, runtimes.RootElement);
        foreach (var key in new[] { "V20", "V21" })
        {
            Assert.Equal(ReleaseRecords.HashFile(Path.Combine(tree.Root, "manifest/tools-list.json")), bound[key]!["toolsList"]!["sha256"]!.GetValue<string>());
            Assert.Equal(multi["plcRuntimes"]![key]!["worker"]!.ToJsonString(), bound[key]!["worker"]!.ToJsonString());
        }
        multi["plcRuntimes"] = bound;
        Validate(tree.Root, multi);
        tree.Write("manifest/tools-list.json", "{\"generatedAt\":\"changed later\",\"tools\":[]}");
        Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi));
    }

    private static IReadOnlyList<ReleaseRecords.FieldComparison> Compare(JsonObject engine, JsonObject multi)
    {
        using var left = JsonDocument.Parse(engine.ToJsonString());
        using var right = JsonDocument.Parse(multi.ToJsonString());
        return ReleaseRecords.Compare(left.RootElement, right.RootElement);
    }

    [Fact]
    public void ComparisonCommandIsRegisteredForTheChildToolCalls()
    {
        // 02-compare-release-records and the release chain start it as a child release tool, which answers 64 for an
        // unregistered command before dispatch.
        Assert.Contains("compare-release-records", CommandLine.Commands);
    }

    [Fact]
    public void DryRunComparesEveryRecordThatCarriesThePlcProof()
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        var enginePath = tree.Write(ReleaseRecords.EnginePath, engine.ToJsonString());
        var before = (JsonObject)multi.DeepClone();
        before.Remove("plcRuntimes");
        var multiPath = tree.Write(ReleaseRecords.MultiPath, before.ToJsonString());
        var options = Options.Parse(["-EngineRecord", enginePath, "-MultiRecord", multiPath], new HashSet<string>());
        // A record written before the PLC proof: the command fails, the dry run names it and continues.
        Assert.Equal(1, ReleaseCommands.Run("compare-release-records", options));
        Assert.Equal(0, ReleaseCommands.DryRunRecordComparison(options));
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        Assert.Equal(0, ReleaseCommands.DryRunRecordComparison(options));
        multi["plcRuntimes"]!["V20"]!["host"]!.AsObject().Remove("sha256");
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        Assert.Equal(1, ReleaseCommands.DryRunRecordComparison(options));
    }

    [Theory]
    [InlineData("build-tools/Package-Release.py")]
    [InlineData("build-tools/Package-MultiVersion.py")]
    [InlineData("scripts/checks/Check-BundleLayout.py")]
    [InlineData("scripts/checks/Check-Repository.py")]
    [InlineData("scripts/checks/Verify-ReleaseAsset.py")]
    public void PythonConsumersRunTheirCompleteSyntheticSelfTests(string script)
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var result = ProcessRunner.Run(OperatingSystem.IsWindows() ? "python" : "python3",
            ["-X", "utf8", Path.Combine(root, script), "--self-test"], root,
            new Dictionary<string, string?> { ["PYTHONDONTWRITEBYTECODE"] = "1" });
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    [Fact]
    public void PlcRecordWriterKeepsCountLinesAndNoPathOfTheCheckoutOrUserProfile()
    {
        using var tree = new StaticTree();
        _ = Records(tree);
        var logs = Path.Combine(tree.Root, "bin-build/multi-version");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.GetTempPath().TrimEnd('\\', '/');
        tree.Write("manifest/tools-list.json", "{\"tools\":[]}");
        foreach (var key in new[] { "20", "21" })
        {
            tree.Write("bin-build/multi-version/tools-" + key + ".json", "{\"tools\":[]}");
            var worker = new PlcWorkerFixtureBuild(key, "sdk", null).Worker(Path.Combine(logs, "plc-fixtures"));
            tree.Write(Path.GetRelativePath(tree.Root, worker), "SDK fixture " + key);
            tree.Write($"bin-build/multi-version/stability-c8-v{key}-plc.log",
                "Running in " + tree.Root + "\r\nuncounted line " + profile + "\r\nCOMPLETE: V" + key + " 5504 local tool calls passed; evidence: " +
                Path.Combine(logs, "stability-c8-v" + key + "-plc") + " temp " + Path.Combine(temp, "x") + " sdk D:/Other/sdk\r\n");
            var run = new JsonObject { ["image"] = Path.Combine(tree.Root, "runtime", "v" + key, "TiaMcp.FoundationHost.exe"), ["cwd"] = profile.Replace('\\', '/') };
            tree.Write($"bin-build/multi-version/stability-c8-v{key}-plc/result.json", new JsonObject
            { ["status"] = "passed", ["rounds"] = 50, ["runs"] = new JsonArray(run), ["output"] = @"\\?\" + Path.Combine(tree.Root, "bin-build") }.ToJsonString());
        }
        var plan = new ReleaseCheckPlan("quick", ["plc-stability"], [], []);
        var proof = ReleaseRecords.PlcProof(tree.Root, logs, plan);
        var parsed = JsonNode.Parse(proof.ToJsonString())!;
        // Every string of the proof, unescaped: none names the checkout, the user profile, the temp folder or any other absolute path.
        var strings = new List<string>();
        void Collect(JsonNode? node)
        {
            if (node is JsonObject value) foreach (var pair in value) Collect(pair.Value);
            else if (node is JsonArray items) foreach (var item in items) Collect(item);
            else if (node is JsonValue leaf && leaf.TryGetValue<string>(out var textValue)) strings.Add(textValue);
        }
        Collect(parsed);
        foreach (var local in new[] { tree.Root, profile, temp })
            Assert.DoesNotContain(strings, value => value.Replace('/', '\\').Contains(local.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(strings, ReleaseRecords.HasLocalPath);
        foreach (var key in new[] { "20", "21" })
        {
            var receipt = parsed["V" + key]!["checks"]!["stability-c8-v" + key + "-plc"]!;
            var log = Path.Combine(logs, "stability-c8-v" + key + "-plc.log");
            Assert.Equal($"bin-build/multi-version/stability-c8-v{key}-plc.log", receipt["path"]!.GetValue<string>());
            Assert.Equal(ReleaseRecords.HashFile(log), receipt["sha256"]!.GetValue<string>());
            Assert.Equal(new FileInfo(log).Length, receipt["length"]!.GetValue<long>());
            Assert.Null(receipt["output"]);
            Assert.Equal([$"COMPLETE: V{key} 5504 local tool calls passed; evidence: ./bin-build/multi-version/stability-c8-v{key}-plc temp %TEMP%/x sdk <local path>"],
                receipt["counts"]!.AsArray().Select(line => line!.GetValue<string>().Replace('\\', '/')));
            var soak = parsed["V" + key]!["localStability"]!;
            Assert.Equal($"./runtime/v{key}/TiaMcp.FoundationHost.exe", soak["runs"]![0]!["image"]!.GetValue<string>().Replace('\\', '/'));
            Assert.Equal("%USERPROFILE%", soak["runs"]![0]!["cwd"]!.GetValue<string>());
            Assert.Equal(proof["V" + key]!["worker"]!["sha256"]!.GetValue<string>(), soak["productionWorkerSha256"]!.GetValue<string>());
        }
        Assert.Equal(new[] { "20", "21" }.Select(key => Path.Combine(logs, "stability-c8-v" + key + "-plc.log")), ReleaseRecords.PlcProofLogs(tree.Root, logs, plan));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRecordedTestFlagSelectsTheRequiredReceipts(bool test)
    {
        using var tree = new StaticTree();
        var (engine, multi) = Records(tree);
        // A plan with a PLC group and foundation-approval: -Test runs every selected successor, without -Test only the approval ones.
        var plan = new ReleaseCheckPlan("quick", ["foundation-approval", "plc-worker-isolation"], [], []);
        foreach (var record in new[] { multi, engine })
        { record["checkPlan"] = JsonSerializer.SerializeToNode(plan, JsonOptions); record["tier"] = "quick"; }
        multi["test"] = test;
        foreach (var key in new[] { "20", "21" })
            multi["plcRuntimes"]!["V" + key]!["checks"]!["approval-v" + key + "-plc"] = Receipt(multi, "approval-v" + key + "-plc", "COMPLETE: 7 approval/readiness checks passed\n");
        if (test) Assert.Contains("worker-robustness-v20-plc", Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi)).Message, StringComparison.Ordinal);
        else Validate(tree.Root, multi);
        Assert.Equal(test ? ["foundation-approval", "plc-worker-isolation"] : ["plc-approval"], ReleaseRecords.PlcProofPlan(plan, test).SelectedChecks);
        foreach (var key in new[] { "20", "21" })
            foreach (var name in new[] { "worker-robustness", "worker-protocol" })
                multi["plcRuntimes"]!["V" + key]!["checks"]![name + "-v" + key + "-plc"] = Receipt(multi, name + "-v" + key + "-plc", "COMPLETE: 717 checks passed\n");
        Validate(tree.Root, multi);
        Assert.DoesNotContain(Compare(engine, multi), row => row.Status is "missing" or "different");
        multi["plcRuntimes"]!["V21"]!["checks"]!.AsObject().Remove("approval-v21-plc");
        Assert.Throws<ReleaseException>(() => Validate(tree.Root, multi));
    }

    [Theory]
    [InlineData("4.0.0", "4.0.0.0", true)]
    [InlineData("4.0.0-beta.1", "4.0.0.0", true)]
    [InlineData("4.0.0-rc.2", "4.0.0.0", true)]
    [InlineData("4.0.0-beta.1", "4.0.0-beta.1.0", false)]
    [InlineData("4.0.0-beta.1", "4.0.1.0", false)]
    public void DeliveryBindsAPreReleaseRecordByItsFileVersion(string release, string fileVersion, bool accepted)
    {
        using var tree = new StaticTree();
        // The package binds the eight-release record and its PlcWorkers (P8-04j2); Records writes both workers.
        var (_, multi) = Records(tree);
        multi["release"] = release;
        multi["fileVersion"] = fileVersion;
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        if (accepted) Assert.Equal(Path.Combine(tree.Root, ReleaseRecords.MultiPath), ReleaseRecords.DeliveryBuildPath(tree.Root, release));
        else Assert.Throws<ReleaseException>(() => ReleaseRecords.DeliveryBuildPath(tree.Root, release));
    }

    [Fact]
    public void ReleaseBuildRecordPassesTheReuseGateWithReleaseWideEvidenceOnly()
    {
        // P8-04l: release-build.json holds the release-wide checks and tools; the engine runtimes, soaks and audit inputs are gone.
        using var tree = new StaticTree();
        var repository = TiaMcp.BuildCommon.Repository.FindRoot();
        tree.Write("build-tools/release/release-checks.json", File.ReadAllText(Path.Combine(repository, "build-tools/release/release-checks.json")));
        tree.Write("Version.props", "<Project />");
        var runtimeFiles = new JsonArray();
        foreach (var relative in new[] { "runtime/tools/TiaMcp.WriteGuard.exe", "runtime/verification/NativeCallWeaver.dll" })
        {
            tree.Write(relative, "built " + relative);
            runtimeFiles.Add(new JsonObject { ["path"] = relative, ["sha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, relative)) });
        }
        var policy = ReleaseCheckPolicy.Load(tree.Root);
        var plan = new ReleaseCheckPlan("quick", ["preflight", "binary-build", "offline-suites", "native-coverage", "resource-discovery", "product-smoke", "relocation", "package", "bundle-validation"], [], []);
        plan = plan with { SkippedChecks = policy.Checks.Except(plan.SelectedChecks, StringComparer.Ordinal).ToArray() };
        var validation = new JsonObject
        {
            ["offlinePassed"] = 303, ["offlineV20Passed"] = 303, ["keptCodePassed"] = 4854, ["keptCodeV20Passed"] = 4854, ["versionPolicySdkPassed"] = 10,
            ["writeGuardPassed"] = 8, ["crashEvidencePassed"] = 7, ["updaterPassed"] = 33, ["nativeMcpSafetyPassed"] = 8
        };
        var record = new JsonObject
        {
            ["tier"] = "quick", ["checkPlan"] = JsonSerializer.SerializeToNode(plan, JsonOptions), ["release"] = "4.0.0", ["fileVersion"] = "4.0.0.0",
            ["validation"] = validation, ["runtimeFiles"] = runtimeFiles, ["validationArtifacts"] = new JsonArray(),
            ["sourceFiles"] = JsonSerializer.SerializeToNode(ReleaseRecords.GetSources(tree.Root, "engine").Select(row => new { path = row.Path, sha256 = row.Sha256 })),
            ["validationInputs"] = JsonSerializer.SerializeToNode(ReleaseRecords.GetValidationInputs(tree.Root, "engine").Select(row => new { path = row.Path, sha256 = row.Sha256 }))
        };
        string Reason()
        {
            using var parsed = JsonDocument.Parse(record.ToJsonString());
            return ReleaseRecords.EngineReuseReason(tree.Root, "4.0.0", parsed.RootElement, ReleaseRecords.GetSources(tree.Root, "engine"), "quick");
        }
        Assert.Equal("", Reason());
        // The release-wide native MCP safety count replaced each engine runtime's sessionStability gate.
        validation["nativeMcpSafetyPassed"] = 7;
        Assert.Equal("offline validation missing", Reason());
        validation["nativeMcpSafetyPassed"] = 8;
        tree.Write("runtime/tools/Unrecorded.dll", "new");
        Assert.Equal("runtime inventory changed (added/removed binary)", Reason());
        File.Delete(Path.Combine(tree.Root, "runtime/tools/Unrecorded.dll"));
        record["validationArtifacts"] = new JsonArray(new JsonObject { ["path"] = "bin-build/releases/v4.0.0/missing.json", ["sha256"] = Hash("x") });
        Assert.StartsWith("audit evidence missing", Reason(), StringComparison.Ordinal);
        var pipeline = File.ReadAllText(Path.Combine(repository, "build-tools/release/BuildReleasePipeline.cs"));
        Assert.DoesNotContain("runtimes = runtimeRecords", pipeline, StringComparison.Ordinal);
        Assert.Contains("nativeMcpSafetyPassed = GetJsonInt(common, \"nativeMcpSafetyChecksPassed\")", pipeline, StringComparison.Ordinal);
    }

    // A receipt as PlcProof writes it, with its log bound in the record's validationArtifacts.
    private static JsonObject Receipt(JsonObject record, string job, string log)
    {
        var path = $"bin-build/multi-version/{job}.log";
        if (record["validationArtifacts"] is not JsonArray artifacts) record["validationArtifacts"] = artifacts = new JsonArray();
        artifacts.Add(new JsonObject { ["path"] = path, ["sha256"] = Hash(log) });
        return new JsonObject
        {
            ["path"] = path, ["sha256"] = Hash(log), ["length"] = log.Length, ["status"] = "passed",
            ["counts"] = new JsonArray(log.Split('\n').Where(line => line.Contains("COMPLETE", StringComparison.Ordinal)).Select(line => (JsonNode?)JsonValue.Create(line)).ToArray())
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static (JsonObject Engine, JsonObject Multi) Records(StaticTree tree)
    {
        var plan = new ReleaseCheckPlan("package", [], [], []);
        var engine = new JsonObject
        {
            ["release"] = "4.0.0", ["fileVersion"] = "4.0.0.0", ["tier"] = "package", ["checkPlan"] = JsonSerializer.SerializeToNode(plan, JsonOptions),
            ["sourceFiles"] = new JsonArray(), ["validationInputs"] = new JsonArray(), ["validation"] = new JsonObject()
        };
        var files = new JsonArray();
        var runtimes = new JsonObject();
        foreach (var key in new[] { "20", "21" })
        {
            var runtime = new JsonObject { ["checks"] = new JsonObject() };
            foreach (var (field, name) in new[] { ("worker", $"worker/TiaMcp.PlcWorker.{key}.exe"), ("adapter", $"worker/TiaMcp.Adapter.{key}.dll"), ("host", "TiaMcp.FoundationHost.exe") })
            {
                var path = $"runtime/v{key}/{name}";
                tree.Write(path, "synthetic " + key + field);
                var artifact = new JsonObject { ["path"] = path, ["sha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, path)) };
                runtime[field] = artifact;
                files.Add(artifact.DeepClone());
            }
            foreach (var field in new[] { "sdkWorker", "catalog", "toolsList" }) runtime[field] = new JsonObject { ["path"] = field, ["sha256"] = Hash(field) };
            runtimes["V" + key] = runtime;
        }
        engine["runtimeFiles"] = files.DeepClone();
        var multi = (JsonObject)engine.DeepClone();
        multi.Remove("runtimeFiles");
        multi["files"] = files;
        multi["plcRuntimes"] = runtimes;
        return (engine, multi);
    }

    private static void Validate(string root, JsonObject record, bool binaries = true)
    {
        using var doc = JsonDocument.Parse(record.ToJsonString());
        ReleaseRecords.AssertPlcProof(root, doc.RootElement, binaries);
    }
}
