using System.Text.Json;
using System;
using System.IO;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OptionalProductTests
{
    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void Cfc_crash_policy_refuses_every_unsafe_action_on_every_enabled_release(int major)
    {
        foreach (string action in new[] { "export", "selectiveExport", "exportInstructionData", "read", "add", "change", "remove" })
        {
            var error = Assert.Throws<NotSupportedException>(() => CfcSafetyPolicy.RequireSafeChartInventory(major, action));
            Assert.Contains("independent CFC chart inventory", error.Message);
            Assert.Contains("CompleteExport", error.Message);
            Assert.Contains("unknown-chart GetChartProtection", error.Message);
            Assert.Contains("skipChartPreflight", error.Message);
        }
        Assert.True(PortedFamilies.Available(major.ToString(System.Globalization.CultureInfo.InvariantCulture), "ManageCfcChartProtection"));
    }

    [Theory]
    [InlineData("16", "archive")]
    [InlineData("17", "archive")]
    [InlineData("18", "archive")]
    [InlineData("16", "fAddressArchive")]
    [InlineData("18", "fAddressArchive")]
    [InlineData("19", "retrieve")]
    public void Missing_password_overloads_are_refused(string release, string action)
        => Assert.NotNull(OptionalProductCapabilities.Unsupported(release, "ManageSinumerikArchive", action: action, password: "fixture"));

    [Theory]
    [InlineData("19", "archive")]
    [InlineData("20", "archive")]
    [InlineData("19", "fAddressArchive")]
    [InlineData("20", "fAddressArchive")]
    [InlineData("20", "retrieve")]
    public void Present_password_overloads_remain_available(string release, string action)
        => Assert.Null(OptionalProductCapabilities.Unsupported(release, "ManageSinumerikArchive", action: action, password: "fixture"));

    [Theory]
    [InlineData("Siemens.Engineering.CFC", true)]
    [InlineData("Siemens.Engineering.CFC.dll", true)]
    [InlineData("Siemens.Engineering.TestSuite, Version=21.0.0.0, Culture=neutral", true)]
    [InlineData("Siemens.Engineering.TestSuite, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b", true)]
    [InlineData("Siemens.Engineering.CFC, Version=recipe.csv", false)]
    [InlineData("Siemens.Engineering.CFC, recipe.csv", false)]
    [InlineData("Siemens.Engineering.CFC, Version=99999999999999.0", false)]
    [InlineData("Siemens.Engineering.recipe.csv", false)]
    [InlineData("C:/fixture/Siemens.Engineering.charts.xml.zip", false)]
    [InlineData("Siemens.Engineering.CFC.xml", false)]
    public void Assembly_detection_does_not_classify_user_input_files(string name, bool expected)
    {
        Assert.Equal(expected, OptionalProductFailures.IsAssemblyLoadFailure(new FileNotFoundException("fixture", name)));
        Assert.Equal(expected, OptionalProductFailures.IsAssemblyLoadFailure(new TargetInvocationException(new FileLoadException("fixture", name))));
    }

    [Theory]
    [InlineData("18", "ExchangeCfcCharts", "", "case", "exportInstructionData")]
    [InlineData("17", "ListTestSuiteCases", "system", "case", "read")]
    [InlineData("17", "RunTestSuiteCase", "system", "case", "run")]
    [InlineData("18", "ManageTestSuiteCase", "system", "case", "createFromMasterCopy")]
    [InlineData("18", "ManageTestSuiteCase", "system", "case", "setScope")]
    [InlineData("18", "ExchangeTestSuiteCase", "system", "case", "delete")]
    [InlineData("19", "ExchangeTestSuiteCase", "system", "case", "delete")]
    [InlineData("19", "ListTestSuiteCases", "application", "testSet", "read")]
    [InlineData("19", "ExchangeTestSuiteCase", "application", "case", "importTestSets")]
    [InlineData("17", "ManageSinumerikArchive", "", "", "fAddressArchive")]
    [InlineData("21", "ExportScadaData", "", "", "")]
    [InlineData("21", "ImportSinumerikAlarmTexts", "", "", "")]
    [InlineData("21", "ManageSinumerikArchive", "", "", "")]
    [InlineData("21", "ManageSinumerikSafetyMode", "", "", "")]
    [InlineData("21", "InitializeSimotionScripting", "", "", "")]
    [InlineData("16", "InitializeSimotionScripting", "", "", "")]
    public void Missing_members_are_refused_before_dispatch(string release, string tool, string category, string kind, string action)
        => Assert.NotNull(OptionalProductCapabilities.Unsupported(release, tool, category, kind, action));

    [Theory]
    [InlineData("17", "RunTestSuiteCase", "styleGuide", "case", "run")]
    [InlineData("17", "RunTestSuiteCase", "application", "case", "run")]
    [InlineData("18", "ExchangeTestSuiteCase", "system", "case", "export")]
    [InlineData("19", "ExchangeTestSuiteCase", "system", "case", "import")]
    [InlineData("20", "ExchangeTestSuiteCase", "system", "case", "delete")]
    [InlineData("20", "RunTestSuiteCase", "application", "testSet", "run")]
    [InlineData("19", "ExchangeCfcCharts", "", "", "exportInstructionData")]
    [InlineData("16", "ManageSinumerikArchive", "", "", "retrieve")]
    [InlineData("14sp1", "InitializeSimotionScripting", "", "", "")]
    public void Present_members_remain_available(string release, string tool, string category, string kind, string action)
        => Assert.Null(OptionalProductCapabilities.Unsupported(release, tool, category, kind, action));

    [Fact]
    public void Worker_request_retains_selection_options_and_bounds()
    {
        var request = new OptionalProductRequest
        {
            Operation = "ManageTestSuiteCase", Category = "styleGuide", Action = "setScope", DryRun = true,
            Plan = new OptionalProductPlan { UpdateOption = "Add", Scope = new[] { new TestSuiteScope { Kind = "units", SoftwarePath = "PLC_1" } } },
            DevicePathJson = new[] { "Group", "NCU" }, FilesJson = new[] { "C:/fixture/alarm.ts" }, Limit = 500
        };
        var copy = JsonSerializer.Deserialize<OptionalProductRequest>(JsonSerializer.Serialize(request))!;
        Assert.Equal("units", Assert.Single(copy.Plan.Scope).Kind);
        Assert.Equal("PLC_1", Assert.Single(copy.Plan.Scope).SoftwarePath);
        Assert.Equal(request.DevicePathJson, copy.DevicePathJson);
        Assert.Equal(request.FilesJson, copy.FilesJson);
        Assert.Equal(500, copy.Limit);
        Assert.True(copy.DryRun);
    }
}
