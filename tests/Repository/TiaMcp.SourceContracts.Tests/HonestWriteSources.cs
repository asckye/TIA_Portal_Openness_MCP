using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class HonestWriteSources
{
    [Fact]
    public void Document_import_records_identity_read_only_after_complete_projection()
    {
        string source = Read("src/Adapters/Native/Plc/PlcDocumentImport.cs");
        int overflow = source.IndexOf("if(messages.Length>256", StringComparison.Ordinal);
        int projection = source.IndexOf("outcome.Identities=imported", overflow, StringComparison.Ordinal);
        int mark = source.IndexOf("outcome.IdentitiesRead=true;", projection, StringComparison.Ordinal);
        int validation = source.IndexOf("if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded", mark, StringComparison.Ordinal);
        Assert.True(overflow >= 0 && projection > overflow && mark > projection && validation > mark);
        Assert.EndsWith(".ToArray(); ", source[projection..mark], StringComparison.Ordinal);
        Assert.Equal(1, source.Split("outcome.IdentitiesRead=true;", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("public HmiMessage BindUnifiedHmiButtonPressedTag", "existing = InvokeCreate(mCreate, pressedStateTags")]
    [InlineData("public HmiMessage BindUnifiedHmiTagDynamization", "dyn = create.MakeGenericMethod(tagDynType).Invoke")]
    public void Unified_binding_creates_mark_mutation_before_native_call(string method, string call)
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedHmiAdapter.cs");
        int start = source.IndexOf(method, StringComparison.Ordinal);
        int native = source.IndexOf(call, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && native > start);
        Assert.EndsWith("meta[\"mayHaveChanged\"] = true; ", source[start..native], StringComparison.Ordinal);
    }

    [Fact]
    public void Unified_list_export_marks_file_effects_before_native_export()
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedObjectServicesAdapter.cs");
        int start = source.IndexOf("public HmiMessage ExportUnifiedEngineeringList", StringComparison.Ordinal);
        int call = source.IndexOf("var native = PlcGroupOperations.Call(collection, \"Export\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && call > start);
        int mark = source.IndexOf("meta[\"mayHaveWrittenFiles\"] = true;", start, StringComparison.Ordinal);
        int preview = source.IndexOf("if (dryRun) return", start, StringComparison.Ordinal);
        Assert.True(preview < mark && mark < call);
    }

    private static string Read(string path) => System.Text.RegularExpressions.Regex.Replace(Repository.ReadSource(
        Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path)), @"\s+", " ");

    [Theory]
    [InlineData("src/Adapters/Native/Plc/PlcOrganisationAdapter.cs", "PlcOrganisationPrimitives.Protect(provider, secure)")]
    [InlineData("src/Adapters/Native/Plc/PlcIntegrity.cs", "provider.Define(secure)")]
    [InlineData("src/Adapters/Native/Security/HardwareSecurityAdapter.cs", "secretProvider!.ChangePassword(secure!, secureNew!)")]
    public void Password_setters_use_the_documented_refusal_boundary(string path, string setter)
    {
        string source = Read(path);
        int call = source.IndexOf(setter, StringComparison.Ordinal);
        Assert.True(call >= 0);
        Assert.Contains("EngineeringCredentialRules.TrySetPassword", source[..call], StringComparison.Ordinal);
        Assert.Contains("EngineeringCredentialRules.PasswordPolicyRefusal", source[call..], StringComparison.Ordinal);
    }

    [Fact]
    public void Aml_import_uses_admission_without_exclusive_access_only_on_v21()
    {
        string source = Read("src/Adapters/Native/Hardware/HardwareAml.cs");
        source = source[source.IndexOf("public HardwareAddressingReply ImportHardwareAml", StringComparison.Ordinal)..];
        Assert.Contains("using var exclusive = !dryRun && ReleaseKey != \"21\" ? HardwareEditAccess() : null;", source, StringComparison.Ordinal);
        Assert.Contains("if (!dryRun && ReleaseKey == \"21\") HardwareWriteAdmission();", source, StringComparison.Ordinal);
        Assert.Equal(1, source.Split("HardwareEditAccess()", StringSplitOptions.None).Length - 1);
        Assert.Contains("cax.Import(source, option)", source, StringComparison.Ordinal);
        Assert.Contains("cax.Import(source, log, option)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Syslog_marks_only_the_selected_native_mutation()
    {
        string source = Read("src/Adapters/Native/Security/SecurityDeepAdapter.cs");
        int start = source.IndexOf("public SecurityResult ManageSyslogServers", StringComparison.Ordinal);
        int end = source.IndexOf(" });", start, StringComparison.Ordinal) + " });".Length;
        source = source[start..end];
        int update = source.IndexOf("case \"update\":", StringComparison.Ordinal);
        Assert.DoesNotContain("meta[\"mayHaveChanged\"] = true;", source[..update], StringComparison.Ordinal);
        Assert.Contains("var value = enable!.GetValue<bool>(); meta[\"mayHaveChanged\"] = true; manager.EnableSystemLogging = value;", source,
            StringComparison.Ordinal);
        Assert.Contains("meta[\"mayHaveChanged\"] = true; manager.TransportProtocol =", source, StringComparison.Ordinal);
        Assert.Contains("case \"createServer\": meta[\"mayHaveChanged\"] = true;", source, StringComparison.Ordinal);
        Assert.Contains("case \"deleteServer\": meta[\"mayHaveChanged\"] = true;", source, StringComparison.Ordinal);
        int updateEnd = source.IndexOf("case \"createServer\":", update, StringComparison.Ordinal);
        string branch = source[update..updateEnd];
        int enable = branch.IndexOf("manager.EnableSystemLogging =", StringComparison.Ordinal);
        int protocol = branch.IndexOf("manager.TransportProtocol =", StringComparison.Ordinal);
        int attributes = branch.IndexOf("_session.SetDynamicAttributes", StringComparison.Ordinal);
        Assert.True(enable >= 0 && enable < protocol && protocol < attributes);
        Assert.Equal(2, branch[..attributes].Split("meta[\"mayHaveChanged\"] = true;", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("src/Adapters/Native/Hardware/HardwareNetwork.cs", "public HardwareAddressingReply HardwareUpdateDeviceItemChannel", "using var access")]
    [InlineData("src/Adapters/Native/Hardware/HardwareAml.cs", "public HardwareAddressingReply ImportHardwareAml", "using var exclusive")]
    public void Hardware_writes_initialize_unchanged_before_edit_admission(string path, string method, string admission)
    {
        string source = Read(path);
        source = source[source.IndexOf(method, StringComparison.Ordinal)..];
        int acquire = source.IndexOf(admission, StringComparison.Ordinal);
        Assert.Contains("RunHardwareAddressStep(", source[..acquire], StringComparison.Ordinal);
        Assert.DoesNotContain("meta[\"mayHaveChanged\"] = true;", source[..acquire], StringComparison.Ordinal);
        string boundary = Read("src/Adapters/Native/Hardware/HardwareAddressingSteps.cs");
        boundary = boundary[boundary.IndexOf("private HardwareAddressingReply RunHardwareAddressStep", StringComparison.Ordinal)..];
        Assert.Equal(2, boundary.Split("meta[\"mayHaveChanged\"] = false;", StringSplitOptions.None).Length - 1);
        Assert.Contains("meta[\"mayHaveChanged\"] = false; return action(meta);", boundary, StringComparison.Ordinal);
        Assert.Contains("meta[\"mayHaveChanged\"] = false; reply.Message = action(meta);", boundary, StringComparison.Ordinal);
        string callbacks = Read("src/Adapters/Native/Engineering/WorkerFamilyCallbacks.cs");
        Assert.Contains("EngineeringStep = (tool, action) => RunHmiStepCore(tool, action, true);", callbacks, StringComparison.Ordinal);
        Assert.Contains("EngineeringCredentialRules.TryRecordRefusal(error, meta)", Read("src/Adapters/Native/Hmi/HmiEngineeringSteps.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Hmi_and_security_boundaries_seed_before_action()
    {
        string hmi = Read("src/Adapters/Native/Hmi/HmiEngineeringSteps.cs");
        Assert.Contains("meta[\"mayHaveChanged\"] = false; return action(meta);", hmi, StringComparison.Ordinal);
        Assert.Contains("meta[\"mayHaveChanged\"] = false; reply.Message = action(meta);", hmi, StringComparison.Ordinal);
        string engineering = Read("src/Adapters/Native/Engineering/EngineeringServicesWorker.cs");
        Assert.Contains("meta[\"mayHaveChanged\"] = false; return action(meta);", engineering, StringComparison.Ordinal);
        string security = Read("src/Adapters/Native/Security/ProjectProtection.cs");
        Assert.Contains("var meta = new HmiObject(values); try { return action(meta); }", security, StringComparison.Ordinal);
        Assert.Contains("var reply = RunHmiStep(tool, values =>", security, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_library_document_paths_gate_count_proof()
    {
        string source = Read("src/Adapters/Native/Libraries/LibraryService.cs");
        Assert.Equal(2, source.Split("LibraryPolicy.CanUseDocumentImportCountProof(", StringSplitOptions.None).Length - 1);
        Assert.Contains("Count(), countProof)", source, StringComparison.Ordinal);
        Assert.Contains("LibraryTypeCount(libraryRoot), typeCountProof)", source, StringComparison.Ordinal);
        Assert.Contains("if (canProveUnchanged && before == after)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_protection_marks_mutation_only_after_password_admission()
    {
        string source = Read("src/Adapters/Native/Plc/PlcIntegrity.cs");
        int preview = source.IndexOf("Write-protection preview; no changes.", StringComparison.Ordinal);
        int setter = source.IndexOf("EngineeringCredentialRules.TrySetPassword", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("MutationStarted = true", source[preview..setter], StringComparison.Ordinal);
        Assert.Contains("MutationStarted = true", source[setter..], StringComparison.Ordinal);
    }
}
