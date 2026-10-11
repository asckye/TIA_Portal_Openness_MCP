using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

/// <summary>Presentation records for engineering pages; production operations use the shared MCP session.</summary>
public interface IProjectClient : IDisposable
{
    bool IsRunning { get; }
    bool IsMock { get; }
    event EventHandler<BridgeLogEventArgs> Log;
    event EventHandler<ProgressEventArgs> Progress;
    event EventHandler Exited;
    void Start(bool forceMock, string opennessVersion);
    Task<SessionState> ConnectAsync(bool withUserInterface);
    Task<SessionState> DisconnectAsync();
    Task<ProjectInfo> OpenProjectAsync(string path);
    Task SaveProjectAsync();
    Task<List<DeviceInfo>> ListDevicesAsync();
    Task<List<BlockInfo>> ListBlocksAsync(string deviceId);
    Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format);
    Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite);
    Task<CompileResult> CompileAsync(string deviceId);
    Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern);
}
