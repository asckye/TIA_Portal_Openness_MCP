using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace TiaMcp.Logic.Generation
{
    // Regular expressions of schemas, packages and SCL sources. Each pattern is parsed once and cached, and
    // every pattern uses the same backtracking engine on .NET Framework and .NET 10, so both judge a pattern
    // alike. The 100 ms budget is counted in the matching thread's own CPU time. The engine's deadline is
    // wall-clock time, so a thread that was descheduled or suspended for a garbage collection during a short
    // match restarts it instead of refusing a valid value. A match whose attempts have together used the budget
    // is refused as before. Without a thread CPU clock the wall-clock deadline alone decides, as it did before.
    internal static class GenerationRegex
    {
        internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
        private const int CacheLimit = 1024;
        private static readonly ConcurrentDictionary<string, Regex> Cache = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private static readonly AsyncLocal<Simulation?> Simulated = new AsyncLocal<Simulation?>();
        private static int cpuClock; // 0 untested, 1 available, -1 unavailable on this platform

        internal static Regex Get(string pattern, RegexOptions options = RegexOptions.CultureInvariant)
        {
            var key = ((int)options).ToString(CultureInfo.InvariantCulture) + ":" + pattern;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var regex = Create(pattern, options);
            // Package patterns are caller data; bound the cache instead of growing it without limit.
            if (Cache.Count >= CacheLimit) Cache.Clear();
            return Cache.GetOrAdd(key, regex);
        }

        internal static bool IsMatch(string pattern, string input)
        {
            var regex = Get(pattern);
            return Run(regex, () => regex.IsMatch(input));
        }

        internal static Match Match(string input, string pattern)
        {
            var regex = Get(pattern);
            return Run(regex, () => regex.Match(input));
        }

        // Evaluates every match inside the budget; a lazy MatchCollection would time out later, outside it.
        internal static Match[] Matches(string input, string pattern)
        {
            var regex = Get(pattern);
            return Run(regex, () => regex.Matches(input).Cast<Match>().ToArray());
        }

        internal static string Replace(string input, string pattern, MatchEvaluator evaluator)
        {
            var regex = Get(pattern);
            return Run(regex, () => regex.Replace(input, evaluator));
        }

        internal static T Run<T>(Regex regex, Func<T> match)
        {
            var simulation = Simulated.Value;
            var used = TimeSpan.Zero;
            for (int attempt = 0; ; attempt++)
            {
                var before = simulation != null ? simulation.Cpu : ThreadCpuTime();
                try
                {
                    simulation?.Attempt(regex, attempt);
                    return match();
                }
                catch (RegexMatchTimeoutException)
                {
                    var after = simulation != null ? simulation.Cpu : ThreadCpuTime();
                    // Without a CPU clock the wall-clock deadline is the only measure, as before.
                    if (before == null || after == null) throw;
                    used += after.Value - before.Value;
                    if (used >= regex.MatchTimeout) throw;
                }
            }
        }

        private static Regex Create(string pattern, RegexOptions options) => new Regex(pattern, options, Budget);

        private static TimeSpan? ThreadCpuTime()
        {
            if (Volatile.Read(ref cpuClock) < 0) return null;
            try
            {
                TimeSpan? value = null;
#if NETFRAMEWORK
                bool windows = true;
#else
                bool windows = OperatingSystem.IsWindows();
#endif
                if (windows)
                {
                    if (NativeMethods.GetThreadTimes(NativeMethods.GetCurrentThread(), out _, out _, out var kernel, out var user))
                        value = TimeSpan.FromTicks(kernel + user);
                }
                else
                {
#if NETFRAMEWORK
                    const int clock = 3;
#else
                    // CLOCK_THREAD_CPUTIME_ID: 3 on Linux, 16 on macOS.
                    int clock = OperatingSystem.IsMacOS() ? 16 : 3;
#endif
                    if (NativeMethods.ClockGetTime(clock, out var time) == 0)
                        value = TimeSpan.FromTicks(time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100);
                }
                Volatile.Write(ref cpuClock, value == null ? -1 : 1);
                return value;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Volatile.Write(ref cpuClock, -1);
                return null;
            }
        }

        // Test seam: the first attempts of every budgeted match observe an expired deadline while the simulated
        // CPU clock of the thread advances by the given amount, as when the thread is descheduled or suspended
        // before the engine checks its deadline. The scope covers the calling asynchronous flow only.
        internal static Simulation SimulateExpiredDeadlines(int attemptsPerMatch, TimeSpan cpuPerAttempt)
        {
            var simulation = new Simulation(attemptsPerMatch, cpuPerAttempt, Simulated.Value);
            Simulated.Value = simulation;
            return simulation;
        }

        internal sealed class Simulation : IDisposable
        {
            private readonly int attemptsPerMatch;
            private readonly TimeSpan cpuPerAttempt;
            private readonly Simulation? previous;
            private long expired;

            internal Simulation(int attemptsPerMatch, TimeSpan cpuPerAttempt, Simulation? previous)
            { this.attemptsPerMatch = attemptsPerMatch; this.cpuPerAttempt = cpuPerAttempt; this.previous = previous; }

            internal TimeSpan? Cpu => TimeSpan.FromTicks(Interlocked.Read(ref expired) * cpuPerAttempt.Ticks);
            internal int ExpiredDeadlines => (int)Interlocked.Read(ref expired);

            internal void Attempt(Regex regex, int attempt)
            {
                if (attempt >= attemptsPerMatch) return;
                Interlocked.Increment(ref expired);
                throw new RegexMatchTimeoutException("", regex.ToString(), regex.MatchTimeout);
            }

            public void Dispose() => Simulated.Value = previous;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            internal long Seconds;
            internal long Nanoseconds;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentThread();

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

            [DllImport("libc", EntryPoint = "clock_gettime")]
            internal static extern int ClockGetTime(int clock, out TimeSpec time);
        }
    }
}
