using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Mock;
using Xunit;

namespace TiaOpenness.Core.Tests;

/// <summary>
/// The mock session is not a test double bolted on from outside - it is the backend the app
/// ships with for --mock, and the CLI, the desktop app and the MCP server were all developed
/// against it. So its contract is worth holding: refusing work before a project is open, and
/// reporting rather than hiding the block that deliberately cannot be exported.
/// </summary>
public class MockTiaSessionTests : IDisposable
{
    private readonly MockTiaSession _session = new();
    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "tia-mock-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _session.Dispose();
        if (Directory.Exists(_outputDirectory)) Directory.Delete(_outputDirectory, recursive: true);
    }

    private MockTiaSession Opened()
    {
        _session.Connect(withUserInterface: false, attachToRunning: false, version: null);
        _session.OpenProject(@"D:\demo\Line.ap21");
        return _session;
    }

    [Fact]
    public void Starts_disconnected_and_without_a_project()
    {
        Assert.Equal(SessionMode.Mock, _session.Mode);
        Assert.False(_session.IsConnected);
        Assert.False(_session.HasProject);
    }

    [Fact]
    public void Listing_devices_before_a_project_is_open_is_an_error_rather_than_an_empty_list()
    {
        _session.Connect(withUserInterface: false, attachToRunning: false, version: null);

        // An empty list would read as "this project has no devices", which is a different fact.
        Assert.ThrowsAny<InvalidOperationException>(() => _session.ListDevices());
    }

    [Fact]
    public void Opening_a_project_exposes_a_plc_and_an_hmi()
    {
        var devices = Opened().ListDevices();

        Assert.True(_session.HasProject);
        Assert.Contains(devices, d => d.Category == "Plc");
        Assert.Contains(devices, d => d.Category == "Hmi");
    }

    [Fact]
    public void The_synthetic_plc_carries_one_protected_and_one_inconsistent_block()
    {
        var blocks = Opened().ListBlocks("PLC_1", includeSystemBlocks: false);

        // Both states exist on purpose: they are the two reasons TIA itself refuses an export,
        // and the UI has to have something to show them with.
        Assert.Single(blocks, b => b.IsKnowHowProtected);
        Assert.Single(blocks, b => !b.IsConsistent);
    }

    [Fact]
    public void Export_writes_real_files_and_reports_the_block_it_could_not_write()
    {
        var session = Opened();
        var blocks = session.ListBlocks("PLC_1", includeSystemBlocks: false);

        var result = session.ExportBlocks(
            "PLC_1", blocks.Select(b => b.Path).ToList(), _outputDirectory,
            ExportFormat.SimaticMl, preserveFolders: true, progress: null);

        Assert.Equal(blocks.Count, result.Requested);
        Assert.True(result.Succeeded > 0);

        // Know-how protection is the one thing TIA itself refuses, so it has to surface as a
        // reported failure with a reason rather than as a block that quietly went missing.
        var failure = Assert.Single(result.Items, i => !i.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));
        Assert.Equal(1, result.Failed);

        Assert.True(Directory.Exists(_outputDirectory));
        Assert.Equal(result.Succeeded, Directory.GetFiles(_outputDirectory, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Export_mirrors_the_tia_folder_structure_when_asked_to()
    {
        var session = Opened();

        session.ExportBlocks("PLC_1", new[] { "Motion/FB_Axis" }, _outputDirectory,
            ExportFormat.Source, preserveFolders: true, progress: null);

        Assert.True(File.Exists(Path.Combine(_outputDirectory, "Motion", "FB_Axis.scl")));
    }

    [Fact]
    public void Export_with_no_paths_takes_every_block()
    {
        var session = Opened();
        var all = session.ListBlocks("PLC_1", includeSystemBlocks: false);

        var result = session.ExportBlocks("PLC_1", Array.Empty<string>(), _outputDirectory,
            ExportFormat.Source, preserveFolders: false, progress: null);

        Assert.Equal(all.Count, result.Requested);
    }

    /// <summary>
    /// A typo in a block path must stop the export, not silently export a shorter list - the
    /// caller asked for those blocks and would otherwise commit an incomplete snapshot.
    /// </summary>
    [Fact]
    public void Export_rejects_a_block_path_that_does_not_exist()
    {
        var session = Opened();

        Assert.Throws<KeyNotFoundException>(() => session.ExportBlocks(
            "PLC_1", new[] { "Motion/FB_Axis", "Nope/FC_Missing" }, _outputDirectory,
            ExportFormat.Source, preserveFolders: false, progress: null));
    }

    [Fact]
    public void Compiling_reports_the_inconsistent_block_as_an_error()
    {
        var result = Opened().CompileDevice("PLC_1", softwareOnly: true);

        Assert.True(result.ErrorCount > 0);
        Assert.NotEmpty(result.Messages);
    }

    [Fact]
    public void Compiling_makes_every_block_consistent_so_a_second_export_succeeds()
    {
        var session = Opened();
        session.CompileDevice("PLC_1", softwareOnly: true);

        var blocks = session.ListBlocks("PLC_1", includeSystemBlocks: false);

        Assert.DoesNotContain(blocks, b => !b.IsConsistent);
    }

    [Fact]
    public void Inspecting_runs_the_same_engine_the_real_backend_does()
    {
        var report = Opened().Inspect("PLC_1", new InspectionOptions
        {
            BlockNamePattern = "^(OB|FB|FC|DB|UDT)_",
            RequireBlockComment = false,
            FindUnusedBlocks = false,
            FlagInconsistentBlocks = true,
        });

        Assert.Equal("PLC_1", report.DeviceId);
        Assert.Contains(report.Findings, f => f.RuleId == "BUILD-001");
        Assert.Contains(report.Findings, f => f.RuleId == "PROT-001");
    }

    [Fact]
    public void An_unknown_device_is_rejected_rather_than_returning_nothing()
    {
        var session = Opened();

        Assert.ThrowsAny<Exception>(() => session.ListBlocks("NOT_A_DEVICE", includeSystemBlocks: false));
    }

    [Fact]
    public void Closing_the_project_clears_it()
    {
        var session = Opened();
        session.CloseProject();

        Assert.False(session.HasProject);
    }
}
