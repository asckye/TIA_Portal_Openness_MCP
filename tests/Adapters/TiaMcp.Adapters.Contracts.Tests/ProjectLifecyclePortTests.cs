using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class ProjectLifecyclePortTests
{
    public static IEnumerable<object[]> Availability()
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            foreach (var tool in new[] { "ArchiveSavedProject", "BuildProjectScaffold", "ConnectIsolatedPortal",
                "EnsureOpennessUserGroup", "GetPortalInfo", "ManageMultiuserSession", "RetrieveProjectArchive",
                "SaveProjectCopy", "ValidateAutomationContext" })
            {
                bool available = tool == "ManageMultiuserSession" ? version >= 17
                    : tool is "ArchiveSavedProject" or "RetrieveProjectArchive" or "SaveProjectCopy" ? version >= 15 : true;
                yield return new object[] { release, tool, available };
            }
        }
    }

    [Theory, MemberData(nameof(Availability))]
    public void RegistrationMatchesProjectAndMultiuserApiAvailability(string release, string tool, bool available)
        => Assert.Equal(available, PortedFamilies.Available(release, tool));
}
