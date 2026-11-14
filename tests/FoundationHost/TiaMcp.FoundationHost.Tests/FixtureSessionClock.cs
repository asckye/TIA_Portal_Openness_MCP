internal sealed class FixtureSessionClock : TimeProvider
{
    internal const string Command = "fixture-clock advance";
    internal const string Reply = "fixture-clock: advanced";
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    private long ticks;
    public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref ticks));
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
}
