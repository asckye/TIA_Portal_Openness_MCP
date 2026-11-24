using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Gui.Services;

public sealed record SoftwareUnitInfo(string Name, string NamespacePreset, IReadOnlyList<string> Blocks)
{
    public IEnumerable<UnitBlockInfo> Children => System.Linq.Enumerable.Select(Blocks, name => new UnitBlockInfo(this, name));
}
public sealed record UnitBlockInfo(SoftwareUnitInfo Unit, string Name);
public enum ProjectLeaseState { Unknown, Available, Held }
public sealed record ProjectProcessObservation(PortalProcessInfo Process, ProjectLeaseState Lease);
public sealed record BlockMember(string Section, string Name, string Datatype, string Comment);

/// <summary>Presentation records for engineering pages; production operations use the shared MCP session.</summary>
public interface IProjectClient : IDisposable
{
    public bool IsRunning { get; }
    public bool IsMock { get; }
    public event EventHandler<BridgeLogEventArgs> Log;
    public event EventHandler<ProgressEventArgs> Progress;
    public event EventHandler Exited;
    public void Start(bool forceMock, string opennessVersion);
    public Task<IReadOnlyList<ProjectProcessObservation>> ListProcessesAsync() => Task.FromResult<IReadOnlyList<ProjectProcessObservation>>([]);
    public Task<SessionState> ConnectAsync(bool withUserInterface, int? processId = null);
    public Task<SessionState> DisconnectAsync();
    public Task<ProjectInfo> OpenProjectAsync(string path);
    public Task SaveProjectAsync();
    public Task<List<DeviceInfo>> ListDevicesAsync();
    public Task<List<BlockInfo>> ListBlocksAsync(string deviceId);
    public Task<IReadOnlyList<SoftwareUnitInfo>> ListUnitsAsync(string plc) => Task.FromResult<IReadOnlyList<SoftwareUnitInfo>>([]);
    public Task<IReadOnlyList<string>?> ListScreensAsync(string hmi) => Task.FromResult<IReadOnlyList<string>?>(null);
    public Task<(int? Tags, int? Alarms)> HmiCountsAsync(DeviceInfo hmi) => Task.FromResult<(int?, int?)>((null, null));
    public Task<IReadOnlyList<BlockMember>> ReadInterfaceAsync(string plc, string block, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<BlockMember>>([]);
    public Task<string> DescribeBlockLogicAsync(string softwarePath, string blockPath, CancellationToken token = default);
    public Task<List<TagInfo>> ListPlcTagsAsync(string plc, string table, bool includeUnits, CancellationToken token = default);
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format);
    public Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite);
    public Task<CompileResult> CompileAsync(string deviceId);
    public Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern);
}
