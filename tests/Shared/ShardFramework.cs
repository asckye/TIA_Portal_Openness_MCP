using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using System.Diagnostics;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace TiaMcp.Engine.Tests.Shared
{
    // Partition before execution. Discovery and the ordinary xUnit runners (including
    // CheckTheory's dynamic skip handling) are unchanged; each process stays sequential.
    public sealed class ShardFramework : XunitTestFramework
    {
        public ShardFramework(IMessageSink sink) : base(sink) { }
        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
            new ShardExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
    }

    public static class TestShard
    {
        public static int Bucket(string key, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            // FNV-1a over UTF-16 code units, independent of runtime hash randomization.
            uint hash = 2166136261;
            foreach (char ch in key) hash = unchecked((hash ^ ch) * 16777619);
            return (int)(hash % (uint)count);
        }

        public static bool SplitCases(string className) =>
            className == "PortedPortalAdmissionTests" || className == "BehaviorParityTests" ||
            className == "BehaviorCapabilityTests" ||
            className == "TiaMcp.ReleaseTool.Tests.BuildOutputCacheTests";

        public static string CostKey(string className, string method, string displayName, bool lateTheory) =>
            lateTheory ? "M:" + className + "." + method : "T:" + displayName;

        public static int Assign(string className, string costKey, string fallbackKey, int count,
            IReadOnlyDictionary<string, int> plan, bool reserveSensitive)
        {
            if (count < (reserveSensitive ? 2 : 1)) throw new InvalidOperationException("Invalid test shard count.");
            if (reserveSensitive && TimeSensitiveTests.Contains(className)) return count - 1;
            int ordinaryCount = reserveSensitive ? count - 1 : count;
            if (plan.TryGetValue(costKey, out int planned) || plan.TryGetValue("C:" + className, out planned))
            {
                if (planned < 0 || planned >= ordinaryCount) throw new InvalidOperationException("Cost shard outside shard count.");
                return planned;
            }
            return Bucket(fallbackKey, ordinaryCount);
        }
    }

    internal sealed class ShardExecutor : XunitTestFrameworkExecutor
    {
        private readonly string assembly;
        private readonly DateTime created = DateTime.UtcNow;
        internal ShardExecutor(AssemblyName name, ISourceInformationProvider source, IMessageSink sink)
            : base(name, source, sink) { assembly = name.Name!; }

        protected override void RunTestCases(IEnumerable<IXunitTestCase> cases, IMessageSink sink,
            ITestFrameworkExecutionOptions options)
        {
            string? countText = Environment.GetEnvironmentVariable("TIA_MCP_TEST_SHARD_COUNT");
            string? indexText = Environment.GetEnvironmentVariable("TIA_MCP_TEST_SHARD_INDEX");
            var plan = new Dictionary<string, int>(StringComparer.Ordinal);
            string? planPath = Environment.GetEnvironmentVariable("TIA_MCP_TEST_SHARD_PLAN");
            if (planPath != null)
                foreach (string line in File.ReadAllLines(planPath))
                {
                    var fields = line.Split('\t');
                    if (fields.Length != 2 || !int.TryParse(fields[1], out int bucket)) throw new InvalidOperationException("Invalid cost shard plan.");
                    plan.Add(Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])), bucket);
                }
            if ((countText != null || indexText != null) &&
                Environment.GetEnvironmentVariable("TIA_MCP_TEST_SHARD_ASSEMBLY") == assembly)
            {
                if (!int.TryParse(countText, out int count) || !int.TryParse(indexText, out int index) ||
                    count < 1 || index < 0 || index >= count)
                    throw new InvalidOperationException("Invalid test shard index/count.");
                cases = cases.Where(test =>
                {
                    string cls = test.TestMethod.TestClass.Class.Name;
                    string costKey = TestShard.CostKey(cls, test.TestMethod.Method.Name, test.DisplayName, test is XunitTheoryTestCase);
                    // Late-enumerated theories are indivisible: running their producer
                    // repeatedly would duplicate side effects and all recorded assertions.
                    string key = TestShard.SplitCases(cls) && !(test is XunitTheoryTestCase)
                        ? cls + ":" + test.UniqueID : cls;
                    return TestShard.Assign(cls, costKey, key, count, plan,
                        Environment.GetEnvironmentVariable("TIA_MCP_TEST_RESERVE_SENSITIVE_SHARD") == "1") == index;
                });
            }
            string? timings = Environment.GetEnvironmentVariable("TIA_MCP_TEST_CASE_TIMINGS");
            base.RunTestCases(cases, timings == null ? sink : new CostSink(sink, timings, created), options);
        }
    }

    internal sealed class CostSink : Xunit.LongLivedMarshalByRefObject, IMessageSink
    {
        private readonly IMessageSink inner;
        private readonly string path;
        private readonly Dictionary<string, Stopwatch> clocks = new Dictionary<string, Stopwatch>();
        private readonly DateTime frameworkCreated;
        private DateTime executionStarted;
        private double caseSeconds;
        private double constructorSeconds;
        private Stopwatch? constructor;
        internal CostSink(IMessageSink inner, string path, DateTime created) { this.inner = inner; this.path = path; frameworkCreated = created; }
        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is ITestClassConstructionStarting) constructor = Stopwatch.StartNew();
            if (message is ITestClassConstructionFinished && constructor != null) { constructorSeconds += constructor.Elapsed.TotalSeconds; constructor = null; }
            if (message is ITestAssemblyStarting) executionStarted = DateTime.UtcNow;
            if (message is ITestAssemblyFinished && Environment.GetEnvironmentVariable("TIA_MCP_TEST_PHASE_TIMINGS") is string phases)
            {
                using (var process = Process.GetCurrentProcess())
                    File.WriteAllText(phases, "{\"pid\":" + process.Id + ",\"processStartUtc\":\"" + process.StartTime.ToUniversalTime().ToString("O") +
                        "\",\"frameworkCreatedUtc\":\"" + frameworkCreated.ToString("O") + "\",\"executionStartUtc\":\"" + executionStarted.ToString("O") +
                        "\",\"executionEndUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"caseSeconds\":" + caseSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"constructorSeconds\":" + constructorSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            if (message is ITestCaseStarting start) clocks[start.TestCase.UniqueID] = Stopwatch.StartNew();
            if (message is ITestCaseFinished end && clocks.TryGetValue(end.TestCase.UniqueID, out var timer))
            {
                caseSeconds += timer.Elapsed.TotalSeconds;
                var test = end.TestCase;
                string key = TestShard.CostKey(test.TestMethod.TestClass.Class.Name, test.TestMethod.Method.Name, test.DisplayName, test is XunitTheoryTestCase);
                File.AppendAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(key)) + "\t" + timer.Elapsed.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                clocks.Remove(test.UniqueID);
            }
            return inner.OnMessage(message);
        }
    }
}
