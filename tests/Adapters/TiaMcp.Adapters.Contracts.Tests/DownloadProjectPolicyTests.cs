using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class DownloadProjectPolicyTests
{
    [Theory]
    [InlineData(true, true, "project-saved")]
    [InlineData(true, false, "project-saved")]
    [InlineData(true, null, "project-saved")]
    [InlineData(false, false, "plc-compiled")]
    [InlineData(false, true, null)]
    [InlineData(false, null, null)]
    public void Download_requires_saved_project_and_known_consistency(bool modified, bool? consistent, string? condition)
    {
        var refusal = OnlineTransitionPolicy.DownloadProjectRefusal(modified, consistent);
        Assert.Equal(condition, refusal?.Condition);
        if (condition == "project-saved") Assert.Contains("SaveProject", refusal?.Message);
        if (condition == "plc-compiled")
        {
            Assert.Contains("CompilePlcSoftware", refusal?.Message);
            Assert.Contains("CompileDevice", refusal?.Message);
        }
    }
}
