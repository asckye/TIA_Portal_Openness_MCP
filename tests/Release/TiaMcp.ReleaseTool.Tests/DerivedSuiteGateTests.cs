using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class DerivedSuiteGateTests
{
    private static string Root => Repository.FindRoot();

    private static JsonObject Catalog => DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root, "windows");

    public static IEnumerable<object[]> DerivedSuites => DerivedSuiteGates.Supersets.Keys.Select(name => new object[] { name });

    private static string TestClass(string name) => name switch
    {
        "foundation" => "FoundationChecks", "worker-robustness" => "WorkerRobustnessTests",
        "workbench-control-host" => "WorkbenchControlHostTests", "shared-pipeline" => "SharedPipelineTests",
        "shared-lifecycle-plc" => "PlcSharedLifecycleTests", "shared-lifecycle" => "SharedLifecycleTests",
        "workbench-control" => "TiaOpenness.Gui.Tests.WorkbenchControlTests", "mcp-panel" => "TiaOpenness.Gui.Tests.McpPanelTests",
        "workbench-vci-prefill" => "TiaOpenness.Gui.Tests.VciPanelTests", "crash-evidence" => "TiaOpenness.Gui.Tests.CrashEvidencePortTests",
        "vci-read-only-binding" => "TiaOpenness.Core.Tests.VciReadOnlyBindingTests",
        _ => throw new InvalidOperationException(name)
    };

    [Fact]
    public void ContainmentIsProvedOverFiltersRatherThanTheRowsThatHappenedToBeReturned()
    {
        JsonObject Suite(string? filter) => new() { ["arguments"] = filter == null ? new JsonArray() : new JsonArray("--filter", filter) };
        Assert.True(DerivedSuiteGates.FilterContains(Suite(null), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.True(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName!~Operator"), Suite("FullyQualifiedName!~ApiMetadataChecks&FullyQualifiedName!~Operator")));
        Assert.True(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName~Tests"), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.False(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName!~Operator"), Suite("FullyQualifiedName~WorkerRobustnessTests")));
        Assert.False(DerivedSuiteGates.FilterContains(Suite("FullyQualifiedName~First"), Suite("FullyQualifiedName~First|FullyQualifiedName~Second")));
    }

    [Fact]
    public void NormalizationChangesOnlyTheFoundationProducerOffsetAndRecognizesExactlyTheSixteenSdkOnlyRows()
    {
        const string a = "FoundationChecks.Check(row: OfflineXmlBuilderTests #100: 19 GlobalDB DB identity and layout retained)";
        Assert.Equal(DerivedSuiteGates.Normalize("FoundationChecks", a), DerivedSuiteGates.Normalize("FoundationChecks", a.Replace("#100:", "#116:")));
        Assert.Equal(a, DerivedSuiteGates.Normalize("OtherChecks", a));
        Assert.NotEqual(DerivedSuiteGates.Normalize("FoundationChecks", a), DerivedSuiteGates.Normalize("FoundationChecks", a.Replace("19 GlobalDB", "20 GlobalDB")));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var kind in new[] { "UDT", "GlobalDB" })
        {
            var row = new DerivedSuiteGates.Case("FoundationChecks", "Check", $"FoundationChecks.Check(row: OfflineXmlBuilderTests #100: {key} {kind} official interface XSD: )", new XElement("row"), new XElement("test"));
            Assert.True(DerivedSuiteGates.FoundationSdkOnly(row));
            Assert.False(DerivedSuiteGates.FoundationSdkOnly(row with { Display = row.Display.Replace("XSD: ", "XSD changed: ") }));
            Assert.False(DerivedSuiteGates.FoundationSdkOnly(row with { Method = "Different" }));
        }
    }

    internal static string Trx(string directory, string name, (string Class, string Method, string Display, string Outcome)[] rows)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        string Id((string Class, string Method, string Display, string Outcome) row) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(row.Class + "\0" + row.Method + "\0" + row.Display)));
        string run = Guid.NewGuid().ToString("N");
        var definitions = rows.Select(row => new XElement(ns + "UnitTest", new XAttribute("id", Id(row)),
            new XElement(ns + "TestMethod", new XAttribute("className", row.Class), new XAttribute("name", row.Method))));
        var results = rows.Select((row, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", Id(row)),
            new XAttribute("executionId", run + "-" + i), new XAttribute("testName", row.Display), new XAttribute("outcome", row.Outcome), new XAttribute("duration", "00:00:00.001")));
        int failed = rows.Count(row => row.Outcome == "Failed"), passed = rows.Count(row => row.Outcome == "Passed");
        var doc = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "TestDefinitions", definitions), new XElement(ns + "Results", results),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", failed > 0 ? "Failed" : "Completed"), new XElement(ns + "Counters",
                new XAttribute("total", rows.Length), new XAttribute("executed", passed + failed), new XAttribute("passed", passed), new XAttribute("failed", failed), new XAttribute("notExecuted", rows.Length - passed - failed)))));
        var path = Path.Combine(directory, name + ".trx"); doc.Save(path); return path;
    }

    [Theory]
    [MemberData(nameof(DerivedSuites))]
    public void DerivedRowsStillEnforceMinimumFailureAndSkipGates(string name)
    {
        using var temp = new OfflineFixtures();
        var suite = Catalog[name]!.DeepClone().AsObject();
        suite["minimumPassed"] = 2; suite["maximumSkipped"] = 0;
        string cls = TestClass(name);
        var source = Trx(temp.DirectoryPath, "source", [(cls, "Check", "first", "Passed"), (cls, "Check", "second", "Passed")]);
        var output = Path.Combine(temp.DirectoryPath, "derived");
        Assert.Equal(0, DerivedSuiteGates.Evaluate(name, suite, source, output, "superset").ExitCode);
        source = Trx(temp.DirectoryPath, "source", [(cls, "Check", "first", "Passed")]);
        Assert.Equal(1, DerivedSuiteGates.Evaluate(name, suite, source, output, "superset").ExitCode);
        source = Trx(temp.DirectoryPath, "source", [(cls, "Check", "first", "Passed"), (cls, "Check", "second", "Failed")]);
        Assert.Equal(1, DerivedSuiteGates.Evaluate(name, suite, source, output, "superset").ExitCode);
        source = Trx(temp.DirectoryPath, "source", [(cls, "Check", "first", "Passed"), (cls, "Check", "second", "NotExecuted")]);
        Assert.Equal(1, DerivedSuiteGates.Evaluate(name, suite, source, output, "superset").ExitCode);
    }
}
