using System.IO.Compression;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

// The archive is immutable once packaged. Its pending tier record names the complete plan;
// this external receipt records the checks which subsequently passed on those exact bytes.
internal static class ReleaseCandidate
{
    internal const string Schema = "tia-mcp/release-candidate/1";
    internal static readonly string[] RecordPaths =
    [
        "manifest/release-build.json", "manifest/configurator-build.json", "manifest/multi-version-build.json",
        "manifest/delivery.json", "manifest/package-manifest.json", "manifest/tools-list.json",
        "manifest/tool-usage-coverage.json", "docs/reference/tool-matrix.md", "docs/reference/version-tool-catalog.md"
    ];
    internal static readonly string[] RecordChecks = ["compare-release-records", "candidate-record-bindings", "candidate-record-only-diff"];

    internal static void RequireRecordOnly(IEnumerable<string> paths)
    {
        var other = paths.Except(RecordPaths, StringComparer.Ordinal).ToArray();
        if (other.Length != 0) throw new ReleaseException("Candidate may change only release records: " + string.Join(", ", other));
    }

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new ReleaseException("Unsafe candidate path: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        ReleaseCommands.RequireCheckAllOwnedPath(root, path);
        return path;
    }

    internal static JsonObject FileBinding(string root, string path) => new()
    {
        ["path"] = Path.GetRelativePath(root, path).Replace('\\', '/'),
        ["sha256"] = ReleaseRecords.HashFile(path), ["length"] = new FileInfo(path).Length
    };

    internal static void VerifyBinding(string root, JsonNode? binding)
    {
        if (binding is not JsonObject row) throw new ReleaseException("Candidate file binding missing");
        var path = Resolve(root, ReleaseEvidence.Text(row["path"]));
        if (!File.Exists(path) || new FileInfo(path).Length != (long?)row["length"] ||
            ReleaseRecords.HashFile(path) != ReleaseEvidence.Text(row["sha256"]))
            throw new ReleaseException("Candidate file missing or hash mismatch: " + ReleaseEvidence.Text(row["path"]));
    }

    internal static JsonObject PackageInventory(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var rows = new JsonObject();
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0) throw new ReleaseException("Candidate archive lacks its package root");
            var relative = entry.FullName[(slash + 1)..];
            if (!relative.StartsWith("runtime/", StringComparison.Ordinal) && Path.GetExtension(relative) is not (".exe" or ".dll")) continue;
            using var stream = entry.Open();
            if (rows.ContainsKey(relative)) throw new ReleaseException("Duplicate candidate binary: " + relative);
            rows[relative] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }
        if (rows.Count == 0) throw new ReleaseException("Candidate has no runtime files");
        return rows;
    }

    internal static void RequirePackage(string root, string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.Single(entry => entry.FullName.EndsWith("/manifest/package-manifest.json", StringComparison.Ordinal));
        using var stream = entry.Open();
        var tier = JsonNode.Parse(stream)!.AsObject();
        var plan = ReleaseCheckPolicy.Load(root).Select("full", null);
        if (ReleaseEvidence.Text(tier["tier"]) != "full" || ReleaseEvidence.Bool(tier["buildCacheEnabled"]) || tier["buildCacheEnabled"] is null ||
            ReleaseEvidence.Text(tier["checkStatus"]) is not ("pending" or "passed") ||
            tier["checksSkipped"] is not JsonArray skipped || skipped.Count != 0 ||
            tier["checksSelected"] is not JsonArray selected || !selected.Select(ReleaseEvidence.Text).SequenceEqual(plan.SelectedChecks) ||
            tier["checksRan"] is not JsonArray ran || !ran.Select(ReleaseEvidence.Text).SequenceEqual(ReleaseEvidence.Text(tier["checkStatus"]) == "passed" ? plan.SelectedChecks : []))
            throw new ReleaseException("Candidate requires the complete full cold-build package plan");
    }

    internal static void RequireRuntimeFiles(string root, JsonObject hashes, bool remapUpdater = true)
    {
        foreach (var (relative, digest) in hashes)
        {
            var source = remapUpdater && relative is ("runtime/tools/TiaMcp.Updater.exe" or "runtime/tools/TiaMcp.Updater.exe.config")
                ? "bin-build/updater/" + Path.GetFileName(relative) : relative;
            var path = Resolve(root, source);
            if (!File.Exists(path) || ReleaseRecords.HashFile(path) != ReleaseEvidence.Text(digest))
                throw new ReleaseException("Candidate checks used changed package runtime bytes: " + relative);
        }
    }

    internal static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new ReleaseException("Candidate is not an object: " + path);

    internal static void Verify(string root, string git, JsonObject candidate, bool requireBranch = true)
    {
        if (ReleaseEvidence.Text(candidate["schema"]) != Schema || ReleaseEvidence.Text(candidate["status"]) != "passed")
            throw new ReleaseException("Candidate is incomplete or from an unsupported schema");
        var state = ReleaseEvidence.SourceState(root, git);
        if (state.Status.Length != 0) throw new ReleaseException("Candidate publication requires a clean working tree");
        RequireSource(candidate, state.Commit, state.Tree, ReleaseEvidence.GitText(root, git, "rev-parse", "--abbrev-ref", "HEAD"), requireBranch);
        RequireVersion(ReleaseEvidence.Text(candidate["version"]), ReleaseVersions.ReadVersionProps(root).Label,
            ReleaseEvidence.Text(Read(Path.Combine(root, "manifest/delivery.json"))["release"]));
        var prep = ReleaseEvidence.Text(candidate["prepCommit"]);
        RequireCommitId(prep);
        if (ReleaseEvidence.GitText(root, git, "rev-parse", prep + "^{tree}") != ReleaseEvidence.Text(candidate["prepTree"]))
            throw new ReleaseException("Candidate prep tree differs from its commit");
        var changes = ReleaseEvidence.GitText(root, git, "diff", "--name-only", prep, state.Commit).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        RequireRecordOnly(changes);
        var inventory = ReleaseEvidence.Inventory(root, git);
        if (ReleaseEvidence.Digest(inventory) != ReleaseEvidence.Text(candidate["inputsSha256"]))
            throw new ReleaseException("Candidate checkout bytes differ from the verified tree");
        VerifyFiles(root, candidate);
    }

    internal static void VerifyFiles(string root, JsonObject candidate)
    {
        var prep = ReleaseEvidence.Text(candidate["prepCommit"]);
        foreach (var field in new[] { "package", "sidecar", "packageResult", "evidence", "adoption" }) VerifyBinding(root, candidate[field]);
        foreach (var field in new[] { "records", "receipts", "toolFiles", "evidenceFiles" })
        {
            if (candidate[field] is not JsonArray rows || rows.Count == 0) throw new ReleaseException("Candidate lacks " + field);
            foreach (var row in rows) VerifyBinding(root, row);
        }
        if (candidate["binaryHashes"] is not JsonObject hashes || hashes.Count == 0) throw new ReleaseException("Candidate binary hashes missing");
        if (ReleaseEvidence.Digest(hashes.Select(pair => KeyValuePair.Create(pair.Key, ReleaseEvidence.Text(pair.Value)))) != ReleaseEvidence.Text(candidate["binaryHashesSha256"]))
            throw new ReleaseException("Candidate binary hash inventory changed");
        var recorded = candidate["records"]!.AsArray().Select(row => ReleaseEvidence.Text(row?["path"])).ToArray();
        if (!recorded.Order(StringComparer.Ordinal).SequenceEqual(RecordPaths.Where(path => File.Exists(Path.Combine(root, path))).Order(StringComparer.Ordinal)))
            throw new ReleaseException("Candidate record inventory is incomplete");
        var checks = candidate["checks"] as JsonArray ?? throw new ReleaseException("Candidate check receipts missing");
        var expected = ReleaseCheckPolicy.Load(root).Select("full", null).SelectedChecks;
        if (!checks.Select(ReleaseEvidence.Text).SequenceEqual(expected)) throw new ReleaseException("Candidate omits full release checks");
        var receiptNames = candidate["receipts"]!.AsArray().Select(row => ReleaseEvidence.Text(row?["name"])).ToHashSet(StringComparer.Ordinal);
        foreach (var name in RequiredReceipts(root))
            if (!receiptNames.Contains(name)) throw new ReleaseException("Candidate lacks passed check receipt: " + name);
        foreach (var row in candidate["receipts"]!.AsArray())
        {
            if (ReleaseEvidence.Text(row?["status"]) != "passed") throw new ReleaseException("Candidate contains a failed check receipt");
            if (ReleaseEvidence.Text(row?["name"]).StartsWith("log:", StringComparison.Ordinal)) continue;
            var receipt = Read(Resolve(root, ReleaseEvidence.Text(row?["path"])));
            if (ReleaseEvidence.Text(receipt["status"]) != "passed" || ReleaseEvidence.Text(receipt["name"]) != ReleaseEvidence.Text(row?["name"]))
                throw new ReleaseException("Candidate check receipt is failed or mislabelled");
            if (receipt["log"] is { } log) VerifyBinding(root, log);
        }
        var zip = Resolve(root, ReleaseEvidence.Text(candidate["package"]?["path"]));
        var sidecar = File.ReadAllText(Resolve(root, ReleaseEvidence.Text(candidate["sidecar"]?["path"]))).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (sidecar.Length != 2 || sidecar[0] != ReleaseEvidence.Text(candidate["package"]?["sha256"]) || sidecar[1] != Path.GetFileName(zip))
            throw new ReleaseException("Candidate sidecar does not bind its ZIP");
        var result = Read(Resolve(root, ReleaseEvidence.Text(candidate["packageResult"]?["path"])));
        if (Path.GetFullPath(ReleaseEvidence.Text(result["path"])) != zip || ReleaseEvidence.Text(result["sha256"]) != ReleaseEvidence.Text(candidate["package"]?["sha256"]))
            throw new ReleaseException("Candidate package-result does not bind its ZIP");
        var adoption = Read(Resolve(root, ReleaseEvidence.Text(candidate["adoption"]?["path"])));
        if (ReleaseEvidence.Text(adoption["schema"]) != ReleaseEvidence.AdoptionSchema ||
            ReleaseEvidence.Text(adoption["record"]?["evidence"]?["commit"]) != prep ||
            ReleaseEvidence.Text(adoption["record"]?["evidence"]?["tree"]) != ReleaseEvidence.Text(candidate["prepTree"]) ||
            ReleaseEvidence.Text(adoption["record"]?["evidence"]?["sha256"]) != ReleaseEvidence.Text(candidate["evidence"]?["sha256"]))
            throw new ReleaseException("Candidate evidence adoption does not bind the prep commit");
    }

    internal static void RequireSource(JsonObject candidate, string commit, string tree, string branch, bool requireBranch)
    {
        if (ReleaseEvidence.Text(candidate["commit"]) != commit || ReleaseEvidence.Text(candidate["tree"]) != tree ||
            ReleaseEvidence.Text(candidate["branch"]) != branch || requireBranch && branch != "master")
            throw new ReleaseException("Candidate branch/tree/commit differs from the publication checkout");
    }

    internal static void RequireCommitId(string commit)
    {
        if (commit.Length is not (40 or 64) || commit.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ReleaseException("Candidate prepCommit must be a full Git object ID");
    }

    internal static void RequireVersion(string candidate, string properties, string delivery)
    {
        if (candidate != properties || candidate != delivery) throw new ReleaseException("Candidate version differs from its committed version/records");
    }

    internal static string[] RequiredReceipts(string root) =>
    [
        "early-gates", "adopt-evidence", "multi-version-preparation", "build-release", "multi-version-completion", "candidate-package", "preflight",
        "strict-delivery-validation", "candidate-checks", "candidate-runtime-bindings", "verify-package", .. RecordChecks,
        "05-prompt-registration", "plc-contracts", "plc-responses", "09-packaged-responses-verify-plc", "10-relocated-bundle",
        "11-product-smoke", "12-foundation-responses", .. ReleaseCheckPolicy.Load(root).SelfTests.Select(test => test.Check)
    ];
}
