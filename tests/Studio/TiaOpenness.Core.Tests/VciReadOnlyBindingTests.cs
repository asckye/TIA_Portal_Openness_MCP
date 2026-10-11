using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Mock;
using TiaOpenness.Core.Rpc;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class VciReadOnlyBindingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tia-vci-read-" + Guid.NewGuid().ToString("N"));
    private readonly Process process = Process.GetCurrentProcess();
    private FileStream? lease;
    private string LeasePath => Path.Combine(root, process.Id + "-" + process.StartTime.ToUniversalTime().Ticks + ".lease");
    public VciReadOnlyBindingTests() => Directory.CreateDirectory(root);
    private void HoldLease() => lease = new FileStream(LeasePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    private RpcDispatcher Dispatcher(Reader reader) => new(() => new Factory(reader), _ => { }, (pid, ticks) => VciReadOnlyBinding.Require(pid, ticks, root));
    private RpcResponse Attach(RpcDispatcher dispatcher) => dispatcher.Handle(new() { Id = "read", Method = "vc.attachReadOnly",
        Params = BridgeJson.ToElement(new { processId = process.Id, startUtcTicks = process.StartTime.ToUniversalTime().Ticks, projectFile = @"D:\fixture\Line.ap19" }) });

    [Fact]
    public void Exact_live_identity_requires_an_exclusively_held_lease()
    {
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        Assert.Throws<InvalidOperationException>(() => VciReadOnlyBinding.Require(process.Id, ticks, root));
        File.WriteAllText(LeasePath, "RELEASED\n");
        Assert.Throws<InvalidOperationException>(() => VciReadOnlyBinding.Require(process.Id, ticks, root));
        HoldLease(); VciReadOnlyBinding.Require(process.Id, ticks, root);
        Assert.Throws<InvalidOperationException>(() => VciReadOnlyBinding.Require(process.Id, ticks + 1, root));
        lease!.Dispose(); lease = null;
        Assert.Throws<InvalidOperationException>(() => VciReadOnlyBinding.Require(process.Id, ticks, root));
    }
    [Fact]
    public void Read_attachment_passes_only_the_exact_process_project_and_start_time()
    {
        HoldLease(); var reader = new Reader(); using var dispatcher = Dispatcher(reader);
        Assert.Null(Attach(dispatcher).Error);
        Assert.Equal(process.Id, reader.Pid); Assert.Equal(process.StartTime.ToUniversalTime().Ticks, reader.Ticks);
        Assert.Equal(@"D:\fixture\Line.ap19", reader.Project);
        Assert.Null(dispatcher.Handle(new() { Id = "list", Method = RpcMethods.VcWorkspaceList }).Error);
        lease!.Dispose(); lease = null;
        Assert.NotNull(dispatcher.Handle(new() { Id = "expired", Method = RpcMethods.VcWorkspaceList }).Error);
        Assert.Null(dispatcher.Handle(new() { Id = "detach", Method = RpcMethods.SessionDisconnect }).Error);
    }
    [Theory]
    [InlineData(RpcMethods.SessionConnect)] [InlineData(RpcMethods.ProjectOpen)] [InlineData(RpcMethods.ProjectSave)]
    [InlineData(RpcMethods.ProjectClose)] [InlineData(RpcMethods.BlockExport)] [InlineData(RpcMethods.BlockImport)]
    [InlineData(RpcMethods.CompileDevice)] [InlineData(RpcMethods.VcWorkspaceCreate)] [InlineData(RpcMethods.VcMapProject)] [InlineData(RpcMethods.VcSync)]
    public void Every_write_or_general_session_method_is_refused_by_the_read_bridge(string method)
    {
        HoldLease(); var reader = new Reader(); using var dispatcher = Dispatcher(reader);
        Assert.Null(Attach(dispatcher).Error);
        var refused = dispatcher.Handle(new() { Id = "forbidden", Method = method, Params = BridgeJson.ToElement(new { dryRun = false }) });
        Assert.NotNull(refused.Error); Assert.Contains("VCI reads only", refused.Error!.Message);
        Assert.Equal(0, reader.UnexpectedCalls);
    }
    [Fact]
    public void Missing_lease_fails_before_factory_or_backend_and_does_not_fall_back_to_connect()
    {
        var reader = new Reader(); using var dispatcher = Dispatcher(reader);
        Assert.NotNull(Attach(dispatcher).Error); Assert.Equal(0, reader.Pid);
        Assert.NotNull(dispatcher.Handle(new() { Id = "connect", Method = RpcMethods.SessionConnect }).Error);
        Assert.Equal(0, reader.UnexpectedCalls);
    }
    public void Dispose()
    {
        lease?.Dispose(); process.Dispose();
        foreach (string file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
    private sealed class Factory(Reader reader) : ITiaSessionFactory
    {
        public SessionMode Mode => SessionMode.Mock;
        public void Configure(string version) { }
        public ITiaSession Create() => reader;
    }
    private sealed class Reader : ITiaSession, IVciReadOnlySession
    {
        private readonly MockTiaSession mock = new();
        public int Pid, UnexpectedCalls;
        public long Ticks;
        public string? Project;
        public SessionState AttachVciReadOnly(int pid, long ticks, string file)
        {
            Pid = pid; Ticks = ticks; Project = file;
            mock.Connect(false, true, "19"); mock.OpenProject(file); return mock.GetState();
        }
        public SessionMode Mode => SessionMode.Mock;
        public bool IsConnected => mock.IsConnected;
        public bool HasProject => mock.HasProject;
        public IVersionControl VersionControl => mock.VersionControl;
        public SessionState GetState() => mock.GetState();
        public void Disconnect() => mock.Disconnect();
        public void Dispose() => mock.Dispose();
        private InvalidOperationException Unexpected() { UnexpectedCalls++; return new("Unexpected backend call."); }
        public SessionState Connect(bool ui, bool attach, string version) => throw Unexpected();
        public ProjectInfo OpenProject(string path) => throw Unexpected();
        public ProjectInfo GetProjectInfo() => mock.GetProjectInfo();
        public void SaveProject() => throw Unexpected();
        public void CloseProject() => throw Unexpected();
        public IReadOnlyList<DeviceInfo> ListDevices() => throw Unexpected();
        public IReadOnlyList<BlockInfo> ListBlocks(string id, bool system) => throw Unexpected();
        public ExportResult ExportBlocks(string id, IReadOnlyList<string> paths, string directory, ExportFormat format, bool folders, ProgressCallback progress) => throw Unexpected();
        public ExportResult ImportBlocks(string id, IReadOnlyList<string> paths, bool overwrite, ProgressCallback progress) => throw Unexpected();
        public IReadOnlyList<TagTableInfo> ListTagTables(string id) => throw Unexpected();
        public IReadOnlyList<TagInfo> ListTags(string id, string table) => throw Unexpected();
        public CompileResult CompileDevice(string id, bool software) => throw Unexpected();
        public InspectionReport Inspect(string id, InspectionOptions options) => throw Unexpected();
    }
}
