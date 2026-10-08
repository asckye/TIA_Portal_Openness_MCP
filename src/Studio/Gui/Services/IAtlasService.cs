using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Gui.Services.Stubs;

public sealed record AtlasRequest(string ProjectPath, string DeviceId, IReadOnlyList<string> BlockPaths, bool SingleBlock);
public sealed record AtlasProgress(int Completed, int Total, string CurrentBlock);
public sealed record AtlasResult(string? HtmlPath, string? Error);

/// <summary>Export acquisition and offline HTML generation boundary.</summary>
public interface IAtlasService
{
    Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken);
}
