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
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;

namespace TiaOpenness.Shared
{
    internal sealed class DataLocations
    {
        internal static DataLocations Current { get; } = new();
        internal string TempDirectory => Path.GetTempPath();
    }
}

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
        internal CompareState? StateAfterRefusedSync;
        internal Exception? RefreshErrorAfterRefusedSync;
        internal bool RefreshesKnownState;
        internal bool AcceptEqual;
        internal Action? OnSynchronized;
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
            if (State == CompareState.Equal && !AcceptEqual)
                throw new EngineeringTargetInvocationException("Error when calling method 'Synchronize' of type " + NativeType
                    + " Synchronize cannot be called on a workspace mapping that has a compare status of equal.");
            if (RefreshError != null)
            {
                State = StateAfterRefusedSync ?? State;
                RefreshError = RefreshErrorAfterRefusedSync ?? RefreshError;
                throw new EngineeringTargetInvocationException("Error when calling method 'Synchronize' of type " + NativeType + " The file is deleted");
            }
            OnSynchronized?.Invoke();
            State = CompareState.Equal;
        }
    }

    public sealed class WorkspaceMapping : IEngineeringServiceProvider
    {
        public Workspace Parent { get; }
        public IEngineeringObject LinkedProjectObject { get; }
        public string RelativeWorkspacePath { get; }
        internal IndividualObjectSynchronizationStatus Status { get; } = new();
        internal WorkspaceMapping(Workspace workspace, string path, IEngineeringObject? target = null)
        { Parent = workspace; RelativeWorkspacePath = path; LinkedProjectObject = target!; }
        public T GetService<T>() where T : class => (Status as T) ?? throw new NotSupportedException();
    }
    public sealed class WorkspaceMappingComposition : List<WorkspaceMapping>
    {
        private readonly Workspace owner;
        internal CompareState InitialState = CompareState.Equal;
        internal bool RefuseInitialStatus, RefuseCreate, RefuseSync, RefuseLookupAfterCreate, RefreshesKnownState;
        internal Action<FileInfo>? WriteOnSync;
        internal Action<FileInfo>? AfterCreate;
        internal Action? BeforeLookup;
        internal WorkspaceMappingComposition(Workspace owner) => this.owner = owner;
        public WorkspaceMapping? Find(IEngineeringObject target)
        {
            BeforeLookup?.Invoke();
            if (RefuseLookupAfterCreate && owner.Calls.Any(call => call.StartsWith("Create:", StringComparison.Ordinal)))
                throw new EngineeringTargetInvocationException("Mapping lookup unavailable");
            return this.FirstOrDefault(m => m.LinkedProjectObject == target);
        }
        public WorkspaceMapping Create(string path, IEngineeringObject target)
        {
            owner.Calls.Add("Create:" + path);
            if (RefuseCreate) throw new EngineeringTargetInvocationException("Linked object is not supported");
            var mapping = new WorkspaceMapping(owner, path, target);
            mapping.Status.State = InitialState;
            if (RefuseInitialStatus) mapping.Status.ReadError = new EngineeringTargetInvocationException("Cannot read new status");
            mapping.Status.RefreshesKnownState = RefreshesKnownState;
            var file = new FileInfo(Path.Combine(owner.RootPath.FullName, path));
            mapping.Status.OnSynchronized = () => WriteOnSync?.Invoke(file);
            mapping.Status.RefreshError = RefuseSync || WriteOnSync == null && !file.Exists
                ? new EngineeringTargetInvocationException("The file is deleted") : null;
            Add(mapping);
            AfterCreate?.Invoke(file);
            return mapping;
        }
    }
#if STUDIO_VCI_MODERN
    public sealed class MappedObject
    {
        internal Exportable Target = null!;
        internal IndividualObjectSynchronizationStatus Status { get; } = new();
        public string FileNameWithoutExtension { get; set; } = "";
        public DirectoryInfo DirectoryPath { get; set; } = null!;
        public string FileFormat => "xml";
        public IndividualObjectCompareResult GetStatus() => Status.GetStatus();
        public void Synchronize(SynchronizationMode direction) => Status.Synchronize(direction);
    }
    public sealed class MappedObjectComposition : List<MappedObject>
    {
        internal Action? BeforeLookup;
        public MappedObject? Find(IEngineeringObject target)
        {
            BeforeLookup?.Invoke();
            return this.FirstOrDefault(m => m.Target == target);
        }
    }
#endif
    public sealed class Workspace
    {
#if STUDIO_VCI_MODERN
        public MappedObjectComposition MappedObjects { get; } = new();
        internal string ExportFormat = "xml";
        internal Action<IEngineeringObject>? QueryFormats;
        public IEnumerable<string> GetSupportedFileFormats(IEngineeringObject target)
        {
            QueryFormats?.Invoke(target);
            return target is Exportable ? new[] { ExportFormat } : Array.Empty<string>();
        }
        public MappedObject ExportObject(IEngineeringObject target, DirectoryInfo directory, string name, string format)
        {
            Calls.Add("Workspace.ExportObject:" + name + ":" + format);
            return AddMapping(target, name);
        }
        public MappedObject ConnectObject(IEngineeringObject target, DirectoryInfo directory, string name, string format)
        {
            Calls.Add("Workspace.ConnectObject:" + name + ":" + format);
            return AddMapping(target, name);
        }
        private MappedObject AddMapping(IEngineeringObject target, string name)
        {
            var mapping = new MappedObject { Target = (Exportable)target, DirectoryPath = RootPath, FileNameWithoutExtension = name };
            MappedObjects.Add(mapping);
            mapping.Status.State = CompareState.Equal;
            return mapping;
        }
#endif
        public string Name => "MCP_RC_WS";
        public DirectoryInfo RootPath { get; set; }
        public CultureInfo WorkspaceLanguage => CultureInfo.InvariantCulture;
        internal List<string> Calls { get; } = new();
        public WorkspaceMappingComposition Mappings { get; }
        internal Workspace(DirectoryInfo root) { RootPath = root; Mappings = new(this); }
    }
    public sealed class WorkspaceComposition : List<Workspace>
    {
        public Workspace Create(string name) => throw new NotSupportedException();
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
    public enum ExportOptions { None, WithDefaults }
    public class NamedObject : IEngineeringObject
    {
        public string Name { get; set; } = "";
        public object GetAttribute(string name) => Name;
    }
    public sealed class Project : ProjectBase { }
    public abstract class Exportable : NamedObject
    {
        internal Action<FileInfo, ExportOptions> Write = (_, _) => throw new NotSupportedException();
        public void Export(FileInfo file, ExportOptions options) => Write(file, options);
    }
    public class ProjectBase : NamedObject, IEngineeringServiceProvider
    {
        internal VersionControlInterface Vci { get; } = new();
        public T GetService<T>() where T : class => (Vci as T) ?? throw new NotSupportedException();
        public DeviceComposition Devices { get; } = new();
        public DeviceUserGroupComposition DeviceGroups { get; } = new();
        public DeviceSystemGroup UngroupedDevicesGroup { get; } = new();
    }
}
namespace Siemens.Engineering.HW
{
    public class DeviceComposition : List<Device> { }
    public class DeviceUserGroupComposition : List<DeviceUserGroup> { }
    public class DeviceItemComposition : List<DeviceItem> { }
    public class DeviceSystemGroup : DeviceGroup { }
    public class DeviceGroup : NamedObject { public DeviceComposition Devices { get; } = new(); }
    public class DeviceUserGroup : DeviceGroup { public DeviceUserGroupComposition Groups { get; } = new(); }
    public class HardwareObject : NamedObject { public DeviceItemComposition DeviceItems { get; } = new(); }
    public class Device : HardwareObject { }
    public class DeviceItem : HardwareObject
    {
        public Features.SoftwareContainer Container { get; } = new();
        public T GetService<T>() where T : class => (Container as T)!;
    }
    namespace Features { public class SoftwareContainer { public SW.Software Software { get; set; } = new SW.PlcSoftware(); } }
}
namespace Siemens.Engineering.SW
{
    public class Software : NamedObject { }
    public class PlcSoftware : Software
    {
        public PlcBlockSystemGroup BlockGroup { get; } = new();
        public PlcTagTableSystemGroup TagTableGroup { get; } = new();
        public PlcTypeSystemGroup TypeGroup { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public class PlcBlock : Exportable { public bool IsConsistent { get; set; } = true; }
    public class PlcBlockComposition : List<PlcBlock> { }
    public class PlcBlockUserGroup : PlcBlockGroup { }
    public class PlcBlockUserGroupComposition : List<PlcBlockUserGroup> { }
    public class PlcBlockSystemGroup : PlcBlockGroup { }
    public class PlcBlockGroup : NamedObject
    {
        public PlcBlockComposition Blocks { get; } = new();
        public PlcBlockUserGroupComposition Groups { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Tags
{
    public class PlcTagTable : Exportable { }
    public class PlcTagTableComposition : List<PlcTagTable> { }
    public class PlcTagTableUserGroup : PlcTagTableGroup { }
    public class PlcTagTableUserGroupComposition : List<PlcTagTableUserGroup> { }
    public class PlcTagTableSystemGroup : PlcTagTableGroup { }
    public class PlcTagTableGroup : NamedObject
    {
        public PlcTagTableComposition TagTables { get; } = new();
        public PlcTagTableUserGroupComposition Groups { get; } = new();
    }
}
namespace Siemens.Engineering.SW.Types
{
    public class PlcType : Exportable { }
    public class PlcTypeComposition : List<PlcType> { }
    public class PlcTypeUserGroup : PlcTypeGroup { }
    public class PlcTypeUserGroupComposition : List<PlcTypeUserGroup> { }
    public class PlcTypeSystemGroup : PlcTypeGroup { }
    public class PlcTypeGroup : NamedObject
    {
        public PlcTypeComposition Types { get; } = new();
        public PlcTypeUserGroupComposition Groups { get; } = new();
    }
}

namespace TiaOpenness.Openness
{
#if STUDIO_VCI_MODERN
    // The connect fixture never dispatches a workspace-to-project restore.
    internal sealed partial class OpennessVersionControl
    {
        private static TiaMcp.Adapters.Contracts.Studio.SyncItem? RestoreSdMapping(MappedObject mapped, string root, string label,
            bool dryRun, out bool connectionLost) => throw new NotSupportedException();
        private static bool IsDisposedMapping(MappedObject mapped, Exception error) => throw new NotSupportedException();
        private static CompareState ReadMappedState(MappedObject mapped, out string? error)
        { error = null; return mapped.GetStatus().CompareState; }
    }
#endif
    internal static class PlcNavigator
    {
        internal static IEnumerable<Device> AllDevices(Siemens.Engineering.Project project) => project.Devices;
        internal static Siemens.Engineering.SW.PlcSoftware FindPlcSoftware(Device device) =>
            (Siemens.Engineering.SW.PlcSoftware)device.DeviceItems.Single().Container.Software;
    }
}
