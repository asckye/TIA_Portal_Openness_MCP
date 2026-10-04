using TiaMcp.Adapters;
using Xunit;

public sealed class WindowsStaFactAttribute : FactAttribute
{
    public WindowsStaFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "STA ownership requires Windows.";
    }
}

public sealed class StudioThreadGuardTests
{
    private static void OnThread(ApartmentState apartment, Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(apartment);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void ConstructionRejectsOtherApartments()
    {
        OnThread(ApartmentState.MTA, () => Assert.Throws<InvalidOperationException>(() => new StudioThreadGuard()));
    }

    [WindowsStaFact]
    public void CallbackRunsSynchronouslyOnOwningSta()
    {
        OnThread(ApartmentState.STA, () =>
        {
            var guard = new StudioThreadGuard();
            var owner = Environment.CurrentManagedThreadId;
            Assert.Equal(42, guard.Run(() =>
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return 42;
            }));
        });
    }

    [WindowsStaFact]
    public void CrossThreadAccessRejectsBeforeOperationAndOwnerRemainsUsable()
    {
        OnThread(ApartmentState.STA, () =>
        {
            var guard = new StudioThreadGuard();
            var calls = 0;
            OnThread(ApartmentState.STA, () => Assert.Throws<InvalidOperationException>(() => guard.Run(() => calls++)));
            OnThread(ApartmentState.MTA, () => Assert.Throws<InvalidOperationException>(() => guard.Run(() => calls++)));
            Assert.Equal(0, calls);
            guard.Run(() => calls++);
            Assert.Equal(1, calls);
        });
    }

    [WindowsStaFact]
    public void ReentryRejectsBeforeOperationAndExceptionsReleaseGuard()
    {
        OnThread(ApartmentState.STA, () =>
        {
            var guard = new StudioThreadGuard();
            var calls = 0;
            guard.Run(() => Assert.Throws<InvalidOperationException>(() => guard.Run(() => calls++)));
            Assert.Equal(0, calls);
            var failure = new IOException("original failure");
            Assert.Same(failure, Assert.Throws<IOException>(() => guard.Run<int>(() => throw failure)));
            guard.Run(() => calls++);
            Assert.Equal(1, calls);
        });
    }
}
