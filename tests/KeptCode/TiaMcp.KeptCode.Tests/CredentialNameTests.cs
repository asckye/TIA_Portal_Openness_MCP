using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class CredentialNameTests
{
    [Theory]
    [InlineData("PASs-word", true)]
    [InlineData("passwd", true)]
    [InlineData("pass.phrase", true)]
    [InlineData("clientSecret", true)]
    [InlineData("credentialSet", true)]
    [InlineData("proxyAuthorization", true)]
    [InlineData("pwd", true)]
    [InlineData("S_K", true)]
    [InlineData("serviceAPIKey", true)]
    [InlineData("accessKey", true)]
    [InlineData("private-key", true)]
    [InlineData("secretKey", true)]
    [InlineData("signing_key", true)]
    [InlineData("keyword", false)]
    [InlineData("accessKeyId", false)]
    [InlineData("entryKey", false)]
    [InlineData("previewToken", false)]
    [InlineData("sketch", false)]
    public void Credential_names_follow_normalized_markers_and_key_suffixes(string name, bool expected)
        => Assert.Equal(expected, CallJournalPayload.IsCredentialName(name));

    [Fact]
    public void Guard_pair_extensions_preserve_the_existing_approval_digest_and_card()
    {
        const string note = """passphrase=alpha authorization=beta password="gamma" <Password>delta</Password> password=epsilon""";
        var raw = new JsonObject { ["note"] = note };
        var reviewed = new JsonObject { ["note"] = note.Replace("epsilon", CallJournalPayload.Hidden) };
        var card = PendingApproval.Create("engine", "21", "WriteFixture", raw.ToJsonString(), "{}", 120);
        var expected = new JsonObject { ["releaseKey"] = "21", ["tool"] = "WriteFixture",
            ["identity"] = new JsonObject(), ["arguments"] = reviewed.DeepClone() };
        Assert.Equal(PendingApproval.Hash(PendingApproval.Canonical(expected)), card.ArgumentDigest);
        Assert.Equal(PendingApproval.Hash(PendingApproval.Canonical(reviewed)), card.InvocationArgumentsDigest);
        Assert.True(JsonNode.DeepEquals(reviewed, JsonNode.Parse(card.ParametersJson)));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Baseline_lists_only_reviewed_credential_properties(string release)
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest", "contracts", "v4", "baseline", release + ".json")));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var responseNames = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Visit(item);
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var member in node.EnumerateObject())
            {
                if (CallJournalPayload.IsCredentialName(member.Name)) names.Add(member.Name);
                if (CallJournalPayload.IsResponseCredentialName(member.Name)) responseNames.Add(member.Name);
                Visit(member.Value);
            }
        }
        Visit(baseline.RootElement.GetProperty("schemas"));
        Assert.Empty(names);
        Assert.Empty(responseNames);
        Visit(baseline.RootElement);
        var expected = new List<string> { "password" };
        if (release != "14sp1")
            expected.AddRange(new[] { "blockBindingPassword", "masterSecretPassword", "moduleAccessPassword", "newPassword", "umacPassword" });
        if (release is not ("14sp1" or "15.1" or "16")) expected.Add("serverPassword");
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), names);
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), responseNames);
    }
}
