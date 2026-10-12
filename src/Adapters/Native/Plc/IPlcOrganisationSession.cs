using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    // Borrowed native handles remain on the existing session owner thread.
    public interface IPlcOrganisationSession
    {
        public Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath);
        public string PlcSoftwarePath(PlcSoftware? software);
        public string ReleaseKey { get; }
        public string SerializeEvidence(Dictionary<string, object?> evidence);
        public void RecordExportFailure(Exception error);
        public void LogEngineeringFailure(Exception error, string message);
        public void RecordExportPath(string? path);
        public PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath);
        public string GetPlcBlockGroupPath(PlcBlockGroup group);
        public bool IsProjectNull();
        public TiaPortal? CurrentPortal { get; }
        public char[] RegexChars { get; }
        public IEqualityComparer<object> ReferenceEqualityComparer { get; }
        public IDisposable AcquireHmiEditAccess();
        public SoftwareContainer? GetSoftwareContainer(string softwarePath);
        public SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath);
        public PlcSoftware? GetPlcSoftware(string path);
        public List<(string Path, bool? Consistent)> ReadPlcConsistency(string path);
        public PlcSoftware? ResolvePlc(string softwarePath, bool write);
        public object? ResolvePlcTagTableGroup(PlcSoftware plc);
        public T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService;
        public PlcSoftware ExactPlcForEngineering(string softwarePath, bool write);
        public object ExactMasterCopyPlcSource(string softwarePath, string objectPath, bool block);
        public Siemens.Engineering.Library.MasterCopies.MasterCopy ExactMasterCopy(string libraryName, string path);
        public void VerifyBinding(string operation);
        public string BindingIdentity();
        public Project? CurrentProject { get; }
        public object ExactObjectUnder(object root, string path, string collection, string label);
        public object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind);
        public PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit);
        public PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit);
        public PlcBlock? GetBlock(string softwarePath, string blockPath);
        public List<PlcBlock>? GetBlocks(string softwarePath, string regex = "");
        public PlcType? GetType(string softwarePath, string typePath);
        public string GetBlockPath(PlcBlock block);
        public void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result);
        public PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath);
        public List<string>? GetPlcTagTables(string softwarePath, out string? reason);
        public string AvailablePlcPathsSuffix();
        public void GroupCreated(string name);
        public bool CompilerLoggingEnabled { get; }
        public void LogCompilerMessage(string path, string state, string description, int errors, int warnings, DateTime time, int nested);
        public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action);
        public List<PlcCrossReference>? GetCrossReferences(string path, string objectPath, string kind, string filter, out string? reason, out bool queried);
        public string? CrossReferenceRefusal(string softwarePath);
        public List<PlcCrossReference> TryFlattenCrossReferenceResult(object result, string fallback);
        public bool RecoverableAuditError(Exception error);
        public object? DocumentMessages(object messages);
    }
}
