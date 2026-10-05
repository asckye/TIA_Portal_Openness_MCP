using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class SwallowedExceptionsTests : IDisposable
    {
        private readonly Action<string>? previousSink = SwallowedExceptions.Sink;
        private readonly string site = Guid.NewGuid().ToString("N");

        public void Dispose() => SwallowedExceptions.Sink = previousSink;

        private sealed class UnreadableException : Exception
        {
            internal UnreadableException() { HResult = unchecked((int)0x81234567); }
            public override string Message => throw new InvalidOperationException("Message must not be read");
            public override string ToString() => throw new InvalidOperationException("ToString must not be called");
            public override bool Equals(object? obj) => throw new InvalidOperationException("Equals must not be called");
            public override int GetHashCode() => throw new InvalidOperationException("GetHashCode must not be called");
        }

        [Fact]
        public void FirstOccurrencePerSiteIsLoggedAndEveryOccurrenceIsCounted()
        {
            var lines = new List<string>();
            SwallowedExceptions.Sink = lines.Add;
            SwallowedExceptions.Note(site, new InvalidOperationException("private message"));
            SwallowedExceptions.Note(site, new ArgumentException("another private message"));
            SwallowedExceptions.Note(site + "-other", new Exception());

            Assert.Equal(2, lines.Count);
            Assert.Equal("site=" + site + " type=System.InvalidOperationException hresult=80131509", lines[0]);
            Assert.Equal(2, SwallowedExceptions.Snapshot()[site]);
            Assert.Equal(1, SwallowedExceptions.Snapshot()[site + "-other"]);
        }

        [Fact]
        public void DisabledSinkStillCountsWithoutReplayingTheFirstOccurrence()
        {
            SwallowedExceptions.Sink = null;
            SwallowedExceptions.Note(site, new UnreadableException());
            var lines = new List<string>();
            SwallowedExceptions.Sink = lines.Add;
            SwallowedExceptions.Note(site, new UnreadableException());

            Assert.Empty(lines);
            Assert.Equal(2, SwallowedExceptions.Snapshot()[site]);
        }

        [Fact]
        public void LogsOnlySafeScalarsWithoutReadingExceptionOverrides()
        {
            var lines = new List<string>();
            SwallowedExceptions.Sink = lines.Add;
            SwallowedExceptions.Note(site + "\r\nline", new UnreadableException());

            Assert.Equal("site=" + site + "  line type=" + typeof(UnreadableException).FullName
                + " hresult=81234567", Assert.Single(lines));
        }

        [Fact]
        public void SinkFailureIsSwallowedAndNotRetried()
        {
            int attempts = 0;
            SwallowedExceptions.Sink = _ => { attempts++; throw new UnreadableException(); };
            Assert.Null(Record.Exception(() => SwallowedExceptions.Note(site, new Exception())));
            SwallowedExceptions.Note(site, new Exception());
            Assert.Equal(1, attempts);
            Assert.Equal(2, SwallowedExceptions.Snapshot()[site]);

            var lines = new List<string>();
            SwallowedExceptions.Sink = lines.Add;
            SwallowedExceptions.Note(site + "-other", new Exception());
            Assert.Single(lines);
        }

        [Fact]
        public void ConcurrentNotesAndSnapshotsKeepExactCountsAndOneLogPerSite()
        {
            var lines = new ConcurrentQueue<string>();
            SwallowedExceptions.Sink = lines.Enqueue;
            var ex = new UnreadableException();
            Parallel.For(0, 10000, index =>
            {
                SwallowedExceptions.Note(site, ex);
                SwallowedExceptions.Note(site + "-other", ex);
                if (index % 100 == 0) Assert.InRange(SwallowedExceptions.Snapshot()[site], 1, 10000);
            });

            Assert.Equal(2, lines.Count);
            Assert.Equal(10000, SwallowedExceptions.Snapshot()[site]);
            Assert.Equal(10000, SwallowedExceptions.Snapshot()[site + "-other"]);
        }

        [Fact]
        public void SnapshotIsDetachedFromTheCounters()
        {
            SwallowedExceptions.Sink = null;
            SwallowedExceptions.Note(site, new Exception());
            var snapshot = SwallowedExceptions.Snapshot();
            SwallowedExceptions.Note(site, new Exception());
            Assert.Equal(1, snapshot[site]);
            snapshot[site] = 42;
            Assert.Equal(2, SwallowedExceptions.Snapshot()[site]);
        }

        [Fact]
        public void InvalidArgumentsCannotEscapeTheDiagnosticHelper()
        {
            int emitted = 0;
            SwallowedExceptions.Sink = _ => Interlocked.Increment(ref emitted);
            Assert.Null(Record.Exception(() => SwallowedExceptions.Note(null!, new Exception())));
            Assert.Null(Record.Exception(() => SwallowedExceptions.Note(site, null!)));
            Assert.Equal(0, emitted);
            Assert.False(SwallowedExceptions.Snapshot().ContainsKey(site));
        }
    }
}
