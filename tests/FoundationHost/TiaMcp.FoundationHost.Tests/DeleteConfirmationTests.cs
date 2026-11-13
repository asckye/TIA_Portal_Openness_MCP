extern alias enginehost;
using ModelContextProtocol.Protocol;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

public sealed class DeleteConfirmationTests
{
    public static IEnumerable<object[]> Tools => from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from tool in new[] { "DeletePlcTagTable", "DeletePlcBlock", "DeletePlcType" }
        select new object[] { release, tool };
    public static IEnumerable<object[]> Refusals => from row in Tools
        from scenario in new[] { "unconfirmed", "mismatch", "blank", "relative", "drive-relative", "unbound" }
        select new object[] { row[0], row[1], scenario };

    private static CallToolResult Delete(Host.PlcOrganisationTools tools, string tool, bool dryRun, bool confirm, string project) => tool switch {
        "DeletePlcTagTable" => tools.DeletePlcTagTableV4("PLC_A", "Table", dryRun, confirm: confirm, expectedProjectFile: project),
        "DeletePlcBlock" => tools.DeletePlcBlockV4("PLC_A", "Block", dryRun, confirm: confirm, expectedProjectFile: project),
        _ => tools.DeletePlcTypeV4("PLC_A", "Type", dryRun, confirm: confirm, expectedProjectFile: project)
    };

    [Theory, MemberData(nameof(Refusals))]
    public void Delete_requires_confirmation_and_project_identity(string release, string tool, string scenario)
    {
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(new HardwareAddressingAdmissionTests.UnboundWorker(), release);
        int calls = 0;
        var service = new Services.PlcOrganisationPortService((_, _) => {
            calls++; throw new InvalidOperationException("Admission must reject before worker dispatch.");
        }, () => scenario != "unbound", () => "C:/Project/Example.ap21");
        string project = scenario switch {
            "mismatch" => "C:/Project/Other.ap21", "blank" => "", "relative" => "Example.ap21",
            "drive-relative" => "C:Example.ap21", _ => "C:/Project/Example.ap21"
        };
        string code = scenario switch {
            "unconfirmed" => "CONFIRMATION_REQUIRED", "mismatch" => "IDENTITY_MISMATCH", "unbound" => "PROJECT_NOT_BOUND",
            _ => "INVALID_ARGUMENT"
        };
        var body = Delete(new Host.PlcOrganisationTools(service), tool, false, scenario != "unconfirmed", project).StructuredContent!;
        Assert.False((bool)body["ok"]!);
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        if (code == "INVALID_ARGUMENT") Assert.Equal("/expectedProjectFile", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal(0, calls);
    }

    [Theory, MemberData(nameof(Tools))]
    public void Confirmed_delete_with_bound_project_reaches_worker(string release, string tool)
    {
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(new HardwareAddressingAdmissionTests.UnboundWorker(), release);
        int calls = 0;
        var service = new Services.PlcOrganisationPortService((_, arguments) => {
            calls++;
            Assert.True((bool)arguments["confirm"]!);
            Assert.Equal("C:/Project/Example.ap21", (string?)arguments["expectedProjectFile"]);
            throw new InvalidOperationException("Synthetic worker boundary.");
        }, () => true, () => "C:/Project/Example.ap21");
        Delete(new Host.PlcOrganisationTools(service), tool, false, true, "C:/Project/Example.ap21");
        Assert.Equal(1, calls);
    }

    [Theory, MemberData(nameof(Tools))]
    public void Preview_keeps_its_unconfirmed_worker_path(string release, string tool)
    {
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(new HardwareAddressingAdmissionTests.UnboundWorker(), release);
        int calls = 0;
        var service = new Services.PlcOrganisationPortService((_, arguments) => {
            calls++;
            Assert.True((bool)arguments["dryRun"]!);
            Assert.False(arguments.ContainsKey("confirm"));
            throw new InvalidOperationException("Synthetic preview boundary.");
        }, () => true, () => "C:/Project/Example.ap21");
        Delete(new Host.PlcOrganisationTools(service), tool, true, false, "");
        Assert.Equal(1, calls);
    }
}
