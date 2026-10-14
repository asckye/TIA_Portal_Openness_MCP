using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.FoundationHost.DeviceAdd.Tests;

public sealed class PreconditionTests
{
    public static IEnumerable<object[]> IdentityChanges()
        => new[] { "18", "19", "20", "21" }.SelectMany(release => new[] { "pid", "start", "path", "epoch" }
            .Select(field => new object[] { release, field }));

    [Theory, MemberData(nameof(IdentityChanges))]
    public void Device_creation_refuses_changed_identity_before_catalog_or_creation(string release, string field)
    {
        var engine = new PlcFoundationEngine { ReleaseKey = release };
        var original = engine.SharedHardwareCandidateIdentity!;
        int reads = 0;
        engine.SharedHardwareCandidateIdentity = path => {
            var identity = original(path);
            if (++reads > 1)
            {
                if (field == "pid") identity.ProcessId++;
                if (field == "start") identity.ProcessStartUtc = identity.ProcessStartUtc!.Value.AddTicks(1);
                if (field == "path") identity.ProjectFile = @"C:\Other\Other.ap19";
                if (field == "epoch") identity.BindingEpoch++;
            }
            return identity;
        };
        var error = Assert.Throws<AdapterPreconditionException>(() => engine.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V1", "PLC_2"));
        Assert.Equal("device-create-preflight", error.ParamName);
        Assert.False(error.IsArgument);
        Assert.Contains("Exact device-creation binding changed", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.Equal(0, engine.AttachedPortal.HardwareCatalog.Calls);
        Assert.Equal(0, engine.BoundProject.Devices.Calls);
    }

    [Theory]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void A_new_binding_epoch_invalidates_the_previous_device_preview(string release)
    {
        var engine = new PlcFoundationEngine { ReleaseKey = release };
        long epoch = 1;
        var original = engine.SharedHardwareCandidateIdentity!;
        engine.SharedHardwareCandidateIdentity = path => { var identity = original(path); identity.BindingEpoch = epoch; return identity; };
        engine.AttachedPortal.HardwareCatalog.Rows.Add(new PlcHardwareCatalogCandidate {
            TypeIdentifier = "OrderNumber:6ES7 513-1AM03-0AB0/V1", ArticleNumber = "6ES7 513-1AM03-0AB0", Version = "V1" });
        var preview = engine.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V1", "PLC_2");
        epoch++;
        var error = Assert.Throws<AdapterPreconditionException>(() => engine.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V1", "PLC_2",
            dryRun: false, expectedPlanHash: preview.PlanHash, confirm: true, expectedProjectFile: engine.BoundProject.Path.FullName));
        Assert.Equal("expectedPlanHash", error.ParamName);
        Assert.Equal(0, engine.BoundProject.Devices.Calls);
    }

    [Theory]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Ordinary_attached_and_local_sessions_share_device_creation_admission(string release)
    {
        foreach (bool local in new[] { false, true })
        {
            var engine = new PlcFoundationEngine { ReleaseKey = release };
            engine.lifecycle.IsLocalSession = local;
            engine.AttachedPortal.HardwareCatalog.Rows.Add(new PlcHardwareCatalogCandidate {
                TypeIdentifier = "OrderNumber:6ES7 513-1AM03-0AB0/V1", ArticleNumber = "6ES7 513-1AM03-0AB0", Version = "V1" });
            var preview = engine.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V1", "PLC_2");
            Assert.False(preview.Attempted);
            Assert.Equal(0, engine.BoundProject.Devices.Calls);
            var result = engine.AddDeviceWithFallback("6ES7 513-1AM03-0AB0", "V1", "PLC_2", dryRun: false,
                expectedPlanHash: preview.PlanHash, confirm: true, expectedProjectFile: engine.BoundProject.Path.FullName);
            Assert.True(result.Executed);
            Assert.Equal(1, engine.BoundProject.Devices.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_preflight_rejections_keep_their_identity_message_and_argument_kind(bool argument)
    {
        var rejection = new AdapterPreconditionException("Preserved rejection.", "deviceName", argument);
        var request = new PlcDeviceAddRequest
        {
            Release = "19", Project = @"C:\Test\Test.ap19", RootIdentity = "root", ProcessId = 42,
            PreferredMlfb = "6ES7 513-1AM03-0AB0", PreferredVersion = "V1", Name = "PLC_2",
        };
        int catalogs = 0;
        int creates = 0;
        var actual = Assert.Throws<AdapterPreconditionException>(() => PlcDeviceAddPolicy.Run(request,
            () => { catalogs++; return []; }, () => [], () => throw rejection,
            (_, _) => { creates++; return new PlcDeviceAddItem(); }));
        Assert.Same(rejection, actual);
        Assert.Equal(rejection.Message, actual.Message);
        Assert.Equal(argument, actual.IsArgument);
        Assert.Equal(0, catalogs);
        Assert.Equal(0, creates);
    }
}
