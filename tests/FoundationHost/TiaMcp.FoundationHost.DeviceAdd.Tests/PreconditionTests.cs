using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.FoundationHost.DeviceAdd.Tests;

public sealed class PreconditionTests
{
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
