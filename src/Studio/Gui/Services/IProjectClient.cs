using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

/// <summary>Presentation records for engineering pages; production operations use the shared MCP session.</summary>
public interface IProjectClient : IDisposable
{
    public bool IsRunning { get; }
    public bool IsMock { get; }
    public event EventHandler<BridgeLogEventArgs> Log;
    public event EventHandler<ProgressEventArgs> Progress;
    public event EventHandler Exited;
    public void Start(bool forceMock, string opennessVersion);
    public Task<SessionState> ConnectAsync(bool withUserInterface);
    public Task<SessionState> DisconnectAsync();
    public Task<ProjectInfo> OpenProjectAsync(string path);
    public Task SaveProjectAsync();
    public Task<List<DeviceInfo>> ListDevicesAsync();
    public Task<List<BlockInfo>> ListBlocksAsync(string deviceId);
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format);
    public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite);
    public Task<CompileResult> CompileAsync(string deviceId);
    public Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern);
}
