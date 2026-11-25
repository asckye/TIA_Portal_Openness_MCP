using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

internal static class Program
{
    private sealed class Options : Dictionary<string, object>, ITestFrameworkDiscoveryOptions, ITestFrameworkExecutionOptions
    {
        public T GetValue<T>(string name) => TryGetValue(name, out var value) ? (T)value : default!;
        public void SetValue<T>(string name, T value) => this[name] = value!;
    }

    private static int Main(string[] args) => FixtureCrashGuard.Run(() =>
    {
        if (args.Length == 2 && args[0] == "--worker-reply")
        {
            Console.WriteLine(TiaMcp.PlcWorker.WorkerJson.Serialize(TiaMcp.Engine.Tests.RefusalFixture.Reply(args[1])));
            return 0;
        }
        string runtime = RuntimeInformation.FrameworkDescription;
        Assert.StartsWith(".NET Framework 4.8", runtime);
        Console.WriteLine(runtime);
        var options = new Options();
        var diagnostics = new NullMessageSink();
        using var framework = new XunitTestFramework(diagnostics);
        using var executor = framework.GetExecutor(Assembly.GetExecutingAssembly().GetName());
        bool error = false;
        var sink = new DelegatingMessageSink<ITestAssemblyFinished>(diagnostics, message => {
            if (message is IFailureInformation failure)
            {
                error = true;
                if (message is ITestFailed test) Console.Error.WriteLine(test.Test.DisplayName);
                Console.Error.WriteLine(string.Join(Environment.NewLine, failure.Messages));
            }
        });
        executor.RunAll(sink, options, options);
        sink.Finished.WaitOne();
        var result = sink.FinalMessage;
        Console.WriteLine($"{result.TestsRun} cases, {result.TestsFailed} failed, {result.TestsSkipped} skipped");
        const int minimumCases = 36;
        return !error && result.TestsRun >= minimumCases && result.TestsFailed == 0 && result.TestsSkipped == 0 ? 0 : 1;
    });
}
