using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class DeviceCreateRefusalTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("added")]
    [InlineData("head-renamed")]
    [InlineData("unreadable")]
    [InlineData("disposed")]
    [InlineData("timeout")]
    [InlineData("abort")]
    public void Exact_create_requires_readable_unchanged_inventory(string failure)
    {
        var engine = new PlcFoundationEngine();
        const string article = "6ES7 513-1AM03-0AB0";
        engine.AttachedPortal.HardwareCatalog.Rows.Add(new() {
            ArticleNumber = article, Version = "V1", TypeIdentifier = "OrderNumber:" + article + "/V1" });
        var project = engine.BoundProject;
        var existing = new Siemens.Engineering.HW.Device { Name = "Station", Parent = project };
        existing.DeviceItems.Add(new() { Name = "CPU" });
        project.Devices.Add(existing);
        var preview = engine.AddDeviceWithFallback(article, "V1", "PLC_2");
        project.Devices.BeforeCreate = () => {
            if (failure == "added") project.Devices.Add(new() { Name = "PLC_2", Parent = project });
            if (failure == "head-renamed") existing.DeviceItems[0].Name = "PLC_2";
            if (failure == "unreadable") engine.Bound = false;
            throw failure switch {
                "disposed" => new ObjectDisposedException("DeviceComposition"),
                "timeout" => new TimeoutException(),
                "abort" => new System.Reflection.TargetInvocationException(new EngineeringUserAbortException()),
                _ => new InvalidOperationException("CreateWithItem failed.")
            };
        };
        PlcDeviceAddResult Apply() => engine.AddDeviceWithFallback(article, "V1", "PLC_2", dryRun: false,
            confirm: true, expectedPlanHash: preview.PlanHash, expectedProjectFile: project.Path.FullName);
        if (failure == "unchanged")
            Assert.Contains("inventory unchanged", Assert.Throws<AdapterPreconditionException>(() => Apply()).Message);
        else
            Assert.True(Apply().OutcomeUnknown);
        Assert.Equal(1, project.Devices.Calls);
    }

    private sealed class EngineeringUserAbortException : Exception { }
}
