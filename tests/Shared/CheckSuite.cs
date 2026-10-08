using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace TiaMcp.Engine.Tests.Shared
{
    public sealed class CheckRow
    {
        public string Suite { get; }
        public int Number { get; }
        public string Message { get; }
        public bool Passed { get; }
        public string? SkipReason { get; }

        internal CheckRow(string suite, int number, string message, bool passed, string? skipReason = null)
        { Suite = suite; Number = number; Message = message; Passed = passed; SkipReason = skipReason; }

        public void Verify()
        {
            if (SkipReason != null) throw new XunitException("Skipped rows require CheckTheory: " + SkipReason);
            if (!Passed) throw new XunitException(ToString());
        }

        public override string ToString() => Suite + " #" + Number + ": " + Message;
    }

    public static class CheckSuite
    {
        public static IEnumerable<object[]> Run(string name, Action<Action<bool, string>> run) =>
            Run(name, (check, skip) => run(check));

        public static IEnumerable<object[]> Run(string name, Action<Action<bool, string>, Action<string, string>> run)
        {
            // Legacy suites block on async work. Keep them off xUnit's synchronization context.
            return Task.Run(() =>
            {
                var rows = new List<object[]>();
                void Add(bool passed, string message, string? reason = null)
                {
                    lock (rows) rows.Add(new object[] { new CheckRow(name, rows.Count + 1, message, passed, reason) });
                }
                try { run((ok, message) => Add(ok, message), (message, reason) => Add(false, message, reason)); }
                catch (Exception ex) { Add(false, "Suite threw: " + ex); }
                return rows;
            }).GetAwaiter().GetResult();
        }
    }
}
