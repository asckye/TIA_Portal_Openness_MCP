using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class BinaryCheckReceipts
{
    internal const string Schema = "tia-mcp/binary-check-receipt/1";

    internal static JsonObject Binaries(IReadOnlyDictionary<string, string> files)
    {
        var result = new JsonObject();
        foreach (var (role, executable) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var directory = Path.GetDirectoryName(executable)!;
            if (!File.Exists(executable)) throw new ReleaseException("Binary receipt input missing: " + executable);
            result[role] = new JsonObject(BuildReproducibility.Inventory(directory)
                // A packaged host also has a worker/ subdirectory. The explicit worker
                // role binds the SDK worker this job actually launches; unrelated sibling
                // products and PDBs do not form part of the host's load directory.
                .Where(row => !row.Path.Contains('/') && (Path.GetExtension(row.Path).ToLowerInvariant() is ".exe" or ".dll" or ".config" ||
                    row.Path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || row.Path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
                .Select(row => KeyValuePair.Create(row.Path, (JsonNode?)JsonValue.Create(row.Sha256))));
        }
        return result;
    }

    internal static bool Matches(JsonObject receipt, JsonObject binaries) =>
        ReleaseEvidence.Text(receipt["schema"]) == Schema && ReleaseEvidence.Text(receipt["status"]) == "passed" &&
        JsonNode.DeepEquals(receipt["binaries"], binaries);

    internal static CheckJob Bind(CheckJob job, string root, string output, IReadOnlyDictionary<string, string> binaries)
    {
        return job with { Run = () =>
        {
            var directory = Path.Combine(output, "binary-receipts"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, job.Name + ".json"); File.Delete(path);
            var before = Binaries(binaries);
            var result = job.Run();
            if (result.ExitCode != 0) return result;
            if (!JsonNode.DeepEquals(before, Binaries(binaries))) throw new ReleaseException("Check input bytes changed during " + job.Name);
            var artifacts = new List<string>();
            var checkOutput = Path.Combine(output, job.Name);
            if (Directory.Exists(checkOutput)) artifacts.AddRange(Directory.GetFiles(checkOutput, "*.json", SearchOption.AllDirectories));
            if (File.Exists(checkOutput + ".json")) artifacts.Add(checkOutput + ".json");
            var receipt = new JsonObject
            {
                ["schema"] = Schema, ["name"] = job.Name, ["status"] = "passed", ["runId"] = Path.GetFileName(output), ["binaries"] = before,
                ["artifacts"] = new JsonArray(artifacts.Order(StringComparer.Ordinal).Select(file => (JsonNode?)new JsonObject
                    { ["path"] = Path.GetRelativePath(output, file).Replace('\\', '/'), ["sha256"] = ReleaseRecords.HashFile(file), ["length"] = new FileInfo(file).Length }).ToArray())
            };
            File.WriteAllText(path, receipt.ToJsonString() + "\n", new System.Text.UTF8Encoding(false));
            return result;
        } };
    }

    internal static bool TryAdopt(string evidencePath, PlcReleaseJob job, string root, string logs, string harness, JsonObject decisions)
    {
        var source = ReleaseCandidate.Read(evidencePath);
        var directory = Path.GetDirectoryName(evidencePath)!;
        var key = job.Release;
        var names = job.Check is "plc-stability" or "plc-isolated-stability"
            ? (from transport in new[] { "stdio", "http" } from profile in new[] { "full", "lite" }
               select $"stability-v{key}-c{(job.Check == "plc-stability" ? "8" : "16")}-{transport}-{profile}-plc").ToArray()
            : job.Mode == "worker-protocol" ? new[] { "transport-usage-v" + key + "-plc" }
            : job.Mode == "capture" && job.Arguments.Contains("stdio", StringComparer.Ordinal)
                ? new[] { (job.Check == "plc-contracts" ? "contracts" : "responses") + "-v" + key + "-plc" }
            : new[] { job.Name };
        var requestedHost = Path.Combine(root, $"runtime/v{key}/TiaMcp.FoundationHost.exe");
        if (job.Mode == "capture") requestedHost = job.Arguments[Array.IndexOf(job.Arguments, "--engine-host") + 1];
        var binaries = Binaries(new Dictionary<string, string>
        {
            ["host"] = requestedHost,
            ["worker"] = new PlcWorkerFixtureBuild(key, "sdk", null).Worker(Path.Combine(logs, "plc-fixtures")), ["harness"] = harness
        });
        var receipts = new List<JsonObject>();
        var copies = new List<(string Source, string Relative)>();
        foreach (var name in names)
        {
            if (source["binaryReceipts"]?[name] is not JsonObject binding) return Fallback("binary receipt missing: " + name);
            ReleaseCandidate.VerifyBinding(directory, binding);
            var receipt = ReleaseCandidate.Read(ReleaseCandidate.Resolve(directory, ReleaseEvidence.Text(binding["path"])));
            if (!Matches(receipt, binaries)) return Fallback("binary dependency hashes differ: " + name);
            var sourceJob = source["jobs"]!.AsArray().Single(row => ReleaseEvidence.Text(row?["name"]) == name)!;
            if (ReleaseEvidence.Text(sourceJob["status"]) != "passed" || ReleaseEvidence.Bool(sourceJob["reused"])) return Fallback("source job is not a fresh pass");
            var log = Path.Combine(directory, "logs", name + ".log");
            if (!File.Exists(log) || ReleaseRecords.HashFile(log) != ReleaseEvidence.Text(sourceJob["logSha256"]))
                throw new ReleaseException("Adopted binary job log changed: " + name);
            foreach (var artifact in receipt["artifacts"]!.AsArray())
            {
                ReleaseCandidate.VerifyBinding(directory, artifact);
                var relative = ReleaseEvidence.Text(artifact!["path"]);
                copies.Add((ReleaseCandidate.Resolve(directory, relative), relative));
            }
            receipts.Add(receipt);
        }
        var target = job.Mode == "capture" ? job.Evidence : Path.Combine(logs, job.Mode == "worker-protocol" ? "transport-v" + key + "-plc" : job.Name);
        Directory.CreateDirectory(target);
        var resultFiles = copies.Where(copy => Path.GetFileName(copy.Source) == "result.json").ToArray();
        if (job.Mode == "capture")
        {
            var captures = copies.Where(copy => Path.GetFileName(copy.Source) == key + ".json").ToArray();
            if (captures.Length != 1) return Fallback("source capture missing");
            File.Copy(captures[0].Source, Path.Combine(target, key + ".json"), true);
            var baseline = Path.Combine(root, "manifest/contracts/v4", job.Check == "plc-contracts" ? "baseline" : "responses", key + ".json");
            if (!File.ReadAllBytes(baseline).SequenceEqual(File.ReadAllBytes(captures[0].Source))) throw new ReleaseException("Adopted capture bytes differ");
        }
        else
        {
            if (resultFiles.Length != names.Length) return Fallback("source binary job result matrix missing");
            JsonObject result;
            if (names.Length == 4)
            {
                result = ReleaseCandidate.Read(resultFiles[0].Source);
                var runs = new JsonArray();
                foreach (var file in resultFiles)
                {
                    var shard = ReleaseCandidate.Read(file.Source);
                    if (ReleaseEvidence.Text(shard["status"]) != "passed" || ReleaseEvidence.Int(shard["rounds"]) != 50 || shard["runs"]!.AsArray().Count != 1)
                        throw new ReleaseException("Adopted stability shard is incomplete");
                    runs.Add(shard["runs"]![0]!.DeepClone());
                }
                result["runs"] = runs;
            }
            else result = ReleaseCandidate.Read(resultFiles[0].Source);
            File.WriteAllText(Path.Combine(target, "result.json"), result.ToJsonString() + "\n");
        }
        var text = string.Join("\n", names.Select(name => File.ReadAllText(Path.Combine(directory, "logs", name + ".log"))));
        File.WriteAllText(Path.Combine(logs, job.Name + ".log"), text);
        ReleaseCommands.ValidatePlcReleaseJob(job, new(0, text, ""), root, logs);
        decisions[job.Name] = new JsonObject
        {
            ["mode"] = "adopted-by-binary-hash", ["sourceRunId"] = source["runId"]!.DeepClone(),
            ["sourceJobs"] = ReleaseEvidence.Strings(names), ["binaries"] = binaries,
            ["receipts"] = new JsonArray(receipts.Select(row => (JsonNode?)row.DeepClone()).ToArray())
        };
        return true;

        bool Fallback(string reason)
        {
            decisions[job.Name] = new JsonObject { ["mode"] = "ran", ["reason"] = reason, ["binaries"] = binaries.DeepClone() };
            return false;
        }
    }

    internal static bool TryAdoptLocal(string evidencePath, string sourceName, string targetName, string logs,
        IReadOnlyDictionary<string, string>? binaries, out JsonObject receipt, string? artifactOutput = null)
    {
        var evidence = ReleaseCandidate.Read(evidencePath);
        var directory = Path.GetDirectoryName(evidencePath)!;
        receipt = new JsonObject();
        var job = evidence["jobs"]!.AsArray().SingleOrDefault(row => ReleaseEvidence.Text(row?["name"]) == sourceName);
        if (ReleaseEvidence.Text(job?["status"]) != "passed" || ReleaseEvidence.Bool(job?["reused"])) return false;
        if (binaries is not null)
        {
            if (evidence["binaryReceipts"]?[sourceName] is not JsonObject binding) return false;
            ReleaseCandidate.VerifyBinding(directory, binding);
            var bound = ReleaseCandidate.Read(ReleaseCandidate.Resolve(directory, ReleaseEvidence.Text(binding["path"])));
            if (!Matches(bound, Binaries(binaries))) return false;
            foreach (var artifact in bound["artifacts"]!.AsArray())
            {
                ReleaseCandidate.VerifyBinding(directory, artifact);
                if (artifactOutput is null) continue;
                Directory.CreateDirectory(artifactOutput);
                var sourcePath = ReleaseCandidate.Resolve(directory, ReleaseEvidence.Text(artifact!["path"]));
                var name = Path.GetFileName(sourcePath).Replace(sourceName, targetName, StringComparison.Ordinal);
                // Both coverage aliases consume the one fixture adapter inventory.
                if (targetName.StartsWith("adapter-native-coverage-", StringComparison.Ordinal) || targetName.StartsWith("jit-adapter-coverage-", StringComparison.Ordinal))
                    name = "adapter-coverage-v" + targetName.Split("-v")[1] + Path.GetExtension(sourcePath);
                File.Copy(sourcePath, Path.Combine(artifactOutput, name), true);
            }
            receipt["binaries"] = bound["binaries"]!.DeepClone();
        }
        var log = Path.Combine(directory, "logs", sourceName + ".log");
        if (!File.Exists(log) || ReleaseRecords.HashFile(log) != ReleaseEvidence.Text(job?["logSha256"])) throw new ReleaseException("Adopted local check log changed");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, targetName + ".log"); File.Copy(log, target, true);
        receipt["name"] = targetName; receipt["status"] = "passed"; receipt["mode"] = "adopted-by-hash";
        receipt["sourceRunId"] = evidence["runId"]!.DeepClone(); receipt["sourceJob"] = sourceName;
        receipt["sha256"] = ReleaseRecords.HashFile(target);
        return true;
    }
}

internal static partial class ReleaseCommands
{
    private static void AddAdoptionEvidenceJobs(List<CheckJob> jobs, string output, string api, string nuget, string dotnet, string manifest,
        Func<string, string, string[], bool, CommandResult> isolated)
    {
        foreach (var major in new[] { "20", "21" })
        {
            var key = major; var name = "host-behavior-parity-v" + key;
            jobs.Add(new(name, () => HostParity.Run(Root, Options.Parse(["-EngineMajor", key, "-Dotnet", dotnet, "-NoBuild",
                "-RuntimeManifest", manifest, "-ResultsDirectory", Path.Combine(output, name)], new HashSet<string> { "NoBuild" }),
                FastSuites.EnvironmentFor(Path.Combine(output, "isolation", name), api)), []));
            var native = "native-safety-v" + key + "-plc";
            jobs.Add(BinaryCheckReceipts.Bind(new(native, () =>
            {
                var result = isolated(native, Path.Combine(Root, $"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/bin/Release/net48/TiaMcp.Engine.Native.Tests.V{key}.exe"), ["--self-test"], true);
                if (result.ExitCode == 0) RequireCount(native, result.StandardOutput, "COMPLETE: (\\d+) native harness safety checks passed; live TIA tests NOT RUN");
                return result;
            }, []), Root, output, new Dictionary<string, string> { ["safety"] = Path.Combine(Root, $"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/bin/Release/net48/TiaMcp.Engine.Native.Tests.V{key}.exe") }));
            var coverage = "native-coverage-v" + key + "-plc";
            var production = CheckAllBuild.TestAssembly(Root,
                new("src/PlcWorker/TiaMcp.PlcWorker.csproj", "", ["-p:TiaReleaseKey=" + key,
                    "-p:SiemensEngineeringDirectory=" + Path.GetFullPath(Path.Combine(api, PlcWorkerApi(key).Directory))]), manifest)!;
            jobs.Add(BinaryCheckReceipts.Bind(new(coverage, () => isolated(coverage, dotnet,
                [Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"), "verify",
                 Path.Combine(Path.GetDirectoryName(production)!, $"TiaMcp.Adapter.{key}.dll"), Path.Combine(output, coverage + ".json")], true), []),
                 Root, output, new Dictionary<string, string> { ["adapter"] = production }));
            // Fixture coverage and JIT/safety retain their existing jobs: adapter-coverage-vK
            // and harness-vK-native-diagnostics-only-plc (including inventory equality and all SDK guards).
        }
    }
}
