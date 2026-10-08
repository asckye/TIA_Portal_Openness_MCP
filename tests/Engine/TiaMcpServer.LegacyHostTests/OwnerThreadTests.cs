using System.Threading;
using TiaMcp.PlcFoundation;
using Xunit;

public sealed class OwnerThreadTests
{
    [Theory]
    [InlineData("14sp1", ApartmentState.STA)] [InlineData("15.1", ApartmentState.STA)]
    [InlineData("16", ApartmentState.STA)] [InlineData("17", ApartmentState.STA)]
    [InlineData("18", ApartmentState.STA)] [InlineData("19", ApartmentState.STA)]
    [InlineData("20", ApartmentState.MTA)] [InlineData("21", ApartmentState.MTA)]
    public void Each_release_keeps_its_owner_and_apartment(string release, ApartmentState expected)
    {
        Assert.Equal(expected, PlcLifecyclePolicy.OwnerApartment(release));
        PlcLifecyclePolicy.RequireOwner(release, Environment.CurrentManagedThreadId);
        Assert.Throws<InvalidOperationException>(() => PlcLifecyclePolicy.RequireOwner(release, -1));
    }
}
