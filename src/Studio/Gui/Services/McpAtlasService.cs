using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal sealed class McpAtlasService(McpProjectClient client) : IAtlasService
{
    public async Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
    {
        if (request.BlockPaths.Count == 0) return new(null, "Select a program block before rendering.");
        string directory = Path.Combine(DataLocations.Current.Root, "reports", "plc-atlas-" + Guid.NewGuid().ToString("N"));
        string exports = Path.Combine(directory, "exports");
        // Export preview/apply and approval precede the host's offline rendering tool.
        var exported = await client.ExportAsync(request.DeviceId, request.BlockPaths, exports, ExportFormat.SimaticMl, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (exported.Items.Any(item => !item.Succeeded || !File.Exists(item.FilePath))) return new(null, "SimaticML export is incomplete.");
        string prefix = Path.GetFullPath(exports) + Path.DirectorySeparatorChar;
        if (exported.Items.Any(item => !Path.GetFullPath(item.FilePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return new(null, "Export returned a file outside the requested directory.");
        progress.Report(new(exported.Succeeded, exported.Requested, "Rendering"));
        string output = Path.Combine(directory, request.SingleBlock ? "block.html" : "atlas.html");
        await client.Read(request.SingleBlock ? "RenderPlcBlock" : "RenderPlcProgramAtlas", new() {
            ["inputPath"] = request.SingleBlock ? exported.Items[0].FilePath : exports, ["outputPath"] = output }, cancellationToken);
        return File.Exists(output) ? new(output, null) : new(null, "The host did not produce an HTML report.");
    }
}
