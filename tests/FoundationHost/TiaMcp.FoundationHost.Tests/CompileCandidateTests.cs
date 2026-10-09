using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class CompileCandidateTests
{
    private const string Project = "C:\\authorized\\Project.ap21";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private sealed class Adapter : ICompileAdapter
    {
        internal CompileObservation State = new() { Binding = new() { ProcessId = 12, ProcessStartUtc = Start, ProjectFile = CandidatePrimitives.CanonicalProject(Project), Epoch = 1, Ownership = "borrowed" },
            ObjectValidity = "valid", Dirty = false, TargetId = "software-1", SoftwareId = "software-1", TargetKind = "PlcSoftware", TargetName = "PLC_1",
            Compilable = true, OfflineState = "offline", SafetyPermission = "not-applicable" };
        internal CompileDiagnostics Diagnostics = new() { State = "Success" };
        internal string Fault = "";
        internal readonly List<string> Trace = new();
        internal Action? Before, After, AfterLogin;
        internal bool Poisoned;
        public CompileObservation Observe()
        {
            Trace.Add("observe");
            if (Fault == "login-observe" && Trace.Contains("login") || Fault == "readback" && Trace.Contains("compile")
                || Fault == "logout-observe" && Trace.Contains("logout")) throw new IOException("secret credential and stack must not escape");
            return Copy(State);
        }
        public void BeforeAction() { Trace.Add("before"); Before?.Invoke(); if (Fault == "before") throw new IOException(); }
        public void Login(string password)
        {
            Trace.Add("login"); if (Fault == "login-before") throw new IOException(password);
            if (Fault == "login-no-effect") return;
            State.SafetyPermission = "logged-on"; AfterLogin?.Invoke(); if (Fault == "login-after" || Fault == "login-and-logout") throw new IOException(password);
        }
        public CompileDiagnostics Compile()
        {
            Trace.Add("compile"); if (Fault == "compile-before" || Fault == "compile-and-logout") throw new IOException();
            State.Dirty = true; After?.Invoke(); if (Fault == "compile-after") throw new IOException();
            return Copy(Diagnostics);
        }
        public void Logout()
        {
            Trace.Add("logout"); if (Fault == "logout-before" || Fault == "login-and-logout" || Fault == "compile-and-logout") throw new IOException();
            if (Fault == "logout-no-effect") return;
            State.SafetyPermission = "password-required"; if (Fault == "logout-after") throw new IOException();
        }
        public void MarkUncertain() { Poisoned = true; }
    }
    private static CompileRequest Request(string entry = "CompilePlcSoftware", bool password = false) => new() { Entry = entry, SoftwarePath = "PLC_1", PasswordProvided = password };
    private static Envelope Run(CompileSession s, Adapter a, CompileRequest r, string mode = "preview", string hash = "", string password = "", bool confirm = true)
        => s.Run(a, "21", r.Entry, "test", r, password, mode, confirm, hash, Project);
    private static string Hash(Envelope e) => e.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    [Theory]
    [InlineData("Success", Outcome.Succeeded)]
    [InlineData("Information", Outcome.Succeeded)]
    [InlineData("Warning", Outcome.Succeeded)]
    [InlineData("Error", Outcome.Failed)]
    [InlineData("987654", Outcome.Unknown)]
    public void EachCompileCandidateEntryUsesEverySdkState(string state, Outcome outcome)
    {
        foreach (string entry in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics", "CompileHmiDiagnostics", "CompileDevice" })
        {
            var a = new Adapter(); var s = new CompileSession(); var r = Request(entry);
            a.Diagnostics.State = state;
            var preview = Run(s, a, r); Assert.True(preview.Ok);
            var result = Run(s, a, r, "apply", Hash(preview));
            Assert.Equal(outcome, result.Meta.Outcome);
            Assert.Equal(outcome == Outcome.Unknown, result.Meta.RequiresSessionReset);
            Assert.Equal(state == "Warning", result.Meta.Warnings.Any(w => w.Code == WarningCode.NativeWarning));
            Assert.Equal(state, result.Data!.Value.GetProperty("attempt").GetProperty("diagnostics").GetProperty("state").GetString());
            Assert.Equal(1, a.Trace.Count(t => t == "compile"));
        }
    }
    [Theory]
    [InlineData("CompilePlcSoftware", "PlcSoftware")][InlineData("CompilePlcDiagnostics", "PlcSoftware")]
    [InlineData("CompileHmiDiagnostics", "HmiTarget")][InlineData("CompileHmiDiagnostics", "HmiSoftware via Device")]
    [InlineData("CompileDevice", "Device")][InlineData("CompileDevice", "DeviceItem")]
    public void EachEntryRetainsExactTargetAndOneNativeCompile(string entry, string kind)
    {
        var a = new Adapter(); a.State.TargetKind = kind; a.State.TargetId = kind; a.State.SoftwareId = entry == "CompileDevice" ? "" : "software-1";
        var s = new CompileSession(); var r = Request(entry); var p = Run(s, a, r); Assert.True(p.Ok, V4Json.Serialize(p));
        var op = p.Data!.Value.GetProperty("plan").GetProperty("operations")[0].GetProperty("arguments");
        Assert.Equal(kind, op.GetProperty("targetKind").GetString()); Assert.Equal(a.State.SoftwareId, op.GetProperty("softwareId").GetString());
        Assert.Single(op.GetProperty("nativeCalls").EnumerateArray()); Assert.DoesNotContain("compile", a.Trace);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok, V4Json.Serialize(result)); Assert.Equal(1, a.Trace.Count(t => t == "compile"));
        Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p)).Error!.Code); Assert.DoesNotContain("logout", a.Trace);
    }
    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")][InlineData("CompileHmiDiagnostics")][InlineData("CompileDevice")]
    public void OnlinePreconditionIsRefusedWithoutAutoOffline(string entry)
    {
        var a = new Adapter(); a.State.OfflineState = "online"; a.State.OfflineTargets = new[] { "PLC_1" }; var r = Request(entry);
        var result = Run(new(), a, r); Assert.Equal(ErrorCode.OfflineRequired, result.Error!.Code); Assert.Equal(Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        Assert.Equal("online", result.Data!.Value.GetProperty("observedState").GetProperty("offlineState").GetString()); Assert.Equal(new[] { "observe" }, a.Trace);
    }
    [Theory]
    [InlineData("ordinary", false)][InlineData("unprotected", false)][InlineData("created", true)][InlineData("existing", true)]
    public void OnlyThisCallsCreatedSafetyLoginIsEnded(string state, bool password)
    {
        var a = new Adapter(); a.State.SafetyPermission = state == "ordinary" ? "not-applicable" : state == "unprotected" ? "unprotected" : state == "created" ? "password-required" : "logged-on";
        var s = new CompileSession(); var r = Request(password: password); string pw = password ? "do-not-leak" : ""; var p = Run(s, a, r, password: pw); Assert.True(p.Ok);
        Assert.DoesNotContain(pw.Length > 0 ? pw : "never-secret", V4Json.Serialize(p));
        var result = Run(s, a, r, "apply", Hash(p), pw); Assert.True(result.Ok, V4Json.Serialize(result));
        Assert.Equal(state == "created" ? new[] { "login", "compile", "logout" } : new[] { "compile" }, a.Trace.Where(t => t != "observe" && t != "before"));
        Assert.Equal(state == "existing" ? "logged-on" : a.State.SafetyPermission, result.Data!.Value.GetProperty("observedState").GetProperty("safetyPermission").GetString());
    }
    [Theory]
    [InlineData("password-required", false, ErrorCode.AuthenticationRequired)]
    [InlineData("api-unavailable", true, ErrorCode.UnsupportedCapability)]
    [InlineData("not-applicable", true, ErrorCode.UnsupportedCapability)]
    public void PasswordIsNeverIgnoredAndRequiredPermissionIsDisclosed(string permission, bool supplied, ErrorCode code)
    {
        var a = new Adapter(); a.State.SafetyPermission = permission;
        var result = Run(new(), a, Request(password: supplied), password: supplied ? "secret" : ""); Assert.Equal(code, result.Error!.Code);
        Assert.Equal(permission, result.Data!.Value.GetProperty("observedState").GetProperty("safetyPermission").GetString()); Assert.DoesNotContain("compile", a.Trace);
    }
    [Theory]
    [InlineData("online")][InlineData("dirty")]
    public void ChangedCompilePreconditionAfterLoginStillCleansOnlyTheOwnedLogin(string change)
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true);
        var p = Run(s, a, r, password: "secret"); a.AfterLogin = () => Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p), "secret"); Assert.Equal(Outcome.Failed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset);
        Assert.Equal(new[] { "login", "logout" }, a.Trace.Where(t => t != "observe" && t != "before"));
        Assert.Equal("password-required", result.Data!.Value.GetProperty("observedState").GetProperty("safetyPermission").GetString());
    }
    [Theory]
    [InlineData("online")][InlineData("dirty")]
    public void LoginFaultAfterPermissionTransitionStillCleansOwnedLoginWhenCompilePreconditionsChange(string change)
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true);
        var p = Run(s, a, r, password: "secret"); a.Fault = "login-after"; a.AfterLogin = () => Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p), "secret"); Assert.Equal(Outcome.Failed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset);
        Assert.Equal(new[] { "login", "logout" }, a.Trace.Where(t => t != "observe" && t != "before"));
        Assert.True(result.Data!.Value.GetProperty("attempt").GetProperty("loginCreated").GetBoolean());
    }
    [Fact]
    public void LoginReturningWithoutPermissionDoesNotClaimOrCleanAnUncreatedLogin()
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true);
        var p = Run(s, a, r, password: "secret"); a.Fault = "login-no-effect"; var result = Run(s, a, r, "apply", Hash(p), "secret");
        Assert.Equal(Outcome.Failed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset);
        Assert.False(result.Data!.Value.GetProperty("attempt").GetProperty("loginCreated").GetBoolean());
        Assert.Equal(new[] { "login" }, a.Trace.Where(t => t != "observe" && t != "before"));
    }
    [Theory]
    [InlineData("before", Outcome.RejectedBeforeOperation, false, false)]
    [InlineData("login-before", Outcome.Failed, false, false)][InlineData("login-after", Outcome.Failed, false, true)]
    [InlineData("login-observe", Outcome.Unknown, true, false)]
    [InlineData("compile-before", Outcome.Unknown, true, true)][InlineData("compile-after", Outcome.Unknown, true, true)]
    [InlineData("logout-before", Outcome.Partial, false, true)][InlineData("logout-after", Outcome.Partial, false, true)]
    [InlineData("readback", Outcome.Unknown, true, false)][InlineData("logout-observe", Outcome.Unknown, true, true)]
    [InlineData("logout-no-effect", Outcome.Partial, false, true)]
    public void LoginCompileLogoutFaultsKeepActualStateAndCleanupEvidence(string fault, Outcome outcome, bool reset, bool logout)
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true); var p = Run(s, a, r, password: "secret"); Assert.True(p.Ok); a.Fault = fault;
        var result = Run(s, a, r, "apply", Hash(p), "secret"); Assert.Equal(outcome, result.Meta.Outcome); Assert.Equal(reset, result.Meta.RequiresSessionReset); Assert.Equal(reset, a.Poisoned);
        Assert.Equal(logout ? 1 : 0, a.Trace.Count(t => t == "logout")); Assert.InRange(a.Trace.Count(t => t == "compile"), 0, 1);
        Assert.DoesNotContain("secret", V4Json.Serialize(result));
        Assert.True(result.Data!.Value.GetProperty("attempt").TryGetProperty("cleanupState", out _));
        if (fault.StartsWith("logout-") && fault != "logout-observe") Assert.Contains(result.Meta.Warnings, w => w.Code == WarningCode.CleanupFailed);
        int calls = a.Trace.Count(t => t == "compile"); Assert.False(Run(s, a, r, "apply", Hash(p), "secret").Ok); Assert.Equal(calls, a.Trace.Count(t => t == "compile"));
    }
    [Theory]
    [InlineData("login-and-logout", Outcome.Partial)][InlineData("compile-and-logout", Outcome.Unknown)]
    public void PrimaryAndCleanupFailuresAreBothRetained(string fault, Outcome outcome)
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true);
        var p = Run(s, a, r, password: "secret"); a.Fault = fault; var result = Run(s, a, r, "apply", Hash(p), "secret");
        Assert.Equal(outcome, result.Meta.Outcome); var attempt = result.Data!.Value.GetProperty("attempt");
        Assert.Equal(JsonValueKind.Object, attempt.GetProperty("fault").ValueKind); Assert.Equal(JsonValueKind.Object, attempt.GetProperty("cleanupFault").ValueKind);
        Assert.Equal("logged-on", result.Data.Value.GetProperty("observedState").GetProperty("safetyPermission").GetString());
        Assert.Contains(result.Meta.Warnings, w => w.Code == WarningCode.CleanupFailed); Assert.Equal(1, a.Trace.Count(t => t == "logout"));
        if (fault == "login-and-logout") Assert.DoesNotContain("Compile returned", result.Error!.Message);
    }
    [Theory]
    [InlineData(0, 1)][InlineData(1, 1)][InlineData(2, 1)][InlineData(1, 0)]
    public void FullNestedDiagnosticsOverrideFalseRootSuccessAndPreserveCounts(int root, int leaves)
    {
        var a = new Adapter(); a.Diagnostics.RootErrorCount = root;
        a.Diagnostics.Messages = new[] { new CompileMessage { State = "Success", Messages = leaves == 0 ? Array.Empty<CompileMessage>()
            : new[] { new CompileMessage { State = "Error", Path = "PLC_1/Program blocks/Block_1", Description = "nested error" } } } };
        var s = new CompileSession(); var r = Request(); var p = Run(s, a, r); var result = Run(s, a, r, "apply", Hash(p));
        Assert.Equal(Outcome.Failed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset);
        var d = result.Data!.Value.GetProperty("attempt").GetProperty("diagnostics"); Assert.Equal(root, d.GetProperty("rootErrorCount").GetInt32());
        Assert.Equal(leaves, d.GetProperty("leafErrorCount").GetInt32()); Assert.Equal(root == leaves, d.GetProperty("countsConsistent").GetBoolean());
        if (leaves > 0) Assert.Contains("nested error", d.GetRawText()); Assert.Equal(1, a.Trace.Count(t => t == "compile"));
    }
    [Fact]
    public void WarningTreeAndDuplicateDescriptionsRetainAllLeaves()
    {
        var a = new Adapter(); a.Diagnostics.State = "Warning"; a.Diagnostics.RootWarningCount = 2;
        a.Diagnostics.Messages = new[] { new CompileMessage { State = "Warning", Messages = new[] {
            new CompileMessage { State = "Warning", Path = "one", Description = "same" }, new CompileMessage { State = "Warning", Path = "two", Description = "same" } } } };
        var s = new CompileSession(); var r = Request(); var p = Run(s, a, r); var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok);
        Assert.Equal(2, result.Data!.Value.GetProperty("attempt").GetProperty("diagnostics").GetProperty("leafWarningCount").GetInt32());
    }
    private static void Change(Adapter a, string change)
    {
        switch (change)
        {
            case "pid": a.State.Binding.ProcessId++; break; case "start": a.State.Binding.ProcessStartUtc = Start.AddSeconds(1); break;
            case "project": a.State.Binding.ProjectFile = "C:\\other\\Other.ap21"; break; case "epoch": a.State.Binding.Epoch++; break;
            case "worker-epoch": a.State.Binding.WorkerEpoch++; break; case "target": a.State.TargetId += "changed"; break;
            case "software": a.State.SoftwareId += "changed"; break; case "kind": a.State.TargetKind = "Device"; break;
            case "online": a.State.OfflineState = "online"; break; case "safety": a.State.SafetyPermission = "password-required"; break;
            case "dirty": a.State.Dirty = true; break; case "validity": a.State.ObjectValidity = "invalid"; break;
        }
    }
    [Theory]
    [InlineData("pid", false)][InlineData("start", false)][InlineData("project", false)][InlineData("epoch", false)][InlineData("worker-epoch", false)]
    [InlineData("target", false)][InlineData("software", false)][InlineData("kind", false)][InlineData("online", false)][InlineData("safety", false)][InlineData("dirty", false)][InlineData("validity", false)]
    [InlineData("pid", true)][InlineData("start", true)][InlineData("project", true)][InlineData("epoch", true)][InlineData("worker-epoch", true)]
    [InlineData("target", true)][InlineData("software", true)][InlineData("kind", true)][InlineData("online", true)][InlineData("safety", true)][InlineData("dirty", true)][InlineData("validity", true)]
    public void PreviewOrFinalBoundaryChangesPreventEveryNativeAction(string change, bool final)
    {
        var a = new Adapter(); var s = new CompileSession(); var r = Request(); var p = Run(s, a, r); Assert.True(p.Ok);
        if (final) a.Before = () => Change(a, change); else Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.False(result.Meta.RequiresSessionReset);
        Assert.DoesNotContain("compile", a.Trace); Assert.DoesNotContain("login", a.Trace); Assert.DoesNotContain("logout", a.Trace);
    }
    [Theory]
    [InlineData("pid")][InlineData("project")][InlineData("epoch")][InlineData("target")][InlineData("software")][InlineData("kind")][InlineData("validity")]
    public void PostNativeStateChangeIsUnknownWithoutReplay(string change)
    {
        var a = new Adapter(); var s = new CompileSession(); var r = Request(); var p = Run(s, a, r); a.After = () => Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset);
        Assert.Equal(JsonValueKind.Object, result.Data!.Value.GetProperty("observedState").GetProperty("binding").ValueKind); Assert.Equal(1, a.Trace.Count(t => t == "compile"));
    }
    [Fact]
    public void HashIsCultureStableSecretSensitiveAndConfirmationIsExplicit()
    {
        var a = new Adapter(); a.State.SafetyPermission = "password-required"; var s = new CompileSession(); var r = Request(password: true); var p = Run(s, a, r, password: "secret-a");
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try { System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(Hash(p), Hash(s.Run(a, "21", r.Entry, "other-id", r, "secret-a"))); }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
        Assert.NotEqual(Hash(p), Hash(Run(s, a, r, password: "secret-b")));
        Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p), "secret-b").Error!.Code);
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p), "secret-a", false).Error!.Code); Assert.DoesNotContain("compile", a.Trace);
    }
    [Fact]
    public void RequestMutationAndUnavailableOfflineProofRefuseBeforeCompile()
    {
        var a = new Adapter(); var s = new CompileSession(); var r = Request(); var p = Run(s, a, r); a.Before = () => r.OfflinePolicy = "auto";
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.DoesNotContain("compile", a.Trace);
        a = new(); a.State.OfflineState = "unavailable"; Assert.Equal(ErrorCode.PreconditionFailed, Run(new(), a, Request()).Error!.Code);
    }
    private sealed class Worker(Adapter adapter) : IFoundationWorker
    {
        internal string Forge = "";
        internal readonly List<string> Actions = new();
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("CompileCandidate", operation); var call = args["candidate"]!.Deserialize<CompileCall>()!; Actions.Add(call.Action);
            CompileReply reply;
            if (call.Action == "observe") reply = new() { Observation = adapter.Observe() };
            else
            {
                var attempt = CandidateExecution.Compile(adapter, call.Check!, call.Password); reply = new() { Attempt = attempt, RequiresSessionReset = attempt.RequiresSessionReset };
                if (Forge == "missing") reply.Attempt = null;
                if (Forge == "count") attempt.Diagnostics!.LeafErrorCount++;
                if (Forge == "target") attempt.After!.TargetId += "forged";
                if (Forge == "logout") { attempt.LoginCreated = false; attempt.LogoutIssued = true; }
                if (Forge == "reset") reply.RequiresSessionReset = !attempt.RequiresSessionReset;
            }
            return Task.FromResult(JsonSerializer.SerializeToNode(reply));
        }
    }
    [Theory]
    [InlineData("success")][InlineData("missing")][InlineData("count")][InlineData("target")][InlineData("logout")][InlineData("reset")][InlineData("compile-before")][InlineData("before")]
    public void FoundationUsesAtomicBoundaryAndInvalidReplyPoisonsOtherFamilies(string fault)
    {
        var a = new Adapter(); var w = new Worker(a); var s = FoundationCandidateSession.For(w); var r = Request();
        Envelope Call(string mode, string hash = "") => s.Compile("21", r.Entry, "test", r, "", mode, true, hash, Project, default);
        var p = Call("preview"); Assert.True(p.Ok); w.Forge = fault; a.Fault = fault; var result = Call("apply", Hash(p));
        Assert.Equal(new[] { "observe", "observe", "execute" }, w.Actions); Assert.Equal(fault == "before" ? 0 : 1, a.Trace.Count(t => t == "compile"));
        bool unknown = fault != "success" && fault != "before"; Assert.Equal(unknown, result.Meta.RequiresSessionReset); Assert.Equal(fault == "success", result.Ok);
        if (unknown)
        {
            Assert.Equal(ErrorCode.SessionResetRequired, Call("preview").Error!.Code);
            Assert.Equal(ErrorCode.SessionResetRequired, s.SaveClose("21", "SaveProject", "test", new(), "preview", false, "", "", false, default).Error!.Code);
            Assert.Equal(ErrorCode.SessionResetRequired, s.Session("21", "ConnectPortal", "test", new(), "preview", false, "", "", false, default).Error!.Code);
        }
    }
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public void FoundationSchemasSelectOnlyTwoExistingEntries(string release)
    {
        foreach (var source in FoundationTools.Create(new Worker(new()), release))
        {
            var current = new FoundationV4Tool(source, release, _ => BehaviorPolicy.Current);
            var safe = new FoundationV4Tool(source, release, f => f == "P6-COMPILE" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current);
            bool selected = CompileContract.Entries.Contains(current.ProtocolTool.Name);
            Assert.Equal(selected ? CompileContract.Schema(current.ProtocolTool.Name).GetRawText() : current.ProtocolTool.InputSchema.GetRawText(), safe.ProtocolTool.InputSchema.GetRawText());
            if (selected) Assert.False(current.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("offlinePolicy", out _));
        }
    }
    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")][InlineData("CompileDevice")][InlineData("CompileHmiDiagnostics")]
    public void ClosedCandidateSchemaHasRequirePreviewAndNoAutoOfflineRoute(string entry)
    {
        var schema = CompileContract.Schema(entry); Assert.False(schema.GetProperty("additionalProperties").GetBoolean()); var p = schema.GetProperty("properties");
        Assert.Equal("require", p.GetProperty("offlinePolicy").GetProperty("default").GetString()); Assert.Equal("preview", p.GetProperty("mode").GetProperty("default").GetString());
        Assert.False(p.GetProperty("confirm").GetProperty("default").GetBoolean()); Assert.False(p.TryGetProperty("autoOffline", out _));
    }
}
