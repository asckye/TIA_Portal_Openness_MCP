using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Services.Stubs;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class AtlasServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "atlas-service-" + Guid.NewGuid().ToString("N"));
    private sealed class ProgressSink : IProgress<AtlasProgress> { public void Report(AtlasProgress value) { } }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Exports_selected_scope_once_and_returns_a_real_new_html_file(bool single)
    {
        using var client = new FakeStudioClient();
        client.ExportHandler = (device, blocks, directory, format) =>
        {
            Assert.Equal("PLC_1", device);
            Assert.Equal(ExportFormat.SimaticMl, format);
            var result = new ExportResult { Requested = blocks.Length, Succeeded = blocks.Length, OutputDirectory = directory };
            foreach (string name in blocks)
            {
                string file = Path.Combine(directory, name + ".xml");
                File.WriteAllText(file, "<Document><Engineering version=\"V21\"/><SW.Blocks.FC><AttributeList><Name>" + name + "</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
                result.Items.Add(new ExportedItem { FilePath = file, Succeeded = true });
            }
            return result;
        };
        var service = new AtlasService(client, () => root);
        var request = new AtlasRequest("project", "PLC_1", single ? new[] { "Main" } : new[] { "Main", "Timer" }, single);
        var first = await service.GenerateAsync(request, new ProgressSink(), CancellationToken.None);
        var second = await service.GenerateAsync(request, new ProgressSink(), CancellationToken.None);
        Assert.Null(first.Error); Assert.Null(second.Error);
        Assert.NotEqual(first.HtmlPath, second.HtmlPath);
        Assert.True(File.Exists(first.HtmlPath));
        Assert.Contains(single ? "PLC block page" : "PLC program atlas", File.ReadAllText(first.HtmlPath!));
        Assert.Equal(2, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.Equal("Export", call.Name));
    }

    [Fact]
    public async Task Partial_export_and_cancelled_request_never_report_success()
    {
        using var client = new FakeStudioClient();
        var service = new AtlasService(client, () => root);
        var request = new AtlasRequest("project", "PLC_1", new[] { "Main" }, true);
        var result = await service.GenerateAsync(request, new ProgressSink(), CancellationToken.None);
        Assert.Null(result.HtmlPath); Assert.Contains("incomplete", result.Error);
        Assert.Empty(Directory.GetFiles(root, "*.html", SearchOption.AllDirectories));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(request, new ProgressSink(), cancelled.Token));
        Assert.Single(client.Calls);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
        Directory.Delete(root, true);
    }
}
