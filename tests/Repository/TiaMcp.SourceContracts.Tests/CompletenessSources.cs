using TiaMcp.BuildCommon;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class CompletenessSources
{
    private static string Read(string path) => SourceFamily.Read(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path));

    [Fact]
    public void Plant_read_and_transfer_areas_compute_data_complete()
    {
        var plant = EngineSources.BlockAfter(Read("src/Adapters/Native/Hmi/UnifiedObjectServicesAdapter.cs"), "public HmiMessage ReadUnifiedPlantObject(");
        Assert.Contains("offset == 0 && rows.Count == items.Length", plant);
        Assert.Contains("rows.All", plant);
        Assert.Contains("Field(\"dataComplete\")", plant);
        var transfer = Read("src/Adapters/Native/Hardware/HardwareNetwork.TransferAreas.cs");
        var areas = EngineSources.BlockAfter(transfer, "=> RunHardwareAddressStep(\"ListTransferAreas\", meta =>");
        Assert.Contains("HardwarePageComplete(rows.ToArray(), offset, (object[])meta[\"records\"]!)", areas);
        var helper = Read("src/Adapters/Native/Hardware/HardwareNetworkPrimitives.cs");
        Assert.Contains("offset == 0 && page.Length == rows.Length", helper);
        Assert.Contains("pair.Key == \"failures\"", helper);
    }

    [Fact]
    public void Button_event_describe_cuts_to_the_cap()
    {
        var source = Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.EventScripts.cs");
        Assert.Contains("Members = members.Take(MemberListCap.Effective(maxMembers)).ToList()", source);
        Assert.Contains("(\"totalMembers\", members.Count)", source);
        Assert.Contains("(\"truncated\", members.Count > MemberListCap.Effective(maxMembers))", source);
    }
}
