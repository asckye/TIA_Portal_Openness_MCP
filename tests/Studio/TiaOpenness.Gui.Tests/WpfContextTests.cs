using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WpfContextTests(WpfContext wpf)
{
    [Fact]
    public void Left_button_reader_matches_native_state_without_capture()
    {
        wpf.Run(() =>
        {
            var window = new Window { Width = 160, Height = 100 };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Drain();
                Assert.True(window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft);
                Assert.True(window.Top + window.ActualHeight < SystemParameters.VirtualScreenTop);
                Assert.Null(Mouse.Captured);
                var expected = (GetKeyState(0x01 /* VK_LBUTTON */) & 0x8000) != 0
                    ? MouseButtonState.Pressed : MouseButtonState.Released;
                Assert.Equal(expected, WpfContext.ReadLeftButton());
                Assert.Null(Mouse.Captured);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Space_wait_pumps_the_queued_mouse_release()
    {
        wpf.Run(() =>
        {
            bool pressed = true;
            int attempts = 0;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => pressed = false));
            WpfContext.PressSpace(() => State(pressed), () =>
            {
                attempts++;
                Assert.False(pressed);
                return new(true, true, Released, Released);
            });
            Assert.Equal(1, attempts);
        });
    }

    [Fact]
    public void Test_windows_are_offscreen_before_their_native_source_is_created()
    {
        wpf.Run(() =>
        {
            var window = new Window { Width = 160, Height = 100, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            bool checkedSource = false;
            window.SourceInitialized += (_, _) =>
            {
                Assert.True(window.Left + window.Width < SystemParameters.VirtualScreenLeft);
                Assert.True(window.Top + window.Height < SystemParameters.VirtualScreenTop);
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.ShowActivated);
                Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
                checkedSource = true;
            };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Drain();
                Assert.True(checkedSource);
                Assert.True(window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft);
                Assert.True(window.Top + window.ActualHeight < SystemParameters.VirtualScreenTop);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Space_releases_capture_left_by_an_aborted_press()
    {
        wpf.Run(() =>
        {
            var button = new AbortedSpaceButton();
            int presses = 0, releases = 0;
            Assert.Throws<InvalidOperationException>(() => WpfContext.PressSpace(() => Released, () =>
            {
                Assert.False(button.IsMouseCaptured);
                presses++;
                return button.Press();
            }, releaseCapture: () =>
            {
                Assert.True(button.IsMouseCaptured);
                releases++;
                button.ReleaseCapture();
            }));
            Assert.True(button.CapturedOnKeyUp);
            Assert.False(button.IsMouseCaptured);
            Assert.NotSame(button, Mouse.Captured);
            Assert.Equal(3, presses);
            Assert.Equal(3, releases);
        });
    }

    [Fact]
    public void Space_releases_capture_when_key_delivery_throws()
    {
        var button = new AbortedSpaceButton();
        var expected = new InvalidOperationException("Interrupted key delivery");
        var actual = Assert.Throws<InvalidOperationException>(() => WpfContext.PressSpace(() => Released, () =>
        {
            button.Press();
            throw expected;
        }, releaseCapture: button.ReleaseCapture));
        Assert.Same(expected, actual);
        Assert.False(button.IsMouseCaptured);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Space_retries_only_the_failed_mouse_contended_press(bool captured, bool downPressed, bool upPressed)
    {
        int attempts = 0;
        WpfContext.PressSpace(() => Released, () => ++attempts == 1
            ? new(false, captured, State(downPressed), State(upPressed))
            : new(true, true, Released, Released), wait: _ => Assert.Fail("A released mouse needs no wait."));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Space_reports_mouse_and_capture_after_three_failed_presses(bool captured, bool pressed)
    {
        wpf.Run(() =>
        {
            var owner = new Button { Name = "OtherCaptureOwner" };
            var state = new WpfContext.SpaceMouseState(pressed ? MouseButtonState.Pressed : MouseButtonState.Released, owner);
            int attempts = 0;
            var error = Assert.Throws<InvalidOperationException>(() => WpfContext.PressSpace(() => Released, () =>
            {
                attempts++;
                return new(false, captured, state, state);
            }));
            Assert.Equal(3, attempts);
            Assert.Contains("after 3 attempt(s)", error.Message);
            Assert.Contains($"left button={state.LeftButton}", error.Message);
            Assert.Contains("capture owner=Button 'OtherCaptureOwner'", error.Message);
            Assert.Contains($"capture acquired={captured}", error.Message);
        });
    }

    [Fact]
    public void Space_does_not_retry_a_failure_without_mouse_contention()
    {
        int attempts = 0;
        var error = Assert.Throws<InvalidOperationException>(() => WpfContext.PressSpace(() => Released, () =>
        {
            attempts++;
            return new(false, true, Released, Released);
        }));
        Assert.Equal(1, attempts);
        Assert.Contains("left button=Released", error.Message);
        Assert.Contains("capture acquired=True", error.Message);
        Assert.Contains("capture owner=<none in WPF>", error.Message);
    }

    [Fact]
    public void Space_waits_in_small_steps_until_release_before_each_press()
    {
        var clock = new TestClock();
        int waits = 0, attempts = 0;
        WpfContext.PressSpace(() => State(waits < (attempts + 1) * 2), () =>
        {
            attempts++;
            return new(attempts == 2, false, Released, Released);
        }, clock, duration =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(10), duration);
            waits++;
            clock.Advance(duration);
        });
        Assert.Equal(2, attempts);
        Assert.Equal(4, waits);
    }

    [Fact]
    public void Space_fails_after_five_seconds_without_pressing_a_held_mouse()
    {
        var clock = new TestClock();
        int attempts = 0, waits = 0;
        var error = Assert.Throws<InvalidOperationException>(() => WpfContext.PressSpace(() => State(true), () =>
        {
            attempts++;
            return new(true, true, Released, Released);
        }, clock, duration =>
        {
            Assert.InRange(duration, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(10));
            waits++;
            clock.Advance(duration);
        }));
        Assert.Equal(0, attempts);
        Assert.Equal(500, waits);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, clock.GetTimestamp());
        Assert.Contains("within 5 s", error.Message);
        Assert.Contains("left button=Pressed", error.Message);
        Assert.Contains("capture owner=<none in WPF>", error.Message);
    }

    [Fact]
    public void Space_on_a_focused_button_raises_both_keys_and_clicks_once_per_press()
    {
        wpf.Run(() =>
        {
            var button = new Button { Content = "Space fixture" };
            var window = new Window { Content = button, Width = 160, Height = 100 };
            var keys = new List<RoutedEvent>();
            int clicks = 0;
            button.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                Assert.Equal(Key.Space, e.Key);
                keys.Add(e.RoutedEvent);
            }), true);
            button.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler((_, e) =>
            {
                Assert.Equal(Key.Space, e.Key);
                keys.Add(e.RoutedEvent);
            }), true);
            button.Click += (_, _) => clicks++;
            try
            {
                WpfContext.ShowWindow(window); window.Activate(); WpfContext.Drain();
                button.Focus();
                Assert.Same(button, Keyboard.FocusedElement);
                WpfContext.PressSpace(button);
                Assert.Equal(1, clicks);
                WpfContext.PressSpace(button);
                Assert.Equal(2, clicks);
                Assert.Equal(new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent }, keys);
            }
            finally { window.Close(); }
        });
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern short GetKeyState(int virtualKey);

    private static WpfContext.SpaceMouseState Released => State(false);
    private static WpfContext.SpaceMouseState State(bool pressed) => new(pressed ? MouseButtonState.Pressed : MouseButtonState.Released, null);

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    private sealed class AbortedSpaceButton
    {
        internal bool CapturedOnKeyUp { get; private set; }
        internal bool IsMouseCaptured { get; private set; }
        internal WpfContext.SpacePressResult Press()
        {
            IsMouseCaptured = true;
            CapturedOnKeyUp = IsMouseCaptured;
            return new(false, true, State(true), Released);
        }
        internal void ReleaseCapture() => IsMouseCaptured = false;
    }
}
