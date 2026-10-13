using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OptionalEngineeringMutationAdmissionTests
{
    [Theory]
    [InlineData("plc-safety.ExecuteSafety", "ManageSafetyGlobalSettings", "update", false, true, "C:/Projects/项目1.ap21", "confirm")]
    [InlineData("plc-safety.ExecuteSafety", "ManageSafetyGlobalSettings", "update", true, false, "C:/Projects/项目1.ap21", "expectedProjectFile")]
    [InlineData("plc-safety.ExecuteSafety", "ManageSafetyGlobalSettings", "update", true, true, "C:/Projects/Other.ap21", "expectedProjectFile")]
    [InlineData("startdrive.ExecuteStartdrive", "ManageTechnologyExtensions", "install", false, true, "C:/Projects/项目1.ap21", "confirm")]
    [InlineData("startdrive.ExecuteStartdrive", "ManageTechnologyExtensions", "install", true, false, "C:/Projects/项目1.ap21", "expectedProjectFile")]
    [InlineData("startdrive.ExecuteStartdrive", "ManageTechnologyExtensions", "install", true, true, "C:/Projects/Other.ap21", "expectedProjectFile")]
    public void Portal_write_refusals_precede_product_calls(string module, string operation, string action, bool confirm, bool attached, string actual, string parameter)
    {
        int projectCalls = 0, nativeCalls = 0;
        var error = Assert.Throws<ArgumentException>(() => {
            OptionalEngineeringMutations.ValidateWrite(module, operation, action, confirm, "C:/Projects/项目1.ap21",
                () => (attached, actual), name => throw new ArgumentException("Rejected.", name), () => projectCalls++);
            nativeCalls++;
        });
        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(0, projectCalls);
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData("startdrive.ExecuteDcc", "ManageDccChart", "create")]
    [InlineData("startdrive.ExecuteStartdrive", "ManageTechnologyExtensions", "activate")]
    [InlineData("plc-safety.ExecuteSafety", "ManagePlcSafety", "setPassword")]
    [InlineData("libraries.ExecuteLibrary", "ManageSafetyGlobalSettings", "update")]
    public void Project_writes_and_wrong_module_requests_retain_project_identity_validation(string module, string operation, string action)
    {
        int projectCalls = 0, bindingReads = 0, nativeCalls = 0;
        Assert.Throws<ArgumentException>(() => {
            OptionalEngineeringMutations.ValidateWrite(module, operation, action, true, "C:/Projects/Other.ap21",
                () => { bindingReads++; return (true, "C:/Projects/项目1.ap21"); }, _ => Assert.Fail("Wrong portal route."),
                () => { projectCalls++; TiaMcp.Adapters.MutationIdentityPolicy.ValidateTarget(false, true, "C:/Projects/Other.ap21",
                    module, "21", "", "", "", expected => TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(expected, "C:/Projects/项目1.ap21")); });
            nativeCalls++;
        });
        Assert.Equal(1, projectCalls);
        Assert.Equal(0, bindingReads);
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData("plc-safety.ExecuteSafety", "ManageSafetyGlobalSettings", "update")]
    [InlineData("startdrive.ExecuteStartdrive", "ManageTechnologyExtensions", "uninstall")]
    public void Confirmed_portal_writes_use_the_same_attached_binding(string module, string operation, string action)
    {
        int projectCalls = 0, reads = 0;
        OptionalEngineeringMutations.ValidateWrite(module, operation, action, true, "C:/Projects/项目1.ap21",
            () => { reads++; return (true, "c:/projects/项目1.ap21"); }, _ => Assert.Fail("Valid binding refused."), () => projectCalls++);
        Assert.Equal(1, reads);
        Assert.Equal(0, projectCalls);
    }
}
