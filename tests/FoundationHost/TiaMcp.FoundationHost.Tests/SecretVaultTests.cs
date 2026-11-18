extern alias toolpipeline;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;
using Xunit;
using VaultArguments = toolpipeline::TiaMcpServer.ModelContextProtocol.SecretVaultArguments;

public sealed class SecretVaultTests
{
    internal const string Secret = "synthetic-vault-password-83!";

    [Fact]
    public void Current_user_round_trip_preserves_case_sensitive_names_and_audits_only_names_and_actions()
    {
        using var fixture = new VaultFileScope();
        var vault = fixture.Vault;
        Assert.Empty(vault.Names());
        Assert.False(vault.TryResolve("plc1", out _));
        Assert.False(File.Exists(SecretVault.VaultPath));
        vault.Set("plc1", Secret, fixture.Audit);
        vault.Set("Plc1", "another-synthetic-password", fixture.Audit);
        Assert.Equal(new[] { "Plc1", "plc1" }, vault.Names());
        Assert.True(vault.TryResolve("plc1", out var value));
        Assert.Equal(Secret, value);
        Assert.False(vault.TryResolve("PLC1", out _));
        string file = File.ReadAllText(SecretVault.VaultPath);
        Assert.DoesNotContain(Secret, file);
        using var json = JsonDocument.Parse(file);
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.NotEmpty(Convert.FromBase64String(json.RootElement.GetProperty("secrets").GetProperty("plc1").GetString()!));
        Assert.True(vault.Remove("plc1", fixture.Audit));
        Assert.False(vault.Remove("plc1", fixture.Audit));
        Assert.False(vault.TryResolve("plc1", out _));
        Assert.Equal(3, fixture.Audit.Read().Count);
        Assert.All(fixture.Audit.Read(), row => Assert.Equal("secret-vault-change", row.Event));
        Assert.Equal(new[] { "set", "set", "remove" }, fixture.Audit.Read().Select(row => row.Outcome));
        Assert.Contains("plc1", string.Join("\n", fixture.Audit.Read().Select(AuditLog.Canonical)));
        Assert.DoesNotContain(Secret, string.Join("\n", fixture.Audit.Read().Select(AuditLog.Canonical)));
        Assert.True(fixture.Audit.Verify().Passed);
    }

    [Theory]
    [InlineData("")] [InlineData("_plc")] [InlineData("a/b")] [InlineData("a b")]
    [InlineData("中文")] [InlineData("a\n")] [InlineData("a:password")]
    public void Invalid_names_are_refused_without_writing(string name)
    {
        using var fixture = new VaultFileScope();
        Assert.Throws<ArgumentException>(() => fixture.Vault.Set(name, Secret, fixture.Audit));
        Assert.False(File.Exists(SecretVault.VaultPath));
    }

    [Fact]
    public void Name_length_and_reference_syntax_have_exact_boundaries()
    {
        SecretVault.ValidateName("a" + new string('-', 63));
        Assert.Throws<ArgumentException>(() => SecretVault.ValidateName(new string('a', 65)));
        Assert.True(CallJournalPayload.IsVaultReference("vault:a0_B.c-d"));
        foreach (string value in new[] { "vault:", "vault:_bad", "Vault:plc1", "vault:plc1\n", " vault:plc1", "vault:plc1 suffix" })
            Assert.False(CallJournalPayload.IsVaultReference(value));
    }

    [Theory]
    [InlineData("not-json")] [InlineData("{}")] [InlineData("{\"version\":2,\"secrets\":{}}")]
    [InlineData("{\"version\":1,\"secrets\":{\"plc1\":\"invalid base64\"}}")]
    [InlineData("{\"version\":1,\"secrets\":{\"plc1\":\"AQID\"}}")]
    public void Invalid_vaults_fail_closed_and_do_not_echo_the_record(string text)
    {
        using var fixture = new VaultFileScope();
        File.WriteAllText(SecretVault.VaultPath, text);
        var error = Assert.Throws<IOException>(() => fixture.Vault.TryResolve("plc1", out _));
        Assert.Contains("secret-vault-unreadable", error.Message);
        Assert.DoesNotContain(text, error.Message);
    }

    [Fact]
    public void One_undecryptable_entry_refuses_every_reference_in_the_file()
    {
        using var fixture = new VaultFileScope();
        File.WriteAllText(SecretVault.VaultPath, JsonSerializer.Serialize(new { version = 1,
            secrets = new Dictionary<string, string> { ["plc1"] = HttpAccessKeyStore.Protect(Secret), ["damaged"] = "AQID" } }));
        foreach (string name in new[] { "plc1", "missing", "damaged" })
            Assert.Contains("secret-vault-unreadable", Assert.Throws<IOException>(() => fixture.Vault.TryResolve(name, out _)).Message);
        Assert.Throws<IOException>(() => fixture.Vault.Names());
    }

    [Fact]
    public void Unreadable_files_and_external_updates_are_observed_on_the_next_call()
    {
        using var fixture = new VaultFileScope();
        fixture.Vault.Set("plc1", Secret, fixture.Audit);
        using (new FileStream(SecretVault.VaultPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => fixture.Vault.TryResolve("plc1", out _));
        File.WriteAllText(SecretVault.VaultPath, "{\"version\":1,\"secrets\":{}}");
        Assert.Empty(fixture.Vault.Names());
        Assert.False(fixture.Vault.TryResolve("plc1", out _));
    }

    [Fact]
    public void Resolution_copies_only_declared_credentials_and_refuses_nested_credentials_with_the_pointer()
    {
        using var fixture = new VaultFileScope();
        fixture.Vault.Set("plc1", Secret, fixture.Audit);
        var original = JsonSerializer.SerializeToElement(new { password = "vault:plc1", title = "ordinary text" });
        using var approval = VaultApproval("ConnectOnlinePlc", original);
        Assert.Null(VaultArguments.Prepare("ConnectOnlinePlc", original, true, out var resolved, out bool changed));
        Assert.True(changed);
        Assert.Equal(Secret, (string?)resolved["password"]);
        Assert.Equal("ordinary text", (string?)resolved["title"]);
        Assert.Contains("vault:plc1", original.GetRawText());
        Assert.Null(VaultArguments.Prepare("ConnectOnlinePlc", JsonSerializer.SerializeToElement(new { password = "plaintext-compatible" }),
            true, out var plaintext, out bool plaintextChanged));
        Assert.False(plaintextChanged);
        Assert.Equal("plaintext-compatible", (string?)plaintext["password"]);
        var error = VaultArguments.Validate("ConnectOnlinePlc",
            JsonSerializer.SerializeToElement(new { users = new[] { new { password = "vault:missing" } } }));
        var details = Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error!.Details);
        Assert.Equal("/users/0/password", details.Parameter);
        Assert.Contains("declared credential", error.Message);
        var unknown = VaultArguments.Validate("ConnectOnlinePlc", JsonSerializer.SerializeToElement(new { password = "vault:missing" }));
        Assert.Equal("/password", Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(unknown!.Details).Parameter);
        Assert.Contains("vault:missing", unknown.Message);
    }

    [Theory]
    [InlineData("{\"users\":[{\"password\":\"vault:plc1\"}]}", "/users/0/password")]
    [InlineData("{\"properties\":{\"password\":\"vault:plc1\"}}", "/properties/password")]
    [InlineData("{\"attributes\":{\"token\":\"vault:plc1\"}}", "/attributes/token")]
    [InlineData("{\"customAttributes\":{\"secret\":\"vault:plc1\"}}", "/customAttributes/secret")]
    [InlineData("{\"entry\":{\"authorization\":\"vault:plc1\"}}", "/entry/authorization")]
    [InlineData("{\"entryKey\":\"vault:plc1\"}", "/entryKey")]
    [InlineData("{\"objectPath\":[{\"key\":\"vault:plc1\"}]}", "/objectPath/0/key")]
    [InlineData("{\"previewToken\":\"vault:plc1\"}", "/previewToken")]
    [InlineData("{\"title\":\"vault:plc1\"}", "/title")]
    public void Noncredential_references_are_refused_with_the_exact_pointer(string json, string pointer)
    {
        var error = VaultArguments.Validate("ConnectOnlinePlc", JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(pointer, Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error!.Details).Parameter);
    }

    [Theory]
    [InlineData("ManageDriveSecurity")] [InlineData("ManageTeamcenterConnection")] [InlineData("ManageSinumerikArchive")]
    public void Reviewed_credential_parameters_accept_references_without_resolving_previews(string tool)
    {
        using var fixture = new VaultFileScope(); fixture.Vault.Set("plc1", Secret, fixture.Audit);
        var arguments = JsonSerializer.SerializeToElement(new { password = "vault:plc1" });
        Assert.Null(VaultArguments.Prepare(tool, arguments, false, out var preview, out bool changed));
        Assert.False(changed); Assert.Equal("vault:plc1", (string?)preview["password"]);
        using var approval = VaultApproval(tool, arguments);
        Assert.Null(VaultArguments.Prepare(tool, arguments, true, out var execution, out changed));
        Assert.True(changed); Assert.Equal(Secret, (string?)execution["password"]);
    }

    [Fact]
    public void Resolved_reference_looking_plaintext_is_hidden_and_plan_identity_uses_its_original_position_in_both_copies()
    {
        var original = JsonSerializer.SerializeToElement(new { password = "vault:plc1" });
        var resolved = new JsonObject { ["password"] = "vault:actual-plaintext" };
        using var scope = VaultArguments.Begin(original, resolved);
        Assert.Equal("••••", (string?)toolpipeline::TiaOpenness.Shared.CallJournalPayload.RedactArguments(resolved)!["password"]);
        var logic = typeof(TiaMcp.Logic.V4.NativeFailureDiagnostics).Assembly.GetType("TiaOpenness.Shared.ApprovalJournalPayload", true)!;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var redacted = (JsonNode)logic.GetMethod("RedactArguments", flags)!.Invoke(null, new object[] { resolved })!;
        Assert.Equal("••••", (string?)redacted["password"]);
        var plan = (JsonNode)logic.GetMethod("ProtectPlanArguments", flags)!.Invoke(null, new object[] { resolved })!;
        Assert.Equal("vault:plc1", (string?)plan["password"]);
        Assert.False(TiaMcp.Logic.V4.NativeFailureDiagnostics.VaultReference("vault:actual-plaintext"));
    }

    [Theory]
    [InlineData("name")] [InlineData("key")]
    public void Named_secret_values_stay_hidden_even_when_the_unresolved_value_looks_like_a_reference(string key)
    {
        var named = new JsonObject { [key] = "password", ["value"] = "vault:unresolved-value" };
        Assert.Equal("••••", (string?)CallJournalPayload.RedactArguments(named)!["value"]);
        var logic = typeof(TiaMcp.Logic.V4.NativeFailureDiagnostics).Assembly.GetType("TiaOpenness.Shared.ApprovalJournalPayload", true)!;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        var redacted = (JsonNode)logic.GetMethod("RedactArguments", flags)!.Invoke(null, new object[] { named })!;
        Assert.Equal("••••", (string?)redacted["value"]);
        Assert.DoesNotContain("vault:unresolved-value", CallJournalPayload.Redact(named.ToJsonString()));
    }

    internal static IDisposable VaultApproval(string tool, JsonElement args)
    {
        var pending = PendingApproval.Create("test", "21", tool, args.GetRawText(), "{}", 1);
        return VaultArguments.Approved(tool, args, new ApprovalOutcome(pending, false, null));
    }

}

internal sealed class VaultFileScope : IDisposable
{
    private readonly byte[]? previous;
    private readonly string auditDirectory = Path.Combine(AppContext.BaseDirectory, "vault-audit", Guid.NewGuid().ToString("N"));
    internal SecretVault Vault { get; } = new();
    internal AuditLog Audit { get; }
    internal VaultFileScope()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SecretVault.VaultPath)!);
        previous = File.Exists(SecretVault.VaultPath) ? File.ReadAllBytes(SecretVault.VaultPath) : null;
        File.Delete(SecretVault.VaultPath);
        Audit = new AuditLog(auditDirectory);
    }
    public void Dispose()
    {
        if (previous == null) File.Delete(SecretVault.VaultPath); else File.WriteAllBytes(SecretVault.VaultPath, previous);
        if (Directory.Exists(auditDirectory)) Directory.Delete(auditDirectory, true);
    }
}
