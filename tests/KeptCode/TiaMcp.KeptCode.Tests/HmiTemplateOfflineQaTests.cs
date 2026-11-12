using System;
using System.IO;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Engine.Tests.Shared;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class HmiTemplateOfflineQaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tia_hmi_offline_qa_" + Guid.NewGuid().ToString("N"));
    private static string RepositoryRoot => TestPaths.RepositoryRoot(AppContext.BaseDirectory);

    public HmiTemplateOfflineQaTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("templates/hmi")]
    [InlineData("templates/standards/tiamcp.basic/sources/hmi")]
    public void Shipped_unified_templates_pass_layout_qa(string relativeDirectory)
    {
        var report = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(
            Path.Combine(RepositoryRoot, relativeDirectory), HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
        Assert.True(report["ok"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal(7, report["templateCount"]!.GetValue<int>());
        Assert.Equal(0, report["failed"]!.GetValue<int>());
        Assert.All(report["results"]!.AsArray(), row =>
        {
            var size = Regex.Match(Path.GetFileNameWithoutExtension(row!["file"]!.ToString()), @"_(\d{3,5})x(\d{3,5})$");
            Assert.True(size.Success);
            Assert.Equal(int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture), row["width"]!.GetValue<int>());
            Assert.Equal(int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture), row["height"]!.GetValue<int>());
            Assert.True(row["executionJsonChecked"]!.GetValue<bool>());
        });
    }

    [Theory]
    [InlineData("unified_declared_1024x768.json", "{\"Screen\":{\"Width\":1280,\"Height\":720}}", 1280, 720)]
    [InlineData("unified_lowercase_1024x768.json", "{\"screen\":{\"width\":1280,\"height\":720}}", 1280, 720)]
    [InlineData("unified_named_1024x768.json", "{\"screen\":{}}", 1024, 768)]
    [InlineData("unified_plain.json", "{\"screen\":{}}", 800, 480)]
    public void Size_comes_from_the_declaration_then_the_name_then_the_fallback(string name, string json, int width, int height)
    {
        var file = Write(name, json);
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal((width, height), HmiTemplateLayoutAnalyzer.ScreenSize(root, file, 800, 480));
        var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(file, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds, 800, 480);
        var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(file, 800, 480);
        Assert.Equal(width, design["width"]!.GetValue<int>());
        Assert.Equal(height, design["height"]!.GetValue<int>());
        Assert.Equal(width, layout["width"]!.GetValue<int>());
        Assert.Equal(height, layout["height"]!.GetValue<int>());
        Assert.Equal("pass", layout["status"]!.ToString());
    }

    [Fact]
    public void Template_with_no_size_anywhere_still_fails()
    {
        var file = Write("unified_plain.json", "{\"screen\":{},\"items\":[]}");
        var row = HmiTemplateLayoutAnalyzer.AnalyzeFile(file, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
        Assert.Equal("fail", row["status"]!.ToString());
        Assert.Contains(row["errors"]!.AsArray(), error => error!.ToString().StartsWith("invalid-size", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_design_size_matches_the_layout_check()
    {
        var file = Path.Combine(directory, "unified_control_strip_1024x768.json");
        File.Copy(Path.Combine(RepositoryRoot, "templates/hmi/unified_control_strip_1024x768.json"), file);
        var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(file, 800, 480);
        var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(file, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds, 800, 480);
        Assert.Equal(1024, design["width"]!.GetValue<int>());
        Assert.Equal(768, design["height"]!.GetValue<int>());
        Assert.Equal(design["width"]!.ToString(), layout["width"]!.ToString());
        Assert.Equal(design["height"]!.ToString(), layout["height"]!.ToString());
        Assert.Equal("pass", layout["status"]!.ToString());
    }

    [Fact]
    public void Precheck_names_why_it_is_not_ok()
    {
        var templates = Path.Combine(directory, "templates");
        Directory.CreateDirectory(templates);
        var xml = Write("empty.xml", """
            <Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0">
            <AttributeList><Name>Empty</Name></AttributeList></SW.Tags.PlcTagTable></Document>
            """);
        var report = HmiTemplatePlcSyncPrecheckSuite.Run(templates, xml, Path.Combine(directory, "reports"));
        Assert.False(report["ok"]!.GetValue<bool>());
        var reasons = report["blockingReasons"]!.AsArray();
        var reason = Assert.Single(reasons)!.ToString();
        Assert.StartsWith("no-plc-symbols: " + xml, reason, StringComparison.Ordinal);
        Assert.Contains("no-plc-symbols-found", reason, StringComparison.Ordinal);
        Assert.True(report["selfTest"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Template_without_required_tags_is_not_ready()
    {
        Write("unified_plain.json", "{\"screen\":{\"BackColor\":\"0xFF000000\"},\"items\":[]}");
        var report = RunPrecheck();
        var template = Assert.Single(report["templates"]!.AsArray())!;
        Assert.True(report["ok"]!.GetValue<bool>());
        Assert.Equal("no-bindings", template["status"]!.ToString());
        Assert.Equal("no-bindings-to-check", template["gate"]!.ToString());
        Assert.False(string.IsNullOrWhiteSpace(template["note"]!.ToString()));
        Assert.Equal(0, report["readyTemplateCount"]!.GetValue<int>());
        Assert.Equal(1, report["noBindingTemplateCount"]!.GetValue<int>());
        Assert.Empty(report["blockingReasons"]!.AsArray());
    }

    [Fact]
    public void Precheck_reports_a_missing_template_directory_and_manifest_errors()
    {
        var missingTemplates = Path.Combine(directory, "missing-templates");
        var missingXml = Path.Combine(directory, "missing.xml");
        var report = HmiTemplatePlcSyncPrecheckSuite.Run(missingTemplates, missingXml, Path.Combine(directory, "reports"));
        Assert.False(report["ok"]!.GetValue<bool>());
        Assert.Contains(report["blockingReasons"]!.AsArray(), reason =>
            reason!.ToString() == "template-directory-missing: " + missingTemplates);
        Assert.Contains(report["blockingReasons"]!.AsArray(), reason =>
            reason!.ToString().StartsWith("no-plc-symbols: " + missingXml, StringComparison.Ordinal)
                && reason.ToString().Contains("xml-file-not-found", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_action_tags_still_block_a_template_without_declared_bindings()
    {
        var templates = JsonNode.Parse("""
            [{"templateName":"Motor","requiredTags":[],"missingRequiredTagsForActions":["Motor_Start"]}]
            """)!.AsArray();
        var result = HmiTemplatePlcSyncPrecheckSuite.BuildPrecheck(templates, new JsonObject());
        var template = Assert.Single(result)!;
        Assert.Equal("blocked", template["status"]!.ToString());
        Assert.Equal("do-not-apply", template["gate"]!.ToString());
    }

    [Theory]
    [InlineData("Motor_Run", "ready")]
    [InlineData("Motor_Missing", "blocked")]
    public void Template_with_a_required_tag_is_still_checked(string symbol, string expectedStatus)
    {
        Write("unified_motor.json", $$"""
            {"TemplateName":"Motor","RequiredTags":[{"Name":"Motor_Run","DataType":"Bool","PlcTag":"{{symbol}}"}],"Items":[]}
            """);
        var report = RunPrecheck();
        var template = Assert.Single(report["templates"]!.AsArray())!;
        Assert.Equal(expectedStatus, template["status"]!.ToString());
        Assert.Equal(expectedStatus, Assert.Single(template["bindingChecks"]!.AsArray())!["status"]!.ToString());
        Assert.Equal(expectedStatus == "ready" ? 1 : 0, report["readyTemplateCount"]!.GetValue<int>());
        Assert.Equal(expectedStatus == "blocked" ? 1 : 0, report["blockedTemplateCount"]!.GetValue<int>());
        Assert.True(report["selfTest"]!["ok"]!.GetValue<bool>());
    }

    private JsonObject RunPrecheck()
    {
        var xml = Write("tags.xml", """
            <Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0">
              <AttributeList><Name>MotorTags</Name></AttributeList><ObjectList>
                <SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList>
                  <DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Motor_Run</Name>
                </AttributeList></SW.Tags.PlcTag>
              </ObjectList>
            </SW.Tags.PlcTagTable></Document>
            """);
        return HmiTemplatePlcSyncPrecheckSuite.Run(directory, xml, Path.Combine(directory, "reports"));
    }

    private string Write(string name, string content)
    {
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, content);
        return file;
    }

    public void Dispose() => Directory.Delete(directory, true);
}
