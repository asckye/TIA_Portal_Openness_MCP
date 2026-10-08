extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Shared;
using PlcProgramRenderer = PlcRendering::TiaMcpServer.ModelContextProtocol.PlcProgramRenderer;

namespace TiaOpenness.Gui.Services.Stubs;

/// <summary>Uses the connected client's existing read-only export route, then renders local files.</summary>
public sealed class AtlasService : IAtlasService
{
    private readonly IStudioClient _client;
    private readonly Func<string> _reportDirectory;

    public AtlasService(IStudioClient client, Func<string>? reportDirectory = null)
    {
        _client = client;
        _reportDirectory = reportDirectory ?? (() => Path.Combine(DataLocations.Current.Root, "reports"));
    }

    public async Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.BlockPaths.Count == 0 || (request.SingleBlock && request.BlockPaths.Count != 1))
            return new AtlasResult(null, "Select a program block before rendering.");
        string directory = Path.Combine(_reportDirectory(), "plc-atlas-" + Guid.NewGuid().ToString("N"));
        string exports = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exports);
        progress.Report(new AtlasProgress(0, request.BlockPaths.Count, "SimaticML export"));
        // Preserve the existing client's export method, arguments and bridge-thread ownership.
        var exported = await _client.ExportBlocksAsync(request.DeviceId, request.BlockPaths, exports, ExportFormat.SimaticMl);
        cancellationToken.ThrowIfCancellationRequested();
        if (exported.Requested != request.BlockPaths.Count || exported.Failed != 0 || exported.Succeeded != request.BlockPaths.Count
            || exported.Items.Count != request.BlockPaths.Count || exported.Items.Any(item => !item.Succeeded || !File.Exists(item.FilePath)))
            return new AtlasResult(null, "SimaticML export is incomplete. No atlas was generated. Export files: " + exports);
        string prefix = Path.GetFullPath(exports) + Path.DirectorySeparatorChar;
        if (exported.Items.Any(item => !Path.GetFullPath(item.FilePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return new AtlasResult(null, "Export returned a file outside the requested directory.");
        progress.Report(new AtlasProgress(exported.Succeeded, exported.Requested, "Rendering"));
        string input = request.SingleBlock ? exported.Items[0].FilePath : exports;
        string output = Path.Combine(directory, request.SingleBlock ? "block.html" : "atlas.html");
        var rendered = await Task.Run(() => PlcProgramRenderer.Write(input, output, !request.SingleBlock, null,
            cancellationToken: cancellationToken), cancellationToken);
        return rendered.Ok ? new AtlasResult(output, null) : new AtlasResult(null, rendered.Error?.Message ?? "Rendering failed.");
    }
}
