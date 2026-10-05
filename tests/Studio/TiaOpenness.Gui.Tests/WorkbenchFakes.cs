using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.Tests;

internal sealed class FakeDialogService : IDialogService
{
    public DialogResult Result { get; set; } = DialogResult.Cancel;
    public string[]? Files { get; set; }
    public string? Folder { get; set; }
    public List<(string Text, string Caption, DialogButtons Buttons, DialogIcon Icon)> Messages { get; } = [];
    public List<(string Title, string Filter, bool Multiselect)> FileRequests { get; } = [];
    public List<string> FolderRequests { get; } = [];

    public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon)
    {
        Messages.Add((text, caption, buttons, icon));
        return Result;
    }

    public string[]? OpenFiles(string title, string filter, bool multiselect = false)
    {
        FileRequests.Add((title, filter, multiselect));
        return Files;
    }

    public string? OpenFolder(string title)
    {
        FolderRequests.Add(title);
        return Folder;
    }
}

internal sealed class FakeStudioClient : IStudioClient
{
    public sealed record Invocation(string Name, object?[] Arguments);
    public List<Invocation> Calls { get; } = [];
    public bool IsRunning { get; private set; }
    public bool IsMock { get; private set; }
    public bool Disposed { get; private set; }
    public bool VcSupported { get; set; } = true;
    public ProjectInfo? AttachedProject { get; set; }
    public Exception? ImportError { get; set; }
    public Func<string, string[], string, ExportFormat, ExportResult>? ExportHandler { get; set; }
    public List<DeviceInfo> Devices { get; } = [new() { Id = "PLC_1", Name = "PLC_1", Category = "Plc" }];
    public List<BlockInfo> Blocks { get; } = [new() { Name = "Main", Path = "Main", Kind = BlockKind.OB, IsConsistent = true }];
    public List<WorkspaceInfo> Workspaces { get; } = [new() { Name = "git", RootPath = @"D:\workspace" }];
    public List<MappedObjectInfo> StatusItems { get; } = [];
    public event EventHandler<BridgeLogEventArgs>? Log;
    public event EventHandler<ProgressEventArgs>? Progress;
    public event EventHandler? Exited;

    public void EmitLog(string line) => Log?.Invoke(this, new BridgeLogEventArgs { Line = line });
    public void EmitProgress(ProgressPayload progress) => Progress?.Invoke(this, new ProgressEventArgs { Progress = progress });
    public void Exit() { IsRunning = false; Exited?.Invoke(this, EventArgs.Empty); }
    private void Record(string name, params object?[] arguments) => Calls.Add(new(name, arguments));
    private Task<T> Reply<T>(string name, T value, params object?[] arguments)
    {
        Record(name, arguments);
        return Task.FromResult(value);
    }

    public void Start(bool forceMock, string opennessVersion)
    {
        Record("Start", forceMock, opennessVersion);
        IsRunning = true;
        IsMock = forceMock;
    }
    public Task<SessionState> ConnectAsync(bool withUserInterface) => Reply("Connect", new SessionState
        { Connected = true, OpennessVersion = "21", OpenProject = AttachedProject! }, withUserInterface);
    public Task<SessionState> DisconnectAsync() => Reply("Disconnect", new SessionState { Connected = false });
    public Task<ProjectInfo> OpenProjectAsync(string path) => Reply("OpenProject", new ProjectInfo { Name = "Line", Path = path }, path);
    public Task SaveProjectAsync() { Record("Save"); return Task.CompletedTask; }
    public Task<List<DeviceInfo>> ListDevicesAsync() => Reply("Devices", Devices);
    public Task<List<BlockInfo>> ListBlocksAsync(string deviceId) => Reply("Blocks", Blocks, deviceId);
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format)
        => Reply("Export", ExportHandler?.Invoke(deviceId, blocks.ToArray(), outputDirectory, format)
            ?? new ExportResult { OutputDirectory = outputDirectory }, deviceId, blocks.ToArray(), outputDirectory, format);
    public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite)
    {
        Record("Import", deviceId, files.ToArray(), overwrite);
        return ImportError is null ? Task.FromResult(new ExportResult()) : Task.FromException<ExportResult>(ImportError);
    }
    public Task<CompileResult> CompileAsync(string deviceId) => Reply("Compile", new CompileResult { State = "Success" }, deviceId);
    public Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern)
        => Reply("Inspect", new InspectionReport { DeviceId = deviceId }, deviceId, blockNamePattern);
    public Task<bool> VcSupportedAsync() => Reply("VcSupported", VcSupported);
    public Task<List<WorkspaceInfo>> VcListWorkspacesAsync() => Reply("Workspaces", Workspaces);
    public Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath)
    {
        var workspace = new WorkspaceInfo { Name = name, RootPath = folderPath };
        Workspaces.Add(workspace);
        return Reply("VcCreate", workspace, name, folderPath);
    }
    public Task<MappingResult> VcMapProjectAsync(string? workspaceName, string? deviceId, bool dryRun)
        => Reply("VcMap", new MappingResult { DryRun = dryRun }, workspaceName, deviceId, dryRun);
    public Task<WorkspaceStatusReport> VcStatusAsync(string? workspaceName, bool changedOnly)
        => Reply("VcStatus", new WorkspaceStatusReport { Items = StatusItems, Total = StatusItems.Count }, workspaceName, changedOnly);
    public Task<WorkspaceDiff> VcDiffAsync(string? workspaceName, string? file)
        => Reply("VcDiff", new WorkspaceDiff { Available = true, Lines = [new() { Kind = DiffLineKind.Added, Text = "+fixture" }] }, workspaceName, file);
    public Task<SyncResult> VcSyncAsync(string? workspaceName, SyncDirection direction, bool dryRun)
        => Reply("VcSync", new SyncResult { DryRun = dryRun, Direction = direction }, workspaceName, direction, dryRun);
    public void Dispose() => Disposed = true;
}
