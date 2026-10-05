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
