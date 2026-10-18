using TiaMcpServer;
using System;
using System.IO;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class PrivateWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "private-inputs", Guid.NewGuid().ToString("N"));
    public PrivateWorkspaceTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Workspace_is_explicit_even_when_the_cwd_has_private_markers()
    {
        Directory.CreateDirectory(Path.Combine(root, "TMP_EXPORT"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var options = new CliOptions { RunPlcBuilderOfflineSuite = true };
        Assert.Contains("INVALID_ARGUMENT", Assert.Throws<ArgumentException>(options.ValidatePrivateInputs).Message);
        options.WorkspaceRoot = root; options.ValidatePrivateInputs();
        Assert.Equal(root, options.RequireWorkspaceRoot());
    }

    [Fact]
    public void Workspace_option_rejects_missing_relative_and_duplicate_values()
    {
        foreach (var args in new[] { new[] { "--workspace-root" }, new[] { "--workspace-root", "relative" }, new[] { "--workspace-root", root, "--workspace-root", root } })
            Assert.Contains("INVALID_ARGUMENT", Assert.Throws<ArgumentException>(() => CliOptions.ParseArgs(args)).Message);
        Assert.Equal(root, CliOptions.ParseArgs(new[] { "--workspace-root", root }).WorkspaceRoot);
    }

    [Fact]
    public void Template_input_is_required_separately_from_the_workspace()
    {
        var options = new CliOptions { WorkspaceRoot = root, ValidateUnifiedHmiTemplates = true };
        Assert.Contains("--hmi-template-directory", Assert.Throws<ArgumentException>(options.ValidatePrivateInputs).Message);
        options.HmiTemplateDirectory = root; options.ValidatePrivateInputs();
    }

    [Fact]
    public void Builder_rejects_missing_workspace_or_fixture_before_writing_reports()
    {
        string reports = Path.Combine(root, "reports");
        Assert.Contains("workspaceRoot", Assert.Throws<ArgumentException>(() => PlcBuilderOfflineValidationSuite.Run(root, reports, "")).Message);
        Assert.Contains("fixtureDirectory", Assert.Throws<ArgumentException>(() => PlcBuilderOfflineValidationSuite.Run("", reports, root)).Message);
        Assert.False(Directory.Exists(reports));
    }
    public void Dispose() => Directory.Delete(root, true);
}
