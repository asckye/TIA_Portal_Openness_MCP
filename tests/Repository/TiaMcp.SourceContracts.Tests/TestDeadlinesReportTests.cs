using System.Globalization;
using System.Text.Json;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class TestDeadlinesReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportWritesConsoleAndOnlyTheConfiguredProcessFile(bool configured)
    {
        Assert.Equal(TimeSpan.FromSeconds(120), TestDeadlines.Hang);
        Assert.Equal(120_000, TestDeadlines.HangMilliseconds);
        string root = Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), "bin-build", "trend-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR");
        var culture = CultureInfo.CurrentCulture;
        var console = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR", configured ? root : null);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Console.SetOut(output);
            TestDeadlines.Report("sample", 1.25, "ms");
            TestDeadlines.Report("quote\"\\\n\t\u0001中文", -2.5, "ms/request");
            Assert.Contains("TREND sample 1.25 ms", output.ToString(), StringComparison.Ordinal);
            string[] files = Directory.GetFiles(root);
            if (!configured) Assert.Empty(files);
            else
            {
                Assert.Equal("trend-" + Environment.ProcessId + ".jsonl", Path.GetFileName(Assert.Single(files)));
                string[] rows = File.ReadAllLines(files[0]);
                Assert.Equal(2, rows.Length);
                using var first = JsonDocument.Parse(rows[0]);
                using var second = JsonDocument.Parse(rows[1]);
                Assert.Equal("sample", first.RootElement.GetProperty("metric").GetString());
                Assert.Equal(1.25, first.RootElement.GetProperty("value").GetDouble());
                Assert.Equal("ms", first.RootElement.GetProperty("unit").GetString());
                Assert.Equal("quote\"\\\n\t\u0001中文", second.RootElement.GetProperty("metric").GetString());
                Assert.Equal(-2.5, second.RootElement.GetProperty("value").GetDouble());
                Assert.Equal("ms/request", second.RootElement.GetProperty("unit").GetString());
                Parallel.For(0, 8, index => TestDeadlines.Report("concurrent-" + index, index, "ms"));
                rows = File.ReadAllLines(files[0]);
                Assert.Equal(10, rows.Length);
                Assert.Equal(8, rows.Skip(2).Select(row =>
                {
                    using var sample = JsonDocument.Parse(row);
                    return sample.RootElement.GetProperty("metric").GetString();
                }).Distinct().Count());
                Environment.SetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR", null);
                TestDeadlines.Report("unset", 3, "ms");
                Assert.Equal(rows, File.ReadAllLines(files[0]));
            }
        }
        finally
        {
            Console.SetOut(console);
            CultureInfo.CurrentCulture = culture;
            Environment.SetEnvironmentVariable("TIA_MCP_TEST_TREND_DIR", previous);
            Directory.Delete(root, true);
        }
    }
}
