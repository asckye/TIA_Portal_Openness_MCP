using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "studio-native-flow-" + Guid.NewGuid().ToString("N"));
    private static string BridgeExe => Path.Combine(AppContext.BaseDirectory, "bridge", "TiaOpenness.Bridge.exe");

    private BridgeClient Start(bool child)
    {
        Directory.CreateDirectory(root);
        var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(child ? BridgeExe : null, forceMock: true);
        return bridge;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_contract_connects_opens_browses_exports_imports_compiles_saves_and_disconnects(bool child)
    {
        using var bridge = Start(child);
        using var client = new TiaClient(bridge);
        var progress = new List<string>();
        bridge.Progress += (_, e) => progress.Add(e.Progress.Operation);
        var state = await client.ConnectAsync(withUserInterface: false);
        Assert.Equal(SessionMode.Mock, state.Mode);
        Assert.True(state.Connected);
        Assert.False(state.WithUserInterface);
        var project = await client.OpenProjectAsync(Path.Combine(root, "Line.ap21"));
        Assert.Equal("Line", project.Name);
        var devices = await client.ListDevicesAsync();
        var plc = devices.First(d => d.Category == "Plc");
        Assert.Contains(devices, d => d.Category == "Hmi");
        var blocks = await client.ListBlocksAsync(plc.Id);
        Assert.NotEmpty(blocks);
        Assert.NotEmpty(await client.ListTagTablesAsync(plc.Id));
        Assert.NotEmpty(await client.ListTagsAsync(plc.Id));
        var selected = blocks.First(b => !b.IsKnowHowProtected && b.IsConsistent);
        var exported = await client.ExportBlocksAsync(plc.Id, new[] { selected.Path }, Path.Combine(root, "xml"));
        Assert.Equal(1, exported.Succeeded);
        Assert.True(File.Exists(exported.Items[0].FilePath));
        var source = await client.ExportBlocksAsync(plc.Id, new[] { selected.Path }, Path.Combine(root, "source"), ExportFormat.Source);
        Assert.Equal(1, source.Succeeded);
        Assert.True(File.Exists(source.Items[0].FilePath));
        var imported = await client.ImportBlocksAsync(plc.Id, new[] { exported.Items[0].FilePath }, overwrite: true);
        Assert.Equal(1, imported.Succeeded);
        var compiled = await client.CompileAsync(plc.Id);
        Assert.NotNull(compiled.State);
        Assert.True((await client.InspectAsync(plc.Id)).BlocksScanned > 0);
        Assert.Contains("export", progress);
        Assert.Contains("import", progress);
        await client.SaveProjectAsync();
        await client.CloseProjectAsync();
        Assert.Null((await client.StateAsync()).OpenProject);
        Assert.False((await client.DisconnectAsync()).Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_contract_runs_workspace_preview_map_status_sync_and_local_diff(bool child)
    {
        using var bridge = Start(child);
        using var client = new TiaClient(bridge);
        await client.ConnectAsync();
        await client.OpenProjectAsync(Path.Combine(root, "Line.ap21"));
        Assert.True(await client.VcSupportedAsync());
        var folder = Path.Combine(root, "git 轴 工作区");
        Directory.CreateDirectory(folder);
        var workspace = await client.VcCreateWorkspaceAsync("LineGit", folder);
        Assert.Equal(folder, workspace.RootPath);
        Assert.Single(await client.VcListWorkspacesAsync());
        var preview = await client.VcMapProjectAsync("LineGit");
        Assert.True(preview.DryRun);
        Assert.True(preview.Mapped > 0);
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
        var mapped = await client.VcMapProjectAsync("LineGit", dryRun: false);
        Assert.True(mapped.Mapped > 0);
        var status = await client.VcStatusAsync("LineGit", changedOnly: false);
        Assert.True(status.Total > 0);
        Assert.True((await client.VcSyncAsync("LineGit")).DryRun);
        var synced = await client.VcSyncAsync("LineGit", dryRun: false);
        Assert.False(synced.DryRun);
        Assert.Equal(0, synced.Failed);
        var diff = await client.VcDiffAsync("LineGit");
        Assert.False(diff.Available);
        Assert.Contains("Git repository", diff.Detail);
        var initialized = await TiaOpenness.Shared.LocalProcess.Run("git", new[] { "init", "--quiet" }, folder, null, 30);
        Assert.True(initialized.Success, initialized.Stderr);
        var actualDiff = await client.VcDiffAsync("LineGit");
        Assert.True(actualDiff.Available, actualDiff.Detail);
        Assert.Contains(actualDiff.Lines, l => l.Kind == DiffLineKind.Added);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_errors_do_not_break_the_next_normal_request(bool child)
    {
        using var bridge = Start(child);
        using var client = new TiaClient(bridge);
        var error = await Assert.ThrowsAsync<BridgeRpcException>(() => bridge.CallAsync<object>("initialize"));
        Assert.Equal(-32601, error.Code);
        Assert.True((await client.ConnectAsync()).Connected);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StateAsync(cancelled.Token));
        Assert.True((await client.StateAsync()).Connected);
    }

    [Fact]
    public async Task Doctor_runs_through_real_bridge_without_loading_a_native_session()
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(BridgeExe);
        using var client = new TiaClient(bridge);
        var report = await client.DoctorAsync();
        Assert.NotEmpty(report.MachineName);
        Assert.Contains(report.Checks, c => c.Id == "ENV-NETFX");
        Assert.Contains(report.Checks, c => c.Id == "TIA-INSTALL");
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
