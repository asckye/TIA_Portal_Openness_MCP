using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Gui.Services.Stubs;

public sealed record AtlasRequest(string ProjectPath, string DeviceId, IReadOnlyList<string> BlockPaths, bool SingleBlock);
public sealed record AtlasProgress(int Completed, int Total, string CurrentBlock);
public sealed record AtlasResult(string? HtmlPath, string? Error);

/// <summary>Offline HTML generation boundary; stub until P6-48.</summary>
public interface IAtlasService
{
    Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Product stub until P6-48; never returns a fabricated atlas.</summary>
public sealed class AtlasServiceStub : IAtlasService
{
    public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        => Task.FromResult(new AtlasResult(null, "atlas generation is not connected yet (P6-48)"));
}
