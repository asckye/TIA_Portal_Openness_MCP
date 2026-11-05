using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class TrxFailureOutputTests
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Fact]
    public void FailedTestPrintsFullNameAndBoundedMessageAndStack()
    {
        using var tree = new StaticTree();
        var path = WriteTrx(tree.Root, "failed", ["Failed"]);
        var output = DotnetSuites.FailureDetails(path);
        Assert.Contains("FAILED TEST: Example.Checks.Check(row: 0)", output);
        Assert.Contains(new string('m', 400), output);
        Assert.DoesNotContain(new string('m', 401), output);
        Assert.Contains("frame 1\nframe 2\nframe 3\nframe 4".Replace("\n", Environment.NewLine), output);
        Assert.DoesNotContain("frame 5", output);
    }

    [Fact]
    public void LimitCountsDistinctNamesAcrossMergedShards()
    {
        using var tree = new StaticTree();
        var first = WriteTrx(tree.Root, "first", Enumerable.Repeat("Failed", 8).ToArray());
        var second = WriteTrx(tree.Root, "second", Enumerable.Repeat("Failed", 8).ToArray(), offset: 5);
        var result = SuiteSharding.Merge("fixture", Suite(), [first, second], Path.Combine(tree.Root, "merged"));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(10, result.StandardOutput.Split("FAILED TEST: ").Length - 1);
        Assert.Equal(1, result.StandardOutput.Split("FAILED TEST: Example.Checks.Check(row: 5)").Length - 1);
        Assert.Contains("FAILED TEST: Example.Checks.Check(row: 9)", result.StandardOutput);
        Assert.DoesNotContain("FAILED TEST: Example.Checks.Check(row: 10)", result.StandardOutput);
        Assert.Contains("16 failed checks", result.StandardError);
    }

    [Fact]
    public void MissingMessagePrintsOnlyName()
    {
        using var tree = new StaticTree();
        var path = WriteTrx(tree.Root, "failed", ["Failed"]);
        var doc = XDocument.Load(path);
        doc.Descendants(Ns + "Message").Remove();
        doc.Save(path);
        Assert.Equal("FAILED TEST: Example.Checks.Check(row: 0)" + Environment.NewLine, DotnetSuites.FailureDetails(path));
    }

    [Fact]
    public void PassingTrxPrintsNoFailureDetails()
    {
        using var tree = new StaticTree();
        var path = WriteTrx(tree.Root, "passed", ["Passed", "NotExecuted"]);
        Assert.Equal("", DotnetSuites.FailureDetails(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableTrxDoesNotThrow(bool malformed)
    {
        using var tree = new StaticTree();
        var path = Path.Combine(tree.Root, "invalid.trx");
        if (malformed) File.WriteAllText(path, "<broken");
        Assert.Equal("", DotnetSuites.FailureDetails(path));
    }

    [Theory]
    [InlineData("Passed", 0)]
    [InlineData("Failed", 1)]
    public void NonShardedRunnerPreservesResultAndPrintsFailureDetails(string outcome, int exitCode)
    {
        using var tree = new StaticTree();
        var result = DotnetSuites.RunSuite(tree.Root, "fixture", Suite(), tree.Root, process: (_, _, _, _) =>
        {
            WriteTrx(tree.Root, "fixture", [outcome]);
            return new(exitCode, "original output\n", "original error");
        });
        Assert.Equal(exitCode, result.ExitCode);
        Assert.StartsWith("RUN fixture:", result.StandardOutput);
        Assert.Contains("original output", result.StandardOutput);
        Assert.Equal("original error", result.StandardError);
        if (exitCode == 1)
        {
            Assert.Contains("FAILED TEST: Example.Checks.Check(row: 0)", result.StandardOutput);
            Assert.Contains(new string('m', 400), result.StandardOutput);
            Assert.Contains("FAIL: fixture:", result.StandardOutput);
            Assert.Contains("1 failed checks", result.StandardOutput);
        }
        else
        {
            Assert.DoesNotContain("FAILED TEST:", result.StandardOutput);
            Assert.Contains("COMPLETE: 1 fixture checks passed", result.StandardOutput);
        }
    }

    private static JsonObject Suite() => new() { ["project"] = "fixture.csproj", ["arguments"] = new JsonArray(),
        ["minimumPassed"] = 1, ["maximumSkipped"] = 0 };

    private static string WriteTrx(string directory, string name, string[] outcomes, int offset = 0)
    {
        var path = Path.Combine(directory, name + ".trx");
        DotnetSuiteSelfTests.SyntheticTrx(path, outcomes);
        var doc = XDocument.Load(path);
        int index = offset;
        foreach (var row in doc.Descendants(Ns + "UnitTestResult"))
        {
            row.SetAttributeValue("executionId", name + index);
            row.SetAttributeValue("testName", $"Example.Checks.Check(row: {index++})");
            if ((string?)row.Attribute("outcome") == "Failed")
                row.Add(new XElement(Ns + "Output", new XElement(Ns + "ErrorInfo",
                    new XElement(Ns + "Message", new string('m', 450)),
                    new XElement(Ns + "StackTrace", "frame 1\r\nframe 2\r\nframe 3\r\nframe 4\r\nframe 5"))));
        }
        doc.Save(path);
        return path;
    }
}
