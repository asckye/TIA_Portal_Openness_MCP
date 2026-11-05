using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    static WpfContext()
    {
        // Product-created modal windows bypass ShowWindow; move them before their Loaded handlers run.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) KeepOffscreen((Window)sender);
            }));
    }

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

    /// <summary>
    /// Runs on the UI thread; exceptions are re-thrown on the caller's thread. Work the test left queued (for example the
    /// window's deferred close after its asynchronous MCP detach) finishes before the next test starts.
    /// </summary>
    public void Run(Action action)
    {
        try { Ui().Invoke(action); }
        finally { Ui().Invoke(() => { }, DispatcherPriority.ContextIdle); }
    }

    public T Run<T>(Func<T> action)
    {
        try { return Ui().Invoke(action); }
        finally { Ui().Invoke(() => { }, DispatcherPriority.ContextIdle); }
    }

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

    internal static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    internal static void ShowWindow(Window window)
    {
        KeepOffscreen(window);
        window.Show();
    }

    private static void KeepOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - 10000;
        window.Top = SystemParameters.VirtualScreenTop - 10000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
    }

    internal static void PressSpace(ButtonBase button)
    {
        bool clicked = false;
        RoutedEventHandler onClick = (_, _) => clicked = true;
        button.AddHandler(ButtonBase.ClickEvent, onClick, true);
        try
        {
            PressSpace(ReadMouse, () =>
            {
                clicked = false;
                RaiseKey(Keyboard.KeyDownEvent);
                var down = ReadMouse();
                bool captured = button.IsMouseCaptured;
                RaiseKey(Keyboard.KeyUpEvent);
                return new SpacePressResult(clicked, captured, down, ReadMouse());
            }, releaseCapture: ReleaseCapture);
        }
        finally
        {
            ReleaseCapture();
            button.RemoveHandler(ButtonBase.ClickEvent, onClick);
        }

        void RaiseKey(RoutedEvent routedEvent) => button.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Space) { RoutedEvent = routedEvent });
        static SpaceMouseState ReadMouse() => new(Mouse.LeftButton, Mouse.Captured);
        void ReleaseCapture()
        {
            if (button.IsMouseCaptured) button.ReleaseMouseCapture();
        }
    }

    // The input seam keeps contention tests deterministic; the real path above always raises both Space events.
    internal static void PressSpace(Func<SpaceMouseState> readMouse, Func<SpacePressResult> press,
        TimeProvider? clock = null, Action<TimeSpan>? wait = null, Action? releaseCapture = null)
    {
        clock ??= TimeProvider.System;
        wait ??= WaitForInput;
        long started = clock.GetTimestamp();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            SpaceMouseState mouse;
            while ((mouse = readMouse()).LeftButton == MouseButtonState.Pressed)
            {
                var remaining = TimeSpan.FromSeconds(5) - clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw new InvalidOperationException($"Space could not wait for the left button to be released within 5 s; {Describe(mouse)}.");
                wait(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10));
            }

            SpacePressResult result;
            try { result = press(); }
            finally { releaseCapture?.Invoke(); }
            if (result.Clicked) return;
            bool contention = !result.CaptureAcquired || result.AfterKeyDown.LeftButton == MouseButtonState.Pressed
                || result.AfterKeyUp.LeftButton == MouseButtonState.Pressed;
            string reason = $"capture acquired={result.CaptureAcquired}; key down: {Describe(result.AfterKeyDown)}; key up: {Describe(result.AfterKeyUp)}";
            if (!contention || attempt == 3)
                throw new InvalidOperationException($"Space did not click after {attempt} attempt(s); {reason}.");
        }
    }

    internal static void WaitForInput(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static string Describe(SpaceMouseState mouse)
    {
        string owner = mouse.CaptureOwner switch
        {
            FrameworkElement element => $"{element.GetType().Name} '{element.Name}'",
            null => "<none in WPF>",
            _ => mouse.CaptureOwner.GetType().Name,
        };
        return $"left button={mouse.LeftButton}, capture owner={owner}";
    }

    internal readonly record struct SpaceMouseState(MouseButtonState LeftButton, IInputElement? CaptureOwner);
    internal readonly record struct SpacePressResult(bool Clicked, bool CaptureAcquired,
        SpaceMouseState AfterKeyDown, SpaceMouseState AfterKeyUp);

    internal static void Complete(System.Threading.Tasks.Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background, (_, _) =>
            { if (task.IsCompleted) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame); timer.Stop();
        }
        task.GetAwaiter().GetResult(); Drain();
    }

    private Dispatcher Ui()
        => _dispatcher ?? throw new InvalidOperationException("The WPF test thread has no dispatcher.");

    /// <summary>
    /// Waits for the dispatcher to finish tearing down its windows. Returning right after the shutdown
    /// request let the test host exit while the STA thread still ran window procedures, which the CLR
    /// reports as a crash ("managed code after the runtime thread state has been destroyed").
    /// </summary>
    public void Dispose()
    {
        if (_dispatcher is null) return;
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(30));
    }
}

[CollectionDefinition(WpfCollection.Name)]
public sealed class WpfCollection : ICollectionFixture<WpfContext>
{
    public const string Name = "wpf";
}
