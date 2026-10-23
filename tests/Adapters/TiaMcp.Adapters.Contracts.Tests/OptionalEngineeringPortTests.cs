using TiaOpenness.Shared;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Optional;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OptionalEngineeringPortTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };

    public static IEnumerable<object[]> RegistrationCases()
    {
        var firstApi = new Dictionary<string, int> {
            ["GenerateSivarc"] = 15, ["GetSivarcRuleTree"] = 15, ["ListSivarcBlockDefinitions"] = 17,
            ["ListSivarcRules"] = 15, ["ManageSivarcBlockDefinition"] = 17, ["ManageSivarcRule"] = 15,
            ["ManageSivarcRuleContainer"] = 18, ["ManageSivarcScreenLayout"] = 21, ["ManageSivarcTableRule"] = 18,
            ["ResolveSivarcExpression"] = 20, ["UpgradeSivarcDefinitions"] = 20,
            ["ExportSafetyPrintout"] = 17, ["GetSafetyBlockSignatures"] = 17, ["ManagePlcSafety"] = 17,
            ["ManageSafetyGlobalSettings"] = 16, ["ListSafetyActivationTests"] = 21, ["ManageSafetyActivationTest"] = 21,
            ["ManageSafetyActivationTestGroup"] = 21, ["ManageSafetyFunction"] = 21, ["ManageSafetyFunctionCondition"] = 21,
            ["GetDccObject"] = 16, ["GetDriveParameters"] = 14, ["GetOnlineDriveParameters"] = 15, ["ListDccCharts"] = 16,
            ["ListDriveObjects"] = 14, ["ManageDcbLibraries"] = 16, ["ManageDccBlock"] = 18, ["ManageDccChart"] = 16,
            ["ManageDccChartInterface"] = 18, ["ManageDccChartPartition"] = 18, ["ManageDccPin"] = 18,
            ["ManageDriveFunctions"] = 15, ["ManageDriveHardwareModule"] = 19, ["ManageDriveSafetyAcceptanceTest"] = 21,
            ["ManageDriveSecurity"] = 19, ["ManageDriveTelegrams"] = 14, ["ManageOnlineDriveFunctions"] = 15,
            ["ManageStartdriveParameter"] = 14, ["ManageTechnologyExtensions"] = 18
        };
        foreach (var release in Releases)
        {
            int major = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var pair in firstApi) yield return new object[] { release, pair.Key, major >= pair.Value };
        }
    }

    [Theory, MemberData(nameof(RegistrationCases))]
    public void Catalog_follows_each_tools_official_entry_api(string release, string tool, bool available)
    {
        Assert.Equal(available, PortedFamilies.Available(release, tool));
        Assert.Equal(!available, PortedFamilies.Withheld(release).Contains(tool));
    }

    [Fact]
    public void V14_predecessor_api_refuses_missing_actions_before_dispatch()
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveTelegrams", Action = "read" };
        Assert.Null(OptionalEngineeringCapabilities.Unsupported("14sp1", request));
        request.Action = "changeNumber";
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported("14sp1", request));
        request.Action = "erase"; request.TelegramType = "SupplementaryTelegram";
        Assert.Contains("PilotAccess", OptionalEngineeringCapabilities.Unsupported("14sp1", request));
        request.TelegramType = "AdditionalTelegram";
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported("14sp1", request));
        request.Operation = "GetDriveParameters"; request.Source = "write"; request.DriveObjectIndex = 1;
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported("14sp1", request));
        request.DriveObjectIndex = 0;
        Assert.Null(OptionalEngineeringCapabilities.Unsupported("14sp1", request));
    }

    [Fact]
    public void Optional_assembly_refusal_does_not_enter_the_native_operation()
    {
        int calls = 0;
        var reply = OptionalAssemblyAdmission.Run("21", "TiaMcp.Test.OptionalProduct.DoesNotExist", () => {
            calls++;
            return new HardwareAddressingReply();
        });
        Assert.Equal(0, calls);
        Assert.Equal("NotSupportedOnVersion", reply.Meta["status"]);
        Assert.Equal(false, reply.Meta["mayHaveChanged"]);
        Assert.False(reply.RequiresSessionReset);
        Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["rejectionCode"]);
        Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["v4Rejection"]);
        Assert.Equal(true, reply.Meta["operationNotStarted"]);
    }

    [Theory]
    [InlineData("15.1", "ManageSivarcRule", "update", false)]
    [InlineData("15.1", "ManageSivarcRule", "delete", false)]
    [InlineData("15.1", "ManageSivarcRule", "create", true)]
    [InlineData("16", "ManageDccChart", "import", false)]
    [InlineData("17", "ManageDccChart", "import", false)]
    [InlineData("16", "ManageDccChart", "create", true)]
    [InlineData("17", "ManageDccChart", "create", true)]
    [InlineData("15.1", "GetOnlineDriveParameters", "read", false)]
    [InlineData("16", "GetOnlineDriveParameters", "read", false)]
    [InlineData("17", "GetOnlineDriveParameters", "read", false)]
    [InlineData("18", "GetOnlineDriveParameters", "read", false)]
    [InlineData("19", "GetOnlineDriveParameters", "read", false)]
    public void Partial_action_admission_matches_exact_sdk_members(string release, string tool, string action, bool refused)
    {
        var request = new OptionalEngineeringRequest { Operation = tool, Action = action, SequenceIndex = -1 };
        Assert.Equal(refused, OptionalEngineeringCapabilities.Unsupported(release, request) != null);
    }

    [Theory]
    [InlineData("GetDriveParameters", "read", false)]
    [InlineData("ManageDriveTelegrams", "check", false)]
    [InlineData("ManageDccChart", "readSequence", false)]
    [InlineData("ManageDriveFunctions", "readMotorConfiguration", false)]
    [InlineData("ManageTechnologyExtensions", "readPackages", false)]
    [InlineData("ManageStartdriveParameter", "write", true)]
    [InlineData("ManageDccPin", "connect", true)]
    [InlineData("ManageOnlineDriveFunctions", "performFactoryReset", true)]
    [InlineData("GenerateSivarc", "", true)]
    public void Worker_mutation_admission_uses_the_actual_action(string operation, string action, bool writing)
    {
        var request = new OptionalEngineeringRequest { Operation = operation, Action = action, DryRun = false, IsWrite = !writing };
        Assert.Equal(writing, OptionalEngineeringMutations.Writes(request));
    }

    [Fact]
    public void Old_drive_read_view_and_array_publish_are_refused_before_dispatch()
    {
        var parameters = new OptionalEngineeringRequest { Operation = "GetDriveParameters", Source = "read" };
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported("19", parameters));
        parameters.Source = "write";
        Assert.Null(OptionalEngineeringCapabilities.Unsupported("19", parameters));
        var pin = new OptionalEngineeringRequest { Operation = "ManageDccPin", Action = "publish", ArrayIndex = 0 };
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported("19", pin));
        Assert.Null(OptionalEngineeringCapabilities.Unsupported("20", pin));
    }

    [Theory]
    [InlineData("15.1", "ListDriveObjects", true, false, false, false)]
    [InlineData("15.1", "ListDriveObjects", false, true, false, false)]
    [InlineData("16", "ListDccCharts", false, false, true, false)]
    [InlineData("17", "ListDccCharts", false, false, false, true)]
    public void Optional_drive_includes_are_absent_and_unsupported_dcc_reads_refuse_before_dispatch(string release, string operation, bool extensions, bool dcc, bool blocks, bool libraries)
    {
        var request = new OptionalEngineeringRequest { Operation = operation, IncludeTechnologyExtensions = extensions,
            IncludeDcc = dcc, IncludeBlocks = blocks, IncludeLibraries = libraries };
        if (operation == "ListDriveObjects") Assert.Null(OptionalEngineeringCapabilities.Unsupported(release, request));
        else Assert.NotNull(OptionalEngineeringCapabilities.Unsupported(release, request));
        Assert.Null(OptionalEngineeringCapabilities.Unsupported("18", request));
    }

    [Theory]
    [InlineData("deleteRuntimeGroup")]
    [InlineData("generateGlobalFIOStatusBlock")]
    [InlineData("cleanSystemGeneratedObjects")]
    [InlineData("generateBaseId")]
    public void Safety_program_edits_retain_the_explicit_confirmation(string action)
    {
        string group = action is "deleteRuntimeGroup" or "generateGlobalFIOStatusBlock" ? "F_Group" : "";
        Assert.Throws<ArgumentException>(() => SafetyPolicy.ValidateRequest(action, group, "", false, false));
        Assert.True(SafetyPolicy.ValidateRequest(action, group, "", true, false));
        Assert.False(SafetyPolicy.ValidateRequest(action, group, "", false, true));
    }
}
