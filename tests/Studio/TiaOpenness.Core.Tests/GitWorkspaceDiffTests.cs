using System;
using System.Diagnostics;
using System.IO;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Inspection;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class GitWorkspaceDiffTests
{
    [Fact]
    public void Tracked_block_edit_reports_added_and_removed_lines()
    {
        var root = CreateRepository();
        var file = Path.Combine(root, "FB_Axis.s7dcl");
        File.WriteAllText(file, "old block body\n");
        Git(root, "add", "FB_Axis.s7dcl");
        File.WriteAllText(file, "new block body\n");
        var diff = GitWorkspaceDiff.Read("test", root, "FB_Axis");
        Assert.True(diff.Available, diff.Detail);
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "-old block body");
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "+new block body");
    }

    [Fact]
    public void New_mapped_block_without_extension_resolves_actual_unicode_filename()
    {
        var root = CreateRepository();
        File.WriteAllText(Path.Combine(root, "FB 轴.s7dcl"), "new mapped source\n");
        File.WriteAllText(Path.Combine(root, "unrelated.s7dcl"), "unrelated source\n");
        var diff = GitWorkspaceDiff.Read("test", root, Path.Combine(root, "FB 轴"));
        Assert.True(diff.Available, diff.Detail);
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "+new mapped source");
        Assert.DoesNotContain(diff.Lines, l => l.Text == "+unrelated source");
    }

    [Fact]
    public void Whole_workspace_shows_new_files_alongside_tracked_changes()
    {
        var root = CreateRepository();
        File.WriteAllText(Path.Combine(root, "existing.s7dcl"), "before\n");
        Git(root, "add", "existing.s7dcl");
        File.WriteAllText(Path.Combine(root, "existing.s7dcl"), "after\n");
        File.WriteAllText(Path.Combine(root, "new.s7dcl"), "new source\n");
        var diff = GitWorkspaceDiff.Read("test", root, null);
        Assert.True(diff.Available, diff.Detail);
        Assert.Contains(diff.Lines, l => l.Text == "+after");
        Assert.Contains(diff.Lines, l => l.Text == "+new source");
    }

    [Fact]
    public void Staged_only_block_is_visible_before_the_first_commit()
    {
        var root = CreateRepository();
        File.WriteAllText(Path.Combine(root, "Staged.scl"), "staged source body\n");
        Git(root, "add", "Staged.scl");
        var diff = GitWorkspaceDiff.Read("test", root, "Staged");
        Assert.True(diff.Available, diff.Detail);
        Assert.Contains(diff.Lines, l => l.Text == "+staged source body");
    }

    [Fact]
    public void Relative_workspace_path_still_reads_the_real_repository()
    {
        var root = CreateRepository();
        File.WriteAllText(Path.Combine(root, "Relative.scl"), "relative workspace source\n");
        var relative = Path.GetRelativePath(System.Environment.CurrentDirectory, root);
        var diff = GitWorkspaceDiff.Read("test", relative, null);
        Assert.True(diff.Available, diff.Detail);
        Assert.Contains(diff.Lines, line => line.Text == "+relative workspace source");
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Git(root, "init", "--quiet");
        return root;
    }

    private static void Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Temporary repository setup timed out.");
        System.Threading.Tasks.Task.WaitAll(output, errors);
        Assert.True(process.ExitCode == 0, errors.Result);
    }
}
