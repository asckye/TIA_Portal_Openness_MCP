using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchControlTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchControlArtifactTests(WpfContext wpf)
{
    [Theory]
    [InlineData("valid")] [InlineData("hash")] [InlineData("missing")] [InlineData("extension")]
    [InlineData("ads")] [InlineData("unc")] [InlineData("relative")]
    public async Task Artifact_is_checked_before_UI_and_opened_only_on_success(string condition)
    {
        var (window, _, client) = wpf.Run(() => Window()); int opened = 0;
        using var server = new WorkbenchControlServer(window.ControlSurface, window.Dispatcher, _ => Environment.ProcessPath!,
            open: _ => Interlocked.Increment(ref opened));
        string path = Scratch("render") + (condition == "extension" ? ".txt" : ".html");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "<html>fixture</html>");
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var artifact = new WorkbenchRenderArtifact { RequestId = CallId, Path = condition switch
                { "unc" => @"\\server\share\render.html", "relative" => "render.html", "ads" => path + ":stream", _ => path },
                Sha256 = condition == "hash" ? new string('0', 64) : hash, Kind = WorkbenchRenderKind.Atlas };
            if (condition == "missing") File.Delete(path);
            var request = Request(WorkbenchControlOperation.DisplayAtlas,
                new WorkbenchDisplayAtlasArguments { RenderRequestId = CallId, Artifact = artifact });
            WorkbenchControlProtocol.Validate(request);
            var response = await server.Execute(request);
            Assert.Equal(condition == "valid" ? WorkbenchControlStatus.Done : WorkbenchControlStatus.Refused, response.Status);
            Assert.Equal(condition == "valid" ? 1 : 0, opened); Assert.Empty(client.Calls);
            if (condition == "valid") Assert.True(response.Data?.Opened);
        }
        finally { wpf.Run(window.Close); }
    }

    [Fact]
    public async Task Validated_artifact_cannot_be_replaced_until_open_finishes()
    {
        string path = Scratch("lease") + ".html"; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html>fixture</html>");
        var metadata = new WorkbenchRenderArtifact { Path = path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
        using (await ControlArtifact.Validate(metadata, CancellationToken.None))
            Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
        File.WriteAllText(path, "replacement");
    }

    [Fact]
    public void Logs_correlate_and_digest_fields_without_writing_audit_events()
    {
        string directory = Scratch("logs"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "workbench-control-fixture.jsonl");
        var request = Request(WorkbenchControlOperation.PrefillForm,
            new WorkbenchPrefillArguments { Form = WorkbenchPrefillForm.BlockFilter,
                Fields = new WorkbenchBlockFilterFields { Filter = "fixture-private-key" } });
        request.Origin.ClientName = "fixture-private-key";
        new WorkbenchControlLog(path).Record(request, ControlResponse.Done(request, new() { Page = WorkbenchPage.Blocks }), 5);
        string text = File.ReadAllText(path);
        Assert.Contains(request.RequestId, text); Assert.DoesNotContain("fixture-private-key", text);
        Assert.Equal("prefill.form", WorkbenchControlLog.ReadActivity(request.RequestId, directory)?.Operation);
        Assert.Equal("done", WorkbenchControlLog.ReadActivity(request.RequestId, directory)?.Status);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Every_whitelisted_page_navigates_without_native_work(int page)
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                var response = window.ApplyControl(Request(arguments: new WorkbenchDisplayPageArguments { Page = (WorkbenchPage)page }));
                Assert.Equal((WorkbenchPage)page, response.Data?.Page); Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Missing_render_metadata_refuses_before_any_navigation()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                Assert.Equal(WorkbenchControlError.NotFound, window.ApplyControl(Request(WorkbenchControlOperation.DisplayLadder,
                    new WorkbenchDisplayLadderArguments { SoftwarePath = "PLC_1", BlockPath = "Main", RenderRequestId = CallId })).Refusal?.Code);
                Assert.Equal(WorkbenchPage.Overview, window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Page);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void An_empty_loaded_tree_returns_not_found_without_loading_again()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = Window();
            try
            {
                model.Engineering.Blocks.Clear();
                Assert.Equal(WorkbenchControlError.NotFound, window.ApplyControl(Request(WorkbenchControlOperation.DisplayBlock)).Refusal?.Code);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }
}
