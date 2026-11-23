using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OnlinePortTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    private static readonly Dictionary<string, int> FirstRelease = new(StringComparer.Ordinal)
    {
        ["GetOnlineState"] = 0, ["ConnectOnlinePlc"] = 1, ["DisconnectOnlinePlc"] = 0,
        ["DisconnectOnlinePlcs"] = 0, ["CompareSoftwareToOnline"] = 0, ["ListTransferRoutes"] = 0,
        ["CheckDownloadReadiness"] = 1, ["DownloadPlc"] = 1, ["UploadStationFromPlc"] = 1,
        ["ManagePlcDataBlockSnapshot"] = 1, ["GetPlcBlockFingerprints"] = 2,
        ["GeneratePlcLoadableFile"] = 4, ["DownloadPlcToFolder"] = 5,
        ["ScanAccessibleDevices"] = 5, ["UploadDeviceParameters"] = 7
    };

    [Fact]
    public void Upload_uses_the_selected_PLC_route_when_the_station_provider_has_no_target()
    {
        const string target = "192.168.0.1";
        const string listing = """
            {"name":"Siemens PLCSIM Virtual Ethernet Adapter","number":1,"addresses":[],
             "subnets":[{"name":"MCP_RC_PN","addresses":[{"name":"192.168.0.1","address":"192.168.0.1"}],"gateways":[]}],
             "targetInterfaces":[{"name":"1 X1","addresses":[]}]}
            """;
        using var pc = JsonDocument.Parse(listing);
        var plcTargets = pc.RootElement.GetProperty("subnets")[0].GetProperty("addresses").EnumerateArray()
            .Select(address => address.GetProperty("address").GetString()!);
        var match = StationUploadRoute.Resolve(target, Array.Empty<string>(), value => value, plcTargets);
        Assert.Null(match.Address);
        Assert.True(match.FromPlc);
        Assert.Equal(target, match.KnownAddresses);
        var missing = StationUploadRoute.Resolve("192.168.0.2", Array.Empty<string>(), value => value, plcTargets);
        Assert.False(missing.FromPlc);
        Assert.Equal(target, missing.KnownAddresses);
        var direct = StationUploadRoute.Resolve(target, new[] { target }, value => value, plcTargets);
        Assert.Equal(target, direct.Address);
        Assert.False(direct.FromPlc);
        var both = StationUploadRoute.Resolve("192.168.0.2", new[] { "192.168.0.2" }, value => value, plcTargets);
        Assert.Equal("192.168.0.2", both.Address);
        Assert.Equal("192.168.0.1, 192.168.0.2", both.KnownAddresses);
    }

    private sealed class FingerprintDataProvider : Siemens.Engineering.IEngineeringService { }
    private sealed class LoadableProvider : Siemens.Engineering.IEngineeringService { }

    [Theory]
    [InlineData("FingerprintDataProvider")]
    [InlineData("LoadableProvider")]
    public void Absent_required_service_reports_each_actual_owner_and_the_cpu(string service)
    {
        var engine = new PlcFoundationEngine("21");
        engine.ServiceCpu.Parent = new Siemens.Engineering.HW.DeviceItem { Name = "Rack" };
        engine.ServiceCpu.TypeIdentifier = "OrderNumber:CPU/V3.1";
        var result = engine.RunHmiStep(service, _ =>
        {
            if (service == "LoadableProvider") engine.ResolveRequiredService<LoadableProvider>("PLC_1");
            else engine.ResolveRequiredService<FingerprintDataProvider>("PLC_1");
            return "unexpected";
        }, requiresProject: false);
        Assert.False(result.OutcomeUnknown);
        Assert.False(result.MayHaveChanged);
        Assert.Equal("PreconditionFailed", result.Meta["status"]);
        Assert.Equal("service-not-provided", result.Meta["precondition"]);
        Assert.Equal(service, result.Meta["service"]);
        Assert.Equal(new[] { "PlcSoftware PLC_1", "DeviceItem PLC_1", "DeviceItem Rack", "Device Station" },
            Assert.IsAssignableFrom<IEnumerable<string>>(result.Meta["searched"]));
        Assert.Equal("OrderNumber:CPU/V3.1", result.Meta["typeIdentifier"]);
        Assert.Equal("V3.1", result.Meta["firmware"]);
        Assert.Contains("official " + service + " API exists on this release", (string?)result.Meta["refusal"]);
        var provided = new LoadableProvider();
        engine.ServiceDevice.Services[typeof(LoadableProvider)] = provided;
        Assert.Same(provided, engine.ResolveRequiredService<LoadableProvider>("PLC_1"));
    }

    [Fact]
    public void Service_diagnostics_use_the_cpu_attributes_when_the_identifier_has_no_version()
    {
        var engine = new PlcFoundationEngine("21");
        engine.ServiceCpu.TypeIdentifier = "System:Device";
        engine.ServiceCpu.AttributeReader = name => name == "FirmwareVersion" ? "V3.1" : "6ES7 513-1AL02-0AB0";
        var error = Assert.Throws<AdapterPreconditionException>(() => engine.ResolveRequiredService<LoadableProvider>("PLC_1"));
        var evidence = Assert.IsType<Dictionary<string, object?>>(error.Data["nativeResultEvidence"]);
        Assert.Equal("System:Device", evidence["typeIdentifier"]);
        Assert.Equal("6ES7 513-1AL02-0AB0", evidence["orderNumber"]);
        Assert.Equal("V3.1", evidence["firmware"]);
        Assert.Contains("6ES7 513-1AL02-0AB0", error.Message);
        Assert.Contains("firmware V3.1", error.Message);
    }

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

    public static IEnumerable<object[]> SnapshotActionCases =>
        from release in Releases
        from action in new[] { "read", "exportSnapshot", "createSnapshot", "loadSnapshotAsActualValues", "loadStartValuesAsActualValues" }
        select new object[] { release, action,
            release == "14sp1" || release != "21" && action is not ("read" or "exportSnapshot") };

    [Theory, MemberData(nameof(SnapshotActionCases))]
    public void Every_snapshot_action_follows_its_release_API(string release, string action, bool refused)
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
    [InlineData("14sp1", true)]
    [InlineData("15.1", true)]
    [InlineData("16", true)]
    [InlineData("17", false)]
    [InlineData("20", false)]
    [InlineData("21", false)]
    public void Software_only_changes_download_needs_the_V17_option(string release, bool refused)
        => Assert.Equal(refused, OnlineCapabilities.Unsupported(release,
            new OnlineRequest { Operation = "DownloadPlc", DownloadScope = "softwareOnlyChanges" }) != null);

    [Theory]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("21")]
    public void Full_software_download_scope_is_admitted_with_the_download_provider(string release)
        => Assert.Null(OnlineCapabilities.Unsupported(release, new OnlineRequest { Operation = "DownloadPlc", DownloadScope = "software" }));

    [Fact]
    public void Online_requests_trust_no_certificate_and_allow_no_widening_prompt_by_default()
    {
        var request = new OnlineRequest();
        Assert.False(request.TrustDeviceCertificate);
        Assert.False(request.AllowNonTlsLogin);
        Assert.False(request.AllowSafetyProgramDownload || request.AllowHardwareDifferences || request.AllowActiveTestCancel
            || request.AllowExpandedDownload || request.AllowIdentificationDataLoad);
        Assert.Equal("software", request.DownloadScope);
        Assert.Equal(new[] { "software", "softwareOnlyChanges" }, OnlineCapabilities.DownloadScopes);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(new OnlineRequest { TrustDeviceCertificate = true, AllowExpandedDownload = true }));
        Assert.True(wire.RootElement.GetProperty("TrustDeviceCertificate").GetBoolean());
        Assert.True(JsonSerializer.Deserialize<OnlineRequest>(wire.RootElement.GetRawText())!.AllowExpandedDownload);
    }

    [Fact]
    public void An_online_PLC_is_taken_offline_and_the_state_read_back_decides()
    {
        // Fake OnlineProvider: the PLC reads Online, GoOffline changes the state, and the state read back decides the result.
        var provider = new FakeOnlineProvider("Online");
        string before = provider.State;
        provider.GoOffline();
        var outcome = OnlineTransitionPolicy.GoOfflineOutcome("PLC_1", before, provider.State);
        Assert.True(outcome.Success);
        Assert.Contains("now offline", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Online", outcome.Message, StringComparison.Ordinal);
        var stuck = OnlineTransitionPolicy.GoOfflineOutcome("PLC_1", "Online", "Disconnecting");
        Assert.False(stuck.Success);
        Assert.Contains("not confirmed", stuck.Message, StringComparison.Ordinal);
        Assert.True(OnlineTransitionPolicy.GoOfflineOutcome("PLC_1", "Offline", "Offline").Success);
    }

    private sealed class FakeOnlineProvider(string state)
    {
        internal string State { get; private set; } = state;
        internal void GoOffline() => State = "Offline";
    }

    [Fact]
    public void Connecting_is_polled_until_it_settles_within_the_bound()
    {
        var states = new Queue<string>(new[] { "Connecting", "Connecting", "Online" });
        int slept = 0;
        Assert.Equal("Online", OnlineTransitionPolicy.WaitWhileConnecting("Connecting", () => states.Dequeue(), ms => slept += ms, 10000, 250));
        Assert.Equal(750, slept);
        slept = 0;
        Assert.Equal("Connecting", OnlineTransitionPolicy.WaitWhileConnecting("Connecting", () => "Connecting", ms => slept += ms, 1000, 250));
        Assert.Equal(1000, slept);
        Assert.Equal("Protected", OnlineTransitionPolicy.WaitWhileConnecting("Protected",
            () => throw new InvalidOperationException("not polled"), _ => throw new InvalidOperationException("not slept")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("192.168.1.10", false)]
    public void GoOnline_requires_an_explicit_address(string? address, bool refused)
    {
        if (refused)
            Assert.Equal("ipAddress", Assert.Throws<ArgumentException>(() => OnlineTransitionPolicy.RequireAddress(address, "ipAddress")).ParamName);
        else OnlineTransitionPolicy.RequireAddress(address, "ipAddress");
    }

    [Fact]
    public void An_explicit_route_that_is_not_applied_is_refused_and_an_automatic_pick_continues()
    {
        var refusal = OnlineTransitionPolicy.RouteApplyRefusal(true, false, "The device is online.", "PN/IE / PLCSIM -> 1 X1");
        Assert.NotNull(refusal);
        Assert.Contains("The device is online.", refusal, StringComparison.Ordinal);
        Assert.Contains("ApplyConfiguration returned false", OnlineTransitionPolicy.RouteApplyRefusal(true, false, null, "route")!, StringComparison.Ordinal);
        Assert.Null(OnlineTransitionPolicy.RouteApplyRefusal(false, false, "failed", "route"));
        Assert.Null(OnlineTransitionPolicy.RouteApplyRefusal(true, true, null, "route"));
    }

    [Theory]
    [InlineData(new[] { "Offline" }, false)]
    [InlineData(new[] { "Offline", "Offline" }, false)]
    [InlineData(new[] { "Online" }, true)]
    [InlineData(new[] { "Connecting" }, true)]
    [InlineData(new[] { "Offline", "Online" }, true)]
    [InlineData(new string[0], true)]
    public void A_download_starts_from_an_offline_PLC(string[] states, bool refused)
    {
        var refusal = OnlineTransitionPolicy.DownloadStateRefusal("PLC_1", states);
        Assert.Equal(refused, refusal != null);
        if (refused && states.Length > 0) Assert.Contains("DisconnectOnlinePlc", refusal!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ObjectsIdentical", false)]
    [InlineData("FolderContentsIdentical", false)]
    [InlineData("CompareIrrelevant", false)]
    [InlineData("Unknown", false)]
    [InlineData("ObjectsDifferent", true)]
    [InlineData("LeftMissing", true)]
    [InlineData("RightMissing", true)]
    [InlineData("FolderContentsDifferent", true)]
    [InlineData("FolderContainsDifferencesOwnStateDifferent", true)]
    [InlineData("FolderContentEqualOwnStateDifferent", true)]
    public void Online_compare_reports_only_difference_states(string state, bool difference)
        => Assert.Equal(difference, OnlineTransitionPolicy.IsCompareDifference(state));

    [Theory]
    [InlineData("DownloadPlc", false, true, false, false, null, true)]
    [InlineData("DownloadPlc", false, true, true, false, "Error", false)]
    [InlineData("DownloadPlc", false, false, false, false, null, false)]
    [InlineData("UploadStationFromPlc", false, true, true, false, null, true)]
    [InlineData("GeneratePlcLoadableFile", false, true, false, true, null, false)]
    [InlineData("GetPlcBlockFingerprints", false, true, false, false, null, false)]
    [InlineData("DownloadPlc", true, true, true, false, "Success", false)]
    public void Completed_failures_and_unknown_writes_have_distinct_session_effects(string tool, bool success,
        bool nativeIssued, bool changed, bool files, string? state, bool reset)
        => Assert.Equal(reset, OnlineCompletionPolicy.OutcomeUnknown(
            new OnlineRequest { Operation = tool, DryRun = false }, success, nativeIssued, changed, files, false, state));
}
