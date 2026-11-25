#if VCI_SDK_EXCEPTIONS
extern alias vciSdk;
global using EngineeringException = vciSdk::Siemens.Engineering.EngineeringException;
global using EngineeringTargetInvocationException = vciSdk::Siemens.Engineering.EngineeringTargetInvocationException;
global using EngineeringUserAbortException = vciSdk::Siemens.Engineering.EngineeringUserAbortException;
global using EngineeringObjectDisposedException = vciSdk::Siemens.Engineering.EngineeringObjectDisposedException;
global using NonRecoverableException = vciSdk::Siemens.Engineering.NonRecoverableException;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiArray : List<object?> { }
    internal sealed class HmiObject : Dictionary<string, object?>
    {
        internal HmiObject() { }
        internal HmiObject(Dictionary<string, object?> values) : base(values) { }
    }
    internal static class HmiMeta
    {
        internal static HmiObject Basic(bool success, params (string Key, object? Value)[] fields)
        {
            var meta = new HmiObject(Contracts.LegacyResponseMetadata.Basic(TiaMcpServer.ModelContextProtocol.ResponseClock.Now, success));
            foreach (var field in fields) meta[field.Key] = field.Value;
            return meta;
        }
    }
}

namespace Siemens.Engineering.VersionControl
{
    public enum CompareState { Unknown, Equal, Unequal, WorkspaceFileMissing }
    public enum SynchronizationMode { ProjectToWorkspace, WorkspaceToProject }
    public sealed class IndividualObjectCompareResult
    {
        public CompareState CompareState { get; set; }
    }

    public sealed class IndividualObjectSynchronizationStatus
    {
        internal const string NativeType = "'Siemens.Engineering.VersionControl.IndividualObjectSynchronizationStatus'.";
        internal CompareState State;
        internal CompareState? Refreshed;
        internal bool RefreshesKnownState;
        internal Exception? RefreshError, ReadError;
        internal List<string> Calls { get; } = new();
        public IndividualObjectCompareResult GetStatus()
        {
            Calls.Add("GetStatus");
            if (ReadError != null) throw ReadError;
            return new() { CompareState = State };
        }
        public void UpdateStatus()
        {
            Calls.Add("UpdateStatus");
            if (RefreshError != null) throw RefreshError;
            if (!RefreshesKnownState && State != CompareState.Unknown)
                throw new EngineeringTargetInvocationException("Error when calling method 'UpdateStatus' of type " + NativeType
                    + " Update status is allowed only for status of type unknown.");
            State = Refreshed ?? State;
            Refreshed = null;
        }
        public void Synchronize(SynchronizationMode mode)
        {
            Calls.Add("Synchronize:" + mode);
            if (State == CompareState.Equal)
                throw new EngineeringTargetInvocationException("Error when calling method 'Synchronize' of type " + NativeType
                    + " Synchronize cannot be called on a workspace mapping that has a compare status of equal.");
            if (RefreshError != null)
                throw new EngineeringTargetInvocationException("Error when calling method 'Synchronize' of type " + NativeType + " The file is deleted");
            State = CompareState.Equal;
        }
    }

    public sealed class WorkspaceMapping : IEngineeringServiceProvider
    {
        public Workspace Parent { get; }
        public string RelativeWorkspacePath { get; }
        internal IndividualObjectSynchronizationStatus Status { get; } = new();
        internal WorkspaceMapping(Workspace workspace, string path) { Parent = workspace; RelativeWorkspacePath = path; }
        public T GetService<T>() where T : class => (Status as T) ?? throw new NotSupportedException();
    }
    public sealed class WorkspaceMappingComposition : List<WorkspaceMapping>
    {
        public WorkspaceMapping Find(IEngineeringObject target) => throw new NotSupportedException();
        public WorkspaceMapping Create(string path, IEngineeringObject target) => throw new NotSupportedException();
    }
    public sealed class Workspace
    {
        public string Name => "MCP_RC_WS";
        public DirectoryInfo RootPath { get; }
        public CultureInfo WorkspaceLanguage => CultureInfo.InvariantCulture;
        public WorkspaceMappingComposition Mappings { get; } = new();
        internal Workspace(DirectoryInfo root) => RootPath = root;
    }
    public sealed class WorkspaceComposition : List<Workspace>
    {
        public Workspace Create(string name, DirectoryInfo dir) => throw new NotSupportedException();
    }
    public class WorkspaceGroup
    {
        public WorkspaceComposition Workspaces { get; } = new();
        public WorkspaceUserGroupComposition Groups { get; } = new();
    }
    public sealed class WorkspaceSystemGroup : WorkspaceGroup { }
    public sealed class WorkspaceUserGroup : WorkspaceGroup { }
    public sealed class WorkspaceUserGroupComposition : List<WorkspaceUserGroup> { }
    public sealed class VersionControlInterface
    {
        public WorkspaceSystemGroup WorkspaceGroup { get; } = new();
    }
}

// Signatures referenced by the shared legacy primitives outside the status and sync paths.
namespace Siemens.Engineering
{
#if !VCI_SDK_EXCEPTIONS
    // Keep the same exception hierarchy for the FoundationHost suite on SDK-free machines.
    public class EngineeringException(string message, Exception? inner = null) : Exception(message, inner) { }
    public class EngineeringTargetInvocationException(string message) : EngineeringException(message) { }
    public class EngineeringUserAbortException(string message) : EngineeringTargetInvocationException(message) { }
    public class EngineeringObjectDisposedException(string message) : EngineeringException(message) { }
    public class NonRecoverableException(string message) : Exception(message) { }
#endif
    public interface IEngineeringObject { public object GetAttribute(string name); }
    public interface IEngineeringServiceProvider { public T GetService<T>() where T : class; }
    public sealed class Project : IEngineeringServiceProvider
    {
        internal VersionControlInterface Vci { get; } = new();
        public T GetService<T>() where T : class => (Vci as T) ?? throw new NotSupportedException();
        public DeviceComposition Devices => throw new NotSupportedException();
        public DeviceUserGroupComposition DeviceGroups => throw new NotSupportedException();
        public DeviceSystemGroup UngroupedDevicesGroup => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.HW
{
    public class DeviceComposition { }
    public class DeviceUserGroupComposition { }
    public class DeviceItemComposition { }
    public class DeviceSystemGroup : DeviceGroup { }
    public class DeviceGroup { public DeviceComposition Devices => throw new NotSupportedException(); }
    public class DeviceUserGroup : DeviceGroup { public DeviceUserGroupComposition Groups => throw new NotSupportedException(); }
    public class HardwareObject { public DeviceItemComposition DeviceItems => throw new NotSupportedException(); }
    public class DeviceItem : HardwareObject { public T GetService<T>() => throw new NotSupportedException(); }
    namespace Features { public class SoftwareContainer { public SW.Software Software => throw new NotSupportedException(); } }
}
namespace Siemens.Engineering.SW
{
    public class Software { }
    public class PlcSoftware : Software
    {
        public PlcBlockSystemGroup BlockGroup => throw new NotSupportedException();
        public PlcTagTableSystemGroup TagTableGroup => throw new NotSupportedException();
        public PlcTypeSystemGroup TypeGroup => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public abstract class PlcBlock : IEngineeringObject { public abstract object GetAttribute(string name); }
    public class PlcBlockComposition { }
    public class PlcBlockUserGroupComposition { }
    public class PlcBlockSystemGroup : PlcBlockGroup { }
    public class PlcBlockGroup
    {
        public PlcBlockComposition Blocks => throw new NotSupportedException();
        public PlcBlockUserGroupComposition Groups => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Tags
{
    public abstract class PlcTagTable : IEngineeringObject { public abstract object GetAttribute(string name); }
    public class PlcTagTableComposition { }
    public class PlcTagTableUserGroupComposition { }
    public class PlcTagTableSystemGroup : PlcTagTableGroup { }
    public class PlcTagTableGroup
    {
        public PlcTagTableComposition TagTables => throw new NotSupportedException();
        public PlcTagTableUserGroupComposition Groups => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Types
{
    public abstract class PlcType : IEngineeringObject { public abstract object GetAttribute(string name); }
    public class PlcTypeComposition { }
    public class PlcTypeUserGroupComposition { }
    public class PlcTypeSystemGroup : PlcTypeGroup { }
    public class PlcTypeGroup
    {
        public PlcTypeComposition Types => throw new NotSupportedException();
        public PlcTypeUserGroupComposition Groups => throw new NotSupportedException();
    }
}
