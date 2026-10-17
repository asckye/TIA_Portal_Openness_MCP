using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class ScaffoldReleaseAdmissionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (string kind in new[] { "connect", "createProject", "save", "addDevice", "sclImport", "sclGenerate",
                "compile", "hmiInfo", "lad", "hmiConnect", "hmiScreen", "hmiDesign", "hmiTag" })
                yield return new object[] { release, kind };
    }

    [Theory, MemberData(nameof(Cases))]
    public void Registration_and_primitive_admission_follow_the_official_API_matrix(string release, string kind)
    {
        bool expected = kind == "lad" ? release is "20" or "21"
            : kind is "hmiConnect" or "hmiScreen" or "hmiDesign" or "hmiTag" ? release is "19" or "20" or "21"
            : true;
        Assert.True(PortedFamilies.Available(release, "BuildProjectScaffold"));
        Assert.Equal(expected, ProjectScaffoldCapabilities.Supports(release, kind));
        var fake = new ScaffoldWorkerFake(release);
        if (expected)
        {
            ProjectScaffoldCapabilities.Require(release, kind);
            if (kind == "hmiInfo")
            {
                fake.Execute(WorkerProjectScaffoldTests.Request(kind, "unused"));
                Assert.Equal("hmi-generic.ExecuteHmi", Assert.Single(fake.Calls).Operation);
            }
        }
        else
        {
            var error = Assert.Throws<NotSupportedException>(() => fake.Execute(WorkerProjectScaffoldTests.Request(kind, "unused")));
            Assert.Equal(ProjectScaffoldCapabilities.Refusal(release, kind), error.Message);
            Assert.Empty(fake.Calls);
        }
    }
}
