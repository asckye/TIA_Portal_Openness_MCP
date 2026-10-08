using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

// The CLI and xUnit suite run the same fourteen cases as the original Python runner.
internal static class DotnetSuiteSelfTests
{
    internal static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("platform override", PlatformOverride),
        ("minimum and growth", () => WithFixture((path, _) => { foreach (var count in new[] { 2, 3 }) { var r = Check(path, Enumerable.Repeat("Passed", count).ToArray()); Require(r.Errors.Count == 0 && r.Counts["methods"]![0]!["passed"]!.GetValue<int>() == count); } })),
        ("deliberate failure", () => WithFixture((path, _) => Require(Check(path, ["Passed", "Passed", "Failed"]).Errors.Contains("1 failed checks")))),
        ("below minimum", () => WithFixture((path, _) => Require(Check(path, ["Passed"]).Errors.Contains("passed 1 below minimum 2")))),
        ("zero executed even with zero minimum", () => WithFixture((path, _) => Require(Check(path, [], 0).Errors.Contains("zero tests executed")))),
        ("skip limit and allowed skip", () => WithFixture((path, _) => { Require(Check(path, ["Passed", "Passed", "NotExecuted"]).Errors.Contains("skipped 1 above maximum 0")); var r = Check(path, ["Passed", "Passed", "NotExecuted"], maximum: 1); Require(r.Errors.Count == 0 && r.Counts["methods"]![0]!["skipped"]!.GetValue<int>() == 1); })),
        ("all skipped is not execution", () => WithFixture((path, _) => Require(Check(path, ["NotExecuted"], 0, 1).Errors.Contains("zero tests executed")))),
        ("vstest skip counter is zero", () => WithFixture((path, _) => { var r = Check(path, ["Passed", "Passed", "NotExecuted"], maximum: 1, overrides: new() { ["notExecuted"] = "0" }); Require(r.Errors.Count == 0 && r.Counts["skipped"]!.GetValue<int>() == 1); Require(Check(path, ["Passed", "Passed", "NotExecuted"], overrides: new() { ["notExecuted"] = "0" }).Errors.Contains("skipped 1 above maximum 0")); })),
        ("other failures", () => WithFixture((path, _) => { foreach (var key in new[] { "error", "timeout", "aborted", "inconclusive" }) Require(Check(path, ["Passed", "Passed"], overrides: new() { [key] = "1" }).Errors.Count != 0); })),
        ("inflated counters", () => WithFixture((path, _) => Require(Check(path, ["Passed"], overrides: new() { ["total"] = "2", ["executed"] = "2", ["passed"] = "2" }).Errors.Count != 0))),
        ("unknown result", () => WithFixture((path, _) => Require(Check(path, ["Passed", "Error"]).Errors.Count != 0))),
        ("invalid xml and counters", InvalidXml),
        ("missing trx removes stale success", MissingTrx),
        ("runner command report and nonzero exit", RunnerCommand)
    ];

    internal static void SyntheticTrx(string path, string[] outcomes, Dictionary<string, string>? overrides = null)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counts = new Dictionary<string, string>
        {
            ["total"] = outcomes.Length.ToString(), ["executed"] = outcomes.Count(x => x != "NotExecuted").ToString(),
            ["passed"] = outcomes.Count(x => x == "Passed").ToString(), ["failed"] = outcomes.Count(x => x == "Failed").ToString(),
            ["notExecuted"] = outcomes.Count(x => x == "NotExecuted").ToString()
        };
        if (overrides is not null) foreach (var (key, value) in overrides) counts[key] = value;
        var root = new XElement(ns + "TestRun", new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
            new XElement(ns + "Counters", counts.Select(p => new XAttribute(p.Key, p.Value)))),
            new XElement(ns + "TestDefinitions", outcomes.Select((_, i) => new XElement(ns + "UnitTest", new XAttribute("id", i),
                new XElement(ns + "TestMethod", new XAttribute("className", "Example.Checks"), new XAttribute("name", "Check"))))),
            new XElement(ns + "Results", outcomes.Select((outcome, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", i), new XAttribute("executionId", i), new XAttribute("outcome", outcome)))));
        new XDocument(root).Save(path);
    }

    private static (JsonObject Counts, List<string> Errors) Check(string path, string[] outcomes, int minimum = 2, int maximum = 0, Dictionary<string, string>? overrides = null)
    { SyntheticTrx(path, outcomes, overrides); return DotnetSuites.EvaluateTrx(path, minimum, maximum); }

    private static void WithFixture(Action<string, string> test)
    {
        using var fixture = new OfflineFixtures("dotnet-gate-self-test-");
        test(Path.Combine(fixture.DirectoryPath, "example.trx"), fixture.DirectoryPath);
    }
    private static void Require(bool value) { if (!value) throw new ReleaseException("dotnet gate self-check failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ReleaseException or ArgumentException or FormatException or System.Xml.XmlException) { return; }
        throw new ReleaseException("dotnet gate self-check expected rejection");
    }
    private static JsonObject Suite() => new() { ["project"] = "example.csproj", ["minimumPassed"] = 2, ["maximumSkipped"] = 0, ["arguments"] = new JsonArray("-p:DefineConstants=TIA_V20") };

    private static void PlatformOverride() => WithFixture((_, directory) =>
    {
        var catalog = Path.Combine(directory, "catalog.json");
        File.WriteAllText(Path.Combine(directory, "example.csproj"), "");
        var value = new JsonObject { ["example"] = new JsonObject { ["project"] = "example.csproj", ["minimumPassed"] = 3, ["maximumSkipped"] = 0,
            ["arguments"] = new JsonArray(), ["platforms"] = new JsonObject { ["linux"] = new JsonObject { ["minimumPassed"] = 2, ["reason"] = "one Windows-only check" } } } };
        File.WriteAllText(catalog, value.ToJsonString());
        Require(DotnetSuites.LoadCatalog(catalog, directory, "linux")["example"]!["minimumPassed"]!.GetValue<int>() == 2);
        Require(DotnetSuites.LoadCatalog(catalog, directory, "windows")["example"]!["minimumPassed"]!.GetValue<int>() == 3);
        value["example"]!["platforms"]!["linux"]!.AsObject().Remove("reason");
        File.WriteAllText(catalog, value.ToJsonString());
        Reject(() => DotnetSuites.LoadCatalog(catalog, directory, "linux"));
    });

    private static void InvalidXml() => WithFixture((path, _) =>
    {
        foreach (var text in new[] { "<broken", "<TestRun />" }) { File.WriteAllText(path, text); Reject(() => DotnetSuites.EvaluateTrx(path, 2, 0)); }
        foreach (var count in new[] { "-1", "bad" }) Reject(() => Check(path, ["Passed"], overrides: new() { ["passed"] = count }));
    });

    private static void MissingTrx() => WithFixture((path, directory) =>
    {
        SyntheticTrx(path, ["Passed", "Passed"]);
        var result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, process: (_, _, _, _) => new CommandResult(0, "", ""));
        Require(result.ExitCode != 0 && !File.Exists(path) && File.ReadAllText(Path.Combine(directory, "example.json")).Contains("missing TRX", StringComparison.Ordinal));
        result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, process: (_, _, _, _) => throw new System.ComponentModel.Win32Exception("dotnet executable missing"));
        Require(result.ExitCode != 0 && File.ReadAllText(Path.Combine(directory, "example.json")).Contains("dotnet executable missing", StringComparison.Ordinal));
    });

    private static void RunnerCommand() => WithFixture((path, directory) =>
    {
        foreach (var code in new[] { 0, 1 })
        {
            var result = DotnetSuites.RunSuite(Repository.FindRoot(), "example", Suite(), directory, arguments: ["--no-restore"], process: (_, arguments, _, _) =>
            {
                var args = arguments.ToArray();
                Require(args[0] == "test" && args.Contains("-p:DefineConstants=TIA_V20") && args.Contains("--no-restore") && args.Contains("trx;LogFileName=example.trx"));
                SyntheticTrx(path, ["Passed", "Passed"]);
                return new CommandResult(code, "", "");
            });
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "example.json")))!;
            Require((result.ExitCode == 0) == (code == 0) && report["passed"]!.GetValue<int>() == 2 && report["methods"]![0]!["testMethod"]!.GetValue<string>() == "Check");
        }
    });
}
