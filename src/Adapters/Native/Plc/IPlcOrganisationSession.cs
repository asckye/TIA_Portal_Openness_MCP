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
        Siemens.Engineering.HW.HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath);
        string PlcSoftwarePath(PlcSoftware? software);
        string ReleaseKey { get; }
        void RecordExportPath(string? path);
        PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath);
        string GetPlcBlockGroupPath(PlcBlockGroup group);
        bool IsProjectNull();
        TiaPortal? CurrentPortal { get; }
        char[] RegexChars { get; }
        IEqualityComparer<object> ReferenceEqualityComparer { get; }
        IDisposable AcquireHmiEditAccess();
        SoftwareContainer? GetSoftwareContainer(string softwarePath);
        SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath);
        PlcSoftware? GetPlcSoftware(string path);
        List<(string Path, bool? Consistent)> ReadPlcConsistency(string path);
        PlcSoftware? ResolvePlc(string softwarePath, bool write);
        object? ResolvePlcTagTableGroup(PlcSoftware plc);
        T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class, IEngineeringService;
        PlcSoftware ExactPlcForEngineering(string softwarePath, bool write);
        object ExactMasterCopyPlcSource(string softwarePath, string objectPath, bool block);
        Siemens.Engineering.Library.MasterCopies.MasterCopy ExactMasterCopy(string libraryName, string path);
        void VerifyBinding(string operation);
        string BindingIdentity();
        Project? CurrentProject { get; }
        object ExactObjectUnder(object root, string path, string collection, string label);
        object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind);
        PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit);
        PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit);
        PlcBlock? GetBlock(string softwarePath, string blockPath);
        List<PlcBlock>? GetBlocks(string softwarePath, string regex = "");
        PlcType? GetType(string softwarePath, string typePath);
        string GetBlockPath(PlcBlock block);
        void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result);
        PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath);
        List<string>? GetPlcTagTables(string softwarePath, out string? reason);
        string AvailablePlcPathsSuffix();
        void GroupCreated(string name);
        bool CompilerLoggingEnabled { get; }
        void LogCompilerMessage(string path, string state, string description, int errors, int warnings, DateTime time, int nested);
        HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action);
        List<PlcCrossReference>? GetCrossReferences(string path, string objectPath, string kind, string filter, out string? reason, out bool queried);
        string? CrossReferenceRefusal(string softwarePath);
        List<PlcCrossReference> TryFlattenCrossReferenceResult(object result, string fallback);
        bool RecoverableAuditError(Exception error);
        object? DocumentMessages(object messages);
    }
}
