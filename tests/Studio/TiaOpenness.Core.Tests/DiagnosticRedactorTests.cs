using System;
using System.Text.Json.Nodes;
using TiaOpenness.Core.Environment;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class DiagnosticRedactorTests
{
    [Theory]
    [InlineData("key")]
    [InlineData("keys")]
    [InlineData("ProtectedKey")]
    [InlineData("api_key")]
    [InlineData("X-API-Key")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("clientSecret")]
    [InlineData("credentials")]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    public void Nested_secret_fields_are_masked_even_when_values_are_arrays_or_objects(string field)
    {
        var redactor = new DiagnosticRedactor();
        foreach (string value in new[] { "\"private-one\"", "[\"private-one\"]", "{\"value\":\"private-one\"}" })
        {
            var result = redactor.Redact("{\"nested\":[{\"" + field + "\":" + value + "}],\"port\":8765}", "json");
            Assert.DoesNotContain("private-one", result.Text);
            Assert.Equal(8765, JsonNode.Parse(result.Text)!["port"]!.GetValue<int>());
            Assert.True(result.Rules.ContainsKey("secret-field"));
        }
    }

    [Theory]
    [InlineData("Authorization: Bearer private-one")]
    [InlineData("Proxy-Authorization: Basic private-one")]
    [InlineData("Authorization: Bearer\tprivate-one")]
    [InlineData("Proxy-Authorization: Basic\tprivate-one")]
    [InlineData("Authorization: Bearer\n\tprivate-one")]
    [InlineData("--http-api-key private-one --port 8765")]
    [InlineData("password='private-one'")]
    [InlineData("API_TOKEN=private-one")]
    [InlineData("GET http://localhost/?access_token=private-one&port=8765")]
    [InlineData("http://user:private-one@localhost/")]
    [InlineData("{\"password\":\"private-one\",broken")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nprivate-one\n-----END PRIVATE KEY-----")]
    public void Free_form_log_secret_shapes_are_removed(string input)
    {
        var result = new DiagnosticRedactor().Redact(input);
        Assert.DoesNotContain("private-one", result.Text); Assert.Contains(DiagnosticRedactor.Mask, result.Text); Assert.NotEmpty(result.Rules);
    }

    [Fact]
    public void Windows_accounts_and_profile_roots_are_masked_in_text_and_json()
    {
        var redactor = new DiagnosticRedactor();
        var name = System.Environment.UserName;
        string path = @"D:\Users\Other Operator\logs";
        var plain = redactor.Redact(name + " " + path).Text;
        Assert.DoesNotContain(name, plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\logs", plain);
        string json = System.Text.Json.JsonSerializer.Serialize(new { path, name });
        var output = redactor.Redact(json, "json").Text;
        Assert.DoesNotContain("Other Operator", output);
        Assert.DoesNotContain(name, output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\dept\Profiles$\Al\Desktop\report.txt", @"%USERPROFILE%\Desktop\report.txt")]
    [InlineData(@"\\dept\Users\Other Operator\Documents", @"%USERPROFILE%\Documents")]
    [InlineData(@"\\dept\C$\Users\Al\Desktop\notes", @"%USERPROFILE%\Desktop\notes")]
    [InlineData(@"\\dept\Al$\Downloads", @"%USERPROFILE%\Downloads")]
    [InlineData(@"//dept/Home/al/Documents", @"%USERPROFILE%/Documents")]
    [InlineData(@"ORG\Al signed in as Al@corp.example.com", "[USER] signed in as [USER]")]
    [InlineData(@"user=Al started a process for the Al team", "user=[USER] started a process for the Al team")]
    public void Network_profiles_and_principals_preserve_unrelated_short_words(string input, string expected)
    {
        var redactor = new DiagnosticRedactor(@"C:\Users\Al", "Al");
        Assert.Equal(expected, redactor.Redact(input).Text);
    }

    [Fact]
    public void Redirected_profile_and_account_fields_are_masked_while_short_object_names_stay_readable()
    {
        var redactor = new DiagnosticRedactor(@"\\files\staff$\R&D\May", "May");
        string json = System.Text.Json.JsonSerializer.Serialize(new { path = @"\\files\staff$\R&D\May\docs", account = "May",
            tag = new { name = "May", description = "May run" } });
        var result = JsonNode.Parse(redactor.Redact(json, "json").Text)!;
        Assert.Equal(@"%USERPROFILE%\docs", (string?)result["path"]);
        Assert.Equal("[USER]", (string?)result["account"]);
        Assert.Equal("May", (string?)result["tag"]!["name"]);
        Assert.Equal("May run", (string?)result["tag"]!["description"]);
    }

    [Fact]
    public void Known_config_values_are_removed_from_unlabelled_logs_and_invalid_configs_are_hidden()
    {
        var redactor = new DiagnosticRedactor();
        redactor.Redact("{\"ProtectedKey\":\"private-one\"}", "json");
        Assert.DoesNotContain("private-one", redactor.Redact("debug echo private-one").Text);
        Assert.Equal(DiagnosticRedactor.Mask, redactor.Redact("{broken private-one", "json").Text);
        Assert.Equal(DiagnosticRedactor.Mask, redactor.Redact("<broken private-one", "xml").Text);
    }

    [Fact]
    public void Xml_settings_and_auth_strings_in_json_arrays_are_redacted()
    {
        var redactor = new DiagnosticRedactor();
        var xml = redactor.Redact("<configuration><add key=\"apiKey\" value=\"private-one\"/><password>private-two</password></configuration>", "xml");
        Assert.DoesNotContain("private-one", xml.Text); Assert.DoesNotContain("private-two", xml.Text);
        Assert.DoesNotContain("private-three", redactor.Redact("[\"Bearer private-three\"]", "json").Text);
    }
}
