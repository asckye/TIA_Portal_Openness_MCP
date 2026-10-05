using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ProjectPageInteractionTests(WpfContext wpf)
{
    [Fact]
    public void Environment_card_navigates_to_the_existing_shell_page()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                var button = (Button)ProjectPageTestSupport.Find(window, "RunEnvironmentButton");
                Assert.True(button.IsEnabled);
                UnifiedDesktopTests.ClickControl(button);
                Assert.True(((RadioButton)window.FindName("RailEnvironment")).IsChecked);
            }
            finally { window.Close(); }
        });
    }

    private sealed class AtlasProbe : IAtlasService
    {
        public AtlasRequest? Request { get; private set; }
        public IProgress<AtlasProgress>? Progress { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<AtlasResult> Completion { get; } = new();
        public Task<AtlasResult> GenerateAsync(AtlasRequest request, IProgress<AtlasProgress> progress, CancellationToken cancellationToken)
        {
            Request = request;
            Progress = progress;
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"D:\fixture\Line.ap21")]
    public void Connect_button_chooses_the_existing_attach_or_open_command(string path)
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient();
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                model.Session.ProjectPath = path;
                UnifiedDesktopTests.ClickControl((Button)ProjectPageTestSupport.Find(window, "ConnectButton"));
                Assert.True(model.Session.IsConnected);
                Assert.Equal("Start", client.Calls[0].Name);
                Assert.Equal("Connect", client.Calls[1].Name);
                Assert.Equal(path.Length > 0, client.Calls.Any(call => call.Name == "OpenProject"));
                if (path.Length > 0) Assert.Equal(path, client.Calls.Single(call => call.Name == "OpenProject").Arguments[0]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Disconnect_forwards_once_clears_project_presentations_and_preserves_log_history()
    {
        wpf.Run(() =>
        {
            var client = new FakeStudioClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
            var model = new MainViewModel(client, new FakeDialogService());
            var window = new MainWindow(model, false);
            try
            {
                model.Session.Connect.Execute(null);
                model.Engineering.Compile.Execute(null);
                var results = (GlassResults)((FrameworkElement)window.FindName("Root")).Tag;
                Assert.True(results.HasCompile);
                window.Navigate("Blocks");
                string history = model.Activity.Log;
                client.Calls.Clear();
                model.Session.Disconnect.Execute(null);
                Assert.Equal("Disconnect", Assert.Single(client.Calls).Name);
                Assert.False(model.Session.IsConnected);
                Assert.Empty(model.Session.ProjectName);
                Assert.Empty(model.Engineering.Blocks);
                Assert.Null(model.Engineering.SelectedDevice);
                Assert.Empty(model.VersionControl.Workspaces);
                Assert.Null(model.VersionControl.SelectedWorkspace);
                Assert.False(results.HasCompile);
                Assert.Empty(results.Diagnostics);
                Assert.StartsWith(history, model.Activity.Log);
                Assert.False(model.Engineering.Compile.CanExecute(null));
                Assert.False(model.Session.Disconnect.CanExecute(null));
                Assert.True(((RadioButton)window.FindName("RailEngineering")).IsChecked);
                model.Session.Connect.Execute(null);
                Assert.True(model.Session.IsConnected);
                Assert.False(results.HasCompile);
                model.Activity.ClearLog();
                model.Engineering.Compile.Execute(null);
                Assert.True(results.HasCompile);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Atlas_uses_selected_block_or_whole_plc_and_displays_progress_without_native_calls()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            using var atlas = new AtlasPresentation(model, service);
            Assert.False(atlas.Visible);
            var pending = atlas.GenerateAsync(false);
            Assert.True(atlas.Running);
            Assert.False(atlas.CanGenerate);
            Assert.Equal(9, service.Request!.BlockPaths.Count);
            Assert.False(service.Request.SingleBlock);
            service.Progress!.Report(new AtlasProgress(3, 9, "Scale_Analog_FC"));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(100.0 / 3, atlas.Percent, 5);
            Assert.Equal("Scale_Analog_FC", atlas.Detail);
            service.Completion.SetResult(new AtlasResult(null, "fixture failure"));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.True(pending.IsCompletedSuccessfully);
            Assert.True(atlas.Failed);
            Assert.Equal("fixture failure", atlas.Detail);
            Assert.True(atlas.CanGenerate);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_html_opens_through_the_browser_boundary(bool singleBlock)
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            string? opened = null;
            using var atlas = new AtlasPresentation(model, service, path => opened = path);
            string html = Path.Combine(AppContext.BaseDirectory, "atlas-" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                File.WriteAllText(html, "<!doctype html><title>Test atlas</title>");
                _ = atlas.GenerateAsync(singleBlock);
                Assert.Equal(singleBlock, service.Request!.SingleBlock);
                Assert.Equal(singleBlock ? 1 : 9, service.Request.BlockPaths.Count);
                if (singleBlock) Assert.Equal(model.Engineering.Blocks[0].Path, service.Request.BlockPaths[0]);
                service.Completion.SetResult(new AtlasResult(html, null));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(atlas.Done);
                if (!singleBlock) { Assert.Null(opened); atlas.Open(); }
                Assert.Equal(html, opened);
                File.Delete(html);
                atlas.Open();
                Assert.True(atlas.Failed);
            }
            finally { if (File.Exists(html)) File.Delete(html); }
        });
    }

    [Fact]
    public void Disconnect_discards_late_atlas_results()
    {
        wpf.Run(() =>
        {
            using var model = ProjectPageTestSupport.Model();
            var service = new AtlasProbe();
            using var atlas = new AtlasPresentation(model, service);
            _ = atlas.GenerateAsync(false);
            model.Session.Disconnect.Execute(null);
            Assert.True(service.Token.IsCancellationRequested);
            service.Completion.SetResult(new AtlasResult(@"D:\obsolete.html", null));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.False(atlas.Visible);
            Assert.False(atlas.CanGenerate);
            atlas.Dispose();
        });
    }
}
