using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Logic.V4
{
    public sealed class PreviewData
    {
        public Plan Plan { get; }
        public PreviewData(Plan plan)
        {
            V4Validation.Require(plan != null, "A preview requires a plan.");
            Plan = plan!;
        }
    }

    // Hashes are supplied by the domain preview policy, which owns normalization and sensitive inputs.
    public sealed class Plan
    {
        [JsonPropertyOrder(0)] public string Hash { get; }
        [JsonPropertyOrder(1)] public string? ReleaseKey { get; }
        [JsonPropertyOrder(2)] public string Tool { get; }
        [JsonPropertyOrder(3)] public string ArgumentsHash { get; }
        [JsonPropertyOrder(4)] public PlanIdentity Identity { get; }
        [JsonPropertyOrder(5)] public IReadOnlyDictionary<string, string> InputHashes { get; }
        [JsonPropertyOrder(6)] public string? InventoryHash { get; }
        [JsonPropertyOrder(7)] public IReadOnlyList<PlanOperation> Operations { get; }
        [JsonPropertyOrder(8)] public IReadOnlyList<Warning> Warnings { get; }

        public Plan(string hash, string? releaseKey, string tool, string argumentsHash, PlanIdentity identity,
            IReadOnlyDictionary<string, string> inputHashes, string? inventoryHash,
            IReadOnlyList<PlanOperation> operations, IReadOnlyList<Warning> warnings)
        {
            Hash = hash;
            ReleaseKey = releaseKey;
            Tool = tool;
            ArgumentsHash = argumentsHash;
            Identity = identity;
            InputHashes = V4Validation.Hashes(inputHashes);
            InventoryHash = inventoryHash;
            Operations = V4Validation.List(operations);
            Warnings = V4Validation.List(warnings);
            V4Validation.Plan(this);
        }
    }

    public sealed class PlanIdentity
    {
        [JsonPropertyOrder(0)] public int? ProcessId { get; }
        [JsonPropertyOrder(1)] public DateTimeOffset? ProcessStartUtc { get; }
        [JsonPropertyOrder(2)] public string? ProjectFile { get; }
        [JsonPropertyOrder(3)] public long? BindingEpoch { get; }
        [JsonPropertyOrder(4)] public string? WorkspaceRoot { get; }
        [JsonPropertyOrder(5)] public IReadOnlyList<PlanFile> Files { get; }

        public PlanIdentity(int? processId, DateTimeOffset? processStartUtc, string? projectFile,
            long? bindingEpoch, string? workspaceRoot, IReadOnlyList<PlanFile> files)
        {
            ProcessId = processId;
            ProcessStartUtc = processStartUtc?.ToUniversalTime();
            ProjectFile = projectFile;
            BindingEpoch = bindingEpoch;
            WorkspaceRoot = workspaceRoot;
            Files = V4Validation.List(files);
            V4Validation.Identity(this);
        }
    }

    public sealed class PlanFile
    {
        [JsonPropertyOrder(0)] public string Path { get; }
        [JsonPropertyOrder(1)] public bool Exists { get; }
        [JsonPropertyOrder(2)] public long? ByteLength { get; }
        [JsonPropertyOrder(3)] public string? Sha256 { get; }

        public PlanFile(string path, bool exists, long? byteLength, string? sha256)
        {
            V4Validation.Text(path, nameof(path));
            V4Validation.Require(exists ? byteLength >= 0 && sha256 != null : byteLength == null && sha256 == null,
                "File identity must distinguish a missing output from an existing file.");
            if (sha256 != null) V4Validation.Hash(sha256);
            Path = path;
            Exists = exists;
            ByteLength = byteLength;
            Sha256 = sha256;
        }
    }

    public sealed class PlanOperation
    {
        [JsonPropertyOrder(0)] public string Tool { get; }
        [JsonPropertyOrder(1)] public string? Target { get; }
        [JsonPropertyOrder(2)] public JsonElement Arguments { get; }

        public PlanOperation(string tool, string? target, JsonElement arguments)
        {
            V4Validation.Text(tool, nameof(tool));
            V4Validation.Require(arguments.ValueKind == JsonValueKind.Object, "Plan arguments must be an object.");
            V4Validation.Json(arguments);
            Tool = tool;
            Target = target;
            Arguments = arguments.Clone();
        }
    }
}
