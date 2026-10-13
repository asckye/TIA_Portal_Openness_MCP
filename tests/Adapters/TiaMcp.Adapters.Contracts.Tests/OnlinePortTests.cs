using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OnlinePortTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    private static readonly Dictionary<string, int> FirstRelease = new(StringComparer.Ordinal)
    {
        ["GetOnlineState"] = 0, ["ConnectOnlinePlc"] = 0, ["DisconnectOnlinePlc"] = 0,
        ["DisconnectOnlinePlcs"] = 0, ["CompareSoftwareToOnline"] = 0, ["ListTransferRoutes"] = 0,
        ["CheckDownloadReadiness"] = 1, ["DownloadPlc"] = 1, ["UploadStationFromPlc"] = 1,
        ["ManagePlcDataBlockSnapshot"] = 1, ["GetPlcBlockFingerprints"] = 2,
        ["GeneratePlcLoadableFile"] = 4, ["DownloadPlcToFolder"] = 5,
        ["ScanAccessibleDevices"] = 5, ["UploadDeviceParameters"] = 7
    };

    public static IEnumerable<object[]> RegistrationCases()
    {
        foreach (var entry in FirstRelease)
            for (int index = 0; index < Releases.Length; index++)
                yield return new object[] { entry.Key, Releases[index], index >= entry.Value };
    }

    [Theory, MemberData(nameof(RegistrationCases))]
    public void Registration_follows_the_release_API(string tool, string release, bool expected)
        => Assert.Equal(expected, PortedFamilies.Available(release, tool));

    [Theory]
    [InlineData("14sp1", "primary", true)]
    [InlineData("15.1", "primary", false)]
    [InlineData("20", "backup", false)]
    [InlineData("21", "backup", false)]
    public void Redundant_online_target_is_admitted_only_with_its_provider(string release, string target, bool refused)
        => Assert.Equal(refused, OnlineCapabilities.Unsupported(release,
            new OnlineRequest { Operation = "ConnectOnlinePlc", RhTarget = target }) != null);

    [Theory]
    [InlineData("15.1", true)]
    [InlineData("16", true)]
    [InlineData("17", false)]
    [InlineData("20", false)]
    public void Connection_password_requires_the_legitimation_event(string release, bool refused)
        => Assert.Equal(refused, OnlineCapabilities.Unsupported(release,
            new OnlineRequest { Operation = "ConnectOnlinePlc", Password = "fixture-password" }) != null);

    [Theory]
    [InlineData("18", true)]
    [InlineData("19", false)]
    [InlineData("20", false)]
    [InlineData("21", false)]
    public void Online_user_credentials_follow_the_authentication_API(string release, bool refused)
        => Assert.Equal(refused, OnlineCapabilities.Unsupported(release,
            new OnlineRequest { Operation = "ConnectOnlinePlc", UserName = "fixture", Password = "fixture-password" }) != null);

    [Theory]
    [InlineData("15.1", "read", false)]
    [InlineData("15.1", "exportSnapshot", false)]
    [InlineData("20", "read", false)]
    [InlineData("20", "exportSnapshot", false)]
    [InlineData("20", "createSnapshot", true)]
    [InlineData("20", "loadSnapshotAsActualValues", true)]
    [InlineData("20", "loadStartValuesAsActualValues", true)]
    [InlineData("21", "createSnapshot", false)]
    [InlineData("21", "loadSnapshotAsActualValues", false)]
    public void Snapshot_actions_distinguish_InterfaceSnapshot_from_ValueService(string release, string action, bool refused)
        => Assert.Equal(refused, OnlineCapabilities.Unsupported(release,
            new OnlineRequest { Operation = "ManagePlcDataBlockSnapshot", Action = action }) != null);

    [Fact]
    public void Secure_password_is_read_only_and_credential_errors_do_not_echo_it()
    {
        const string secret = "fixture-sensitive-input";
        using var password = EngineeringCredentialRules.ToSecureString(secret);
        Assert.True(password.IsReadOnly());
        Assert.Equal(secret.Length, password.Length);
        var error = Assert.Throws<ArgumentException>(() => EngineeringCredentialRules.ValidateOnlineCredentials("", secret, "ProjectUser"));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("units")]
    public void Both_software_unit_spellings_are_admitted_on_V18(string kind)
        => Assert.Null(OnlineCapabilities.Unsupported("18",
            new OnlineRequest { Operation = "GeneratePlcLoadableFile", ObjectKind = kind }));

    [Fact]
    public void Native_reply_carries_only_managed_evidence()
    {
        var reply = new OnlineReply
        {
            Value = new OnlineDownloadResult { State = "Warning", ErrorCount = 0, WarningCount = 1,
                Warnings = new[] { "fixture-warning" }, Meta = new() { ["mayHaveChanged"] = true } },
            MayHaveChanged = true
        };
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(reply));
        Assert.Equal("Warning", wire.RootElement.GetProperty("Value").GetProperty("State").GetString());
        Assert.Equal("fixture-warning", wire.RootElement.GetProperty("Value").GetProperty("Warnings")[0].GetString());
        Assert.True(wire.RootElement.GetProperty("MayHaveChanged").GetBoolean());
        Assert.False(wire.RootElement.TryGetProperty("Password", out _));
    }

    [Theory]
    [InlineData("DownloadPlc", false, true, false, false, null, true)]
    [InlineData("DownloadPlc", false, true, true, false, "Error", false)]
    [InlineData("DownloadPlc", false, false, false, false, null, false)]
    [InlineData("UploadStationFromPlc", false, true, true, false, null, true)]
    [InlineData("GeneratePlcLoadableFile", false, true, false, true, null, true)]
    [InlineData("GetPlcBlockFingerprints", false, true, false, false, null, false)]
    [InlineData("DownloadPlc", true, true, true, false, "Success", false)]
    public void Completed_failures_and_unknown_writes_have_distinct_session_effects(string tool, bool success,
        bool nativeIssued, bool changed, bool files, string? state, bool reset)
        => Assert.Equal(reset, OnlineCompletionPolicy.RequiresSessionReset(
            new OnlineRequest { Operation = tool, DryRun = false }, success, nativeIssued, changed, files, false, state));
}
