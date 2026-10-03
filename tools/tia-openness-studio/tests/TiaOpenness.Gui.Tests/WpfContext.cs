using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TiaOpenness.Gui;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// One STA thread with a running dispatcher, shared by every test that touches WPF.
///
/// WPF objects can only be created on an STA thread, and <see cref="Application"/> is a
/// per-process singleton, so spinning up a thread per test would fail on the second one. The
/// real <see cref="App"/> is instantiated rather than a bare Application: that is what loads
/// App.xaml's merged dictionaries, so the tests check the resource set the product actually
/// ships instead of a list copied into the test project that would drift from it.
/// </summary>
public sealed class WpfContext : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    public WpfContext()
    {
        var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            if (Application.Current is null)
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-test-ui",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The WPF test thread did not start.");
        }
    }

    /// <summary>Runs on the UI thread; exceptions are re-thrown on the caller's thread.</summary>
    public void Run(Action action) => Ui().Invoke(action);

    public T Run<T>(Func<T> action) => Ui().Invoke(action);

    /// <summary>
    /// Runs with the language forced, then puts it back. The catalogue is a process-wide
    /// singleton, so a test that leaves it in Chinese would quietly change the next one.
    /// </summary>
    public void RunWithLanguage(AppLanguage language, Action action) => Run(() =>
    {
        var previous = Loc.Current.Language;
        Loc.Current.Language = language;
        try
        {
            action();
        }
        finally
        {
            Loc.Current.Language = previous;
        }
    });

    private Dispatcher Ui()
        => _dispatcher ?? throw new InvalidOperationException("The WPF test thread has no dispatcher.");

    public void Dispose() => _dispatcher?.InvokeShutdown();
}

[CollectionDefinition(WpfCollection.Name)]
public sealed class WpfCollection : ICollectionFixture<WpfContext>
{
    public const string Name = "wpf";
}
