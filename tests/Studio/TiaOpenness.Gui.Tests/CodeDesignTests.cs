using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class CodeDesignTests(WpfContext wpf)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_reports_synchronous_and_async_errors_and_can_run_again(bool asynchronous)
    {
        wpf.Run(() =>
        {
            var completed = new TaskCompletionSource();
            var failure = new InvalidOperationException("Command failed.");
            int runs = 0;
            Exception? reported = null;
            var command = new AsyncCommand(async () =>
            {
                runs++;
                if (asynchronous) await Task.Yield();
                throw failure;
            }, reportError: error => { reported = error; completed.SetResult(); });
            command.Execute(null);
            WpfContext.Complete(completed.Task);
            Assert.Same(failure, reported);
            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute(null));
            completed = new TaskCompletionSource();
            command.Execute(null);
            WpfContext.Complete(completed.Task);
            Assert.Equal(2, runs);
        });
    }

    [Fact]
    public void Control_detail_read_failure_is_reported_without_faulting_the_dispatcher()
    {
        wpf.Run(() =>
        {
            var (window, model, _) = WorkbenchControlTests.Window();
            try
            {
                WpfContext.Complete(window.ReadControlDetailAsync(WorkbenchControlTests.CallId,
                    () => throw new InvalidOperationException("Detail read failed.")));
                Assert.Contains(model.Activity.Entries, entry => entry.Level == Services.WorkbenchActivity.Severity.Error
                    && entry.Message.Contains("Detail read failed.", StringComparison.Ordinal));
            }
            finally { window.Close(); WpfContext.Drain(); }
        });
    }
    [Fact]
    public void Window_close_waits_for_detach_without_blocking_the_UI_and_honors_other_vetoes()
    {
        wpf.Run(() =>
        {
            var session = new DelayedSession();
            var model = new MainViewModel(new McpProjectClient(session), new FakeDialogService());
            var window = new MainWindow(model, false);
            bool veto = true;
            window.Closing += (_, e) => e.Cancel |= veto;
            window.Show();
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(0, session.DisposeCalls);
            veto = false;
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, session.DisposeCalls);
            Assert.False(window.IsEnabled);
            Assert.False(session.Finished.Task.IsCompleted);
            // A dispatcher action still runs while the host exit is pending.
            bool responsive = false;
            _ = window.Dispatcher.InvokeAsync(() => responsive = true);
            WpfContext.Drain();
            Assert.True(responsive);
            session.Allow.SetResult();
            WpfContext.Complete(session.Finished.Task);
            WpfContext.Drain();
            Assert.False(window.IsVisible);
            Assert.Equal(1, session.DisposeCalls);
        });
    }

    private sealed class DelayedSession : IMcpSessionClient
    {
        internal readonly TaskCompletionSource Allow = new();
        internal readonly TaskCompletionSource Finished = new();
        internal int DisposeCalls;
        public string? SessionKey => null;
        public JsonObject? State => null;
        public JsonObject? ToolSchema(string tool) => null;
        public event EventHandler<JsonObject>? StateChanged { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }
        public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string releaseKey,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<OperatorSession>>([]);
        public Task AttachAsync(OperatorSession session, CancellationToken token = default)
            => throw new InvalidOperationException("Unexpected attach.");
        public Task CreateAsync(string releaseKey, CancellationToken token = default)
            => throw new InvalidOperationException("Unexpected create.");
        public Task DetachAsync() => throw new InvalidOperationException("Unexpected detach.");
        public void CancelPending() { }
        public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected tool call.");
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await Allow.Task;
            Finished.SetResult();
        }
    }

}
