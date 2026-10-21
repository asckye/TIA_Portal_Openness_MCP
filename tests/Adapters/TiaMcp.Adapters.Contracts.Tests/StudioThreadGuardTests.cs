using TiaMcp.Adapters;
using Xunit;

public sealed class WindowsStaFactAttribute : FactAttribute
{
    public WindowsStaFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "STA ownership requires Windows.";
    }
}

public sealed class WindowsStaTheoryAttribute : TheoryAttribute
{
    public WindowsStaTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Apartment ownership requires Windows.";
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

    // P8-81c F3: V14 SP1-V19 keep the STA owner; V20/V21 own an MTA thread like the PlcWorker.
    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")]
    public void ConstructionRejectsOtherApartments(string release)
    {
        OnThread(ApartmentState.MTA, () => Assert.Throws<InvalidOperationException>(() => new StudioThreadGuard(release)));
    }

    [WindowsStaTheory]
    [InlineData("20")] [InlineData("21")]
    public void V20AndV21OwnAnMtaThreadAndRejectSta(string release)
    {
        OnThread(ApartmentState.STA, () => Assert.Throws<InvalidOperationException>(() => new StudioThreadGuard(release)));
        OnThread(ApartmentState.MTA, () =>
        {
            var guard = new StudioThreadGuard(release);
            var owner = Environment.CurrentManagedThreadId;
            Assert.Equal(42, guard.Run(() =>
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                return 42;
            }));
            var calls = 0;
            OnThread(ApartmentState.MTA, () => Assert.Throws<InvalidOperationException>(() => guard.Run(() => calls++)));
            Assert.Equal(0, calls);
        });
    }

    [WindowsStaFact]
    public void CallbackRunsSynchronouslyOnOwningSta()
    {
        OnThread(ApartmentState.STA, () =>
        {
            var guard = new StudioThreadGuard("19");
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
            var guard = new StudioThreadGuard("19");
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
            var guard = new StudioThreadGuard("19");
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
