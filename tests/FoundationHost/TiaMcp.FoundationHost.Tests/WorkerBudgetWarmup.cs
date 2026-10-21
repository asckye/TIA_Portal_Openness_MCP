using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcp.WorkerChannel;
using Xunit;

// Tests that scale the worker budgets down (0.5, 0.8, 1 or 2 s) measure one operation, not the start of a worker generation.
// Under CPU contention a worker's own start and hello alone took up to 1.3 s on 4 CPUs (P8-stab-1). Such tests start and warm
// each generation under the production budgets, as PlcWorkerTimeoutTests.WarmBeforeScaledBudgets does, and run the operation
// under test with the exact scaled budgets, which disposing this scope restores. The budget values are not changed.
internal sealed class ProductionWorkerBudgets : IDisposable
{
    private readonly IDictionary<string, TimeSpan> budgets;
    private readonly Dictionary<string, TimeSpan> scaled;
    private bool disposed;
    // client: the production WorkerClient or EngineWorkerClient whose own policy the calls read.
    internal ProductionWorkerBudgets(object client)
    {
        budgets = Live(client);
        scaled = new Dictionary<string, TimeSpan>(budgets, StringComparer.Ordinal);
        Assert.Equal(WorkerTimeoutPolicy.Defaults.Keys.Order(StringComparer.Ordinal), budgets.Keys.Order(StringComparer.Ordinal));
        foreach (var (family, production) in WorkerTimeoutPolicy.Defaults) budgets[family] = production;
    }
    internal static IDictionary<string, TimeSpan> Live(object client)
    {
        var policy = (WorkerTimeoutPolicy)client.GetType().GetField("timeouts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        return (IDictionary<string, TimeSpan>)policy.Budgets;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var (family, value) in scaled) budgets[family] = value;
    }
}

// The HTTP fixture host (Program --foundation-http-fixture) runs its session workers in another process. With
// --fixture-scaled-worker-budgets it starts every worker under the production budgets and switches all of them to the scaled
// budgets, or back, when the test writes "fixture-budgets scaled" or "fixture-budgets production" to its input. It answers
// "fixture-budgets: <workers> default=<seconds>" on its output after the switch, so the test proceeds only once it applies.
internal static class FixtureHostWorkerBudgets
{
    internal const string Command = "fixture-budgets ";
    internal const string Reply = "fixture-budgets: ";
    private static readonly List<IDictionary<string, TimeSpan>> Workers = new();
    internal static IReadOnlyList<McpServerTool> Register(object worker, IReadOnlyList<McpServerTool> tools)
    {
        lock (Workers) Workers.Add(ProductionWorkerBudgets.Live(worker));
        return tools;
    }
    internal static string Apply(IReadOnlyDictionary<string, TimeSpan> values)
    {
        lock (Workers)
        {
            foreach (var budgets in Workers)
                foreach (var (family, value) in values) budgets[family] = value;
            return Reply + Workers.Count + " default=" + values["default"].TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
