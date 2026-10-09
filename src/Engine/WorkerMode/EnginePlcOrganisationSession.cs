using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.Worker
{
    internal sealed class EnginePlcOrganisationSession : IPlcOrganisationSession
    {
        private readonly IEngineeringSession session;
        private readonly PlcFoundationEngine? foundation;
        internal EnginePlcOrganisationSession(IEngineeringSession session, PlcFoundationEngine? foundation = null)
        { this.session = session; this.foundation = foundation; }
        public global::Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath) => session.ExactEngineeringHardware(JsonSerializer.Serialize(devicePath), JsonSerializer.Serialize(itemPath));
        public string PlcSoftwarePath(PlcSoftware? software) => SoftwareContainerLookup.PathOf(software);
        public string ReleaseKey => Engineering.TiaMajorVersion.ToString();
        public void RecordExportPath(string? path) => ((Portal)session).RecordPlcSoftwareExportPath(path);
        public PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath) => ((Portal)session).GetPlcTypeGroupByPath(path, groupPath);
        public string GetPlcBlockGroupPath(PlcBlockGroup group) => session.GetPlcBlockGroupPath(group);
        public bool IsProjectNull() => session.IsProjectNull();
        public TiaPortal? CurrentPortal => session.CurrentPortal;
        public Project? CurrentProject => session.CurrentProject as Project;
        public char[] RegexChars => session.RegexChars;
        public IEqualityComparer<object> ReferenceEqualityComparer => session.ReferenceEqualityComparer;
        public IDisposable AcquireHmiEditAccess() => session.AcquireHmiEditAccess();
        public void VerifyBinding(string operation) => session.VerifyBinding(operation);
        public string BindingIdentity() { var value = session.GetBindingIdentity(); return value["identity"] == null ? "" : value.ToJsonString(); }
        public SoftwareContainer? GetSoftwareContainer(string path) => session.GetSoftwareContainer(path);
        public SoftwareContainer? ResolveSoftwareContainerUncached(string path) => session.ResolveSoftwareContainerUncached(path);
        public PlcSoftware? GetPlcSoftware(string path) => session.GetPlcSoftware(path);
        public List<(string Path, bool? Consistent)> ReadPlcConsistency(string path) => session.ReadPlcConsistency(path);
        public PlcSoftware? ResolvePlc(string path, bool write) => session.ResolvePlc(path, write ? PlcAccess.Write : PlcAccess.Read);
        public object? ResolvePlcTagTableGroup(PlcSoftware plc) => session.ResolvePlcTagTableGroup(plc);
        public T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService => session.ResolvePlcService<T>(path, plc);
        public PlcSoftware ExactPlcForEngineering(string path, bool write) => session.ExactPlcForEngineering(path, write);
        public object ExactMasterCopyPlcSource(string path, string objectPath, bool block) => session.ExactMasterCopyPlcSource(path, objectPath, block);
        public global::Siemens.Engineering.Library.MasterCopies.MasterCopy ExactMasterCopy(string libraryName, string path) => session.ExactMasterCopy(libraryName, path);
        public object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind) => session.OptionalUnit(plc, unitName, unitKind);
        public object ExactObjectUnder(object root, string path, string collection, string label) => session.ExactObjectUnder(root, path, collection, label);
        public PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit) => session.BlockRootOf(plc, (global::Siemens.Engineering.SW.Units.PlcUnitBase?)unit);
        public PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit) => session.TypeRootOf(plc, (global::Siemens.Engineering.SW.Units.PlcUnitBase?)unit);
        public PlcBlock? GetBlock(string path, string blockPath) => session.GetBlock(path, blockPath);
        public List<PlcBlock>? GetBlocks(string path, string regex = "") => session.GetBlocks(path, regex);
        public PlcType? GetType(string path, string typePath) => session.GetType(path, typePath);
        public string GetBlockPath(PlcBlock block) => session.GetBlockPath(block);
        public void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result) => session.GetBlocksRecursive(root, result);
        public PlcBlockGroup? GetPlcBlockGroupByPath(string path, string groupPath) => session.GetPlcBlockGroupByPath(path, groupPath);
        public List<string>? GetPlcTagTables(string path, out string? reason) { reason = null; return session.GetPlcTagTables(path, out _); }
        public string AvailablePlcPathsSuffix() => session.AvailablePlcPathsSuffix();
        public bool CompilerLoggingEnabled => session.Logger != null;
        public void LogCompilerMessage(string path, string state, string description, int errors, int warnings, DateTime time, int nested)
            => session.Logger?.LogInformation("Compile {Path}: {State} {Description} ({Errors} errors / {Warnings} warnings, {Time}, {Nested} nested)", path, state, description, errors, warnings, time, nested);
        public void GroupCreated(string name) => session.Logger?.LogInformation($"Created PLC block group '{name}'");
        public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action) => foundation!.EngineeringStep!(tool, action);
        private static List<PlcCrossReference>? References(List<TiaMcpServer.ModelContextProtocol.CrossReferenceEntry>? values)
            => values == null ? null : JsonSerializer.Deserialize<List<PlcCrossReference>>(JsonSerializer.Serialize(values));
        public List<PlcCrossReference>? GetCrossReferences(string path, string objectPath, string kind, string filter, out string? reason, out bool queried)
            => References(session.GetCrossReferences(path, objectPath, kind, filter, out reason, out queried));
        public string? CrossReferenceRefusal(string path) => session.CrossReferenceRefusal(path);
        public List<PlcCrossReference> TryFlattenCrossReferenceResult(object result, string fallback) => References(session.TryFlattenCrossReferenceResult(result, fallback))!;
        public bool RecoverableAuditError(Exception error) => session.RecoverableAuditError(error);
        public object? DocumentMessages(object messages) => Plain(session.DocumentMessages((DocumentResultMessageComposition)messages));
        private static object? Plain(JsonNode? node)
        {
            if (node is JsonObject obj) return obj.ToDictionary(p => p.Key, p => Plain(p.Value));
            if (node is JsonArray array) return array.Select(Plain).ToArray();
            if (node is not JsonValue value) return null;
            if (value.TryGetValue<bool>(out var flag)) return flag;
            if (value.TryGetValue<int>(out var count)) return count;
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToString();
        }
    }
}
