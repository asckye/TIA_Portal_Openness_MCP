using System;
using System.Collections.Generic;
using System.IO;
using TiaMcp.Adapters.Native.Vci;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class VciConnectTests
{
    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("empty", false)]
    [InlineData("new-target", true)]
    [InlineData("changed-target", true)]
    [InlineData("timestamp", true)]
    [InlineData("new-unrelated", true)]
    [InlineData("existing-unrelated", true)]
    [InlineData("subfolder-added-file", true)]
    [InlineData("subfolder-changed-file", true)]
    [InlineData("subfolder-timestamp", true)]
    [InlineData("missing-before", true)]
    [InlineData("missing-after", true)]
    [InlineData("find-throws", true)]
    [InlineData("over-limit", true)]
    public void Modern_export_requires_an_absent_mapping_and_two_unchanged_snapshots(string scenario, bool unknown)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vci-" + Guid.NewGuid().ToString("N")));
        try
        {
            const string stem = "PLC.Line.Motor";
            string folder = Path.Combine(workspace.FullName, "Blocks");
            string resource = Path.Combine(folder, stem + ".en-US.s7res");
            if (scenario != "missing-before") Directory.CreateDirectory(folder);
            if (scenario is not ("empty" or "missing-before"))
            {
                string examples = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "reference", "tool-examples", "languages");
                File.Copy(Path.Combine(examples, "FB_LadStart.s7dcl"), Path.Combine(folder, stem + ".s7dcl"));
                File.Copy(Path.Combine(examples, "FB_LadStart.s7res"), resource);
            }
            if (scenario == "over-limit")
                for (int i = 0; i < 10000; i++) File.WriteAllText(Path.Combine(folder, i + ".xml"), "<Document/>");
            string unrelated = Path.Combine(folder, "OtherBlock.xml");
            if (scenario == "existing-unrelated") File.WriteAllText(unrelated, "<Document/>");
            string subfolder = Path.Combine(folder, "OtherBlocks");
            string nestedFile = Path.Combine(subfolder, "Block.xml");
            if (scenario.StartsWith("subfolder-", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(subfolder);
                File.WriteAllText(nestedFile, "<Document/>");
            }
            void Write()
            {
                switch (scenario)
                {
                    case "new-target": File.Create(Path.Combine(folder, stem + ".liblink")).Dispose(); break;
                    case "changed-target": File.AppendAllText(resource, "\n"); break;
                    case "timestamp": File.SetLastWriteTimeUtc(resource, File.GetLastWriteTimeUtc(resource).AddMinutes(1)); break;
                    case "new-unrelated": File.WriteAllText(Path.Combine(folder, "unrecognized.aux"), "companion"); break;
                    case "existing-unrelated": File.AppendAllText(unrelated, "\n"); break;
                    case "subfolder-added-file": File.WriteAllText(Path.Combine(subfolder, "New.xml"), "<Document/>"); break;
                    case "subfolder-changed-file":
                        var directoryTime = Directory.GetLastWriteTimeUtc(subfolder);
                        File.AppendAllText(nestedFile, "\n");
                        Directory.SetLastWriteTimeUtc(subfolder, directoryTime);
                        break;
                    case "subfolder-timestamp":
                        Directory.SetLastWriteTimeUtc(subfolder, Directory.GetLastWriteTimeUtc(subfolder).AddMinutes(1));
                        break;
                    case "missing-before": Directory.CreateDirectory(folder); break;
                    case "missing-after": Directory.Move(folder, Path.Combine(workspace.FullName, "moved")); break;
                }
                throw new IOException("MCP_RC_FC [FC1] object(s) is not consistent");
            }
            var attempts = new VciExportAttempts();
            var items = new List<string>();
            int finds = 0;
            Assert.False(attempts.ExportToWorkspace(folder, stem, Write, () =>
            {
                finds++;
                return scenario == "find-throws" ? throw new IOException("Find refused") : false;
            }, items.Add));
            Assert.Equal(1, finds);
            Assert.Equal(1, attempts.Issued);
            Assert.Equal(unknown ? 1 : 0, attempts.Unconfirmed);
            Assert.Equal(0, attempts.MappedAfterFailure);
            Assert.Equal(scenario == "find-throws", attempts.MappingProbeFailed);
            Assert.Contains("FAILED: MCP_RC_FC [FC1] object(s) is not consistent", items);
        }
        finally { workspace.Delete(true); }
    }

    [Fact]
    public void Disposed_file_first_mapping_stops_before_lookup()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var file = new FileInfo(Path.Combine(root.FullName, "Motor.xml"));
            var attempts = new VciExportAttempts();
            int lookups = 0;
            Assert.False(attempts.ExportFileFirst(file, () => File.WriteAllText(file.FullName, "<Document/>"),
                () => throw new ObjectDisposedException("Motor"), () => { lookups++; return true; }, _ => { }));
            Assert.Equal(0, lookups);
            Assert.True(attempts.MappingProbeFailed);
            Assert.Equal(1, attempts.Unconfirmed);
            Assert.Equal(1, attempts.FilesWritten);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Disposed_export_still_looks_up_the_mapping_after_an_ordinary_target()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var attempts = new VciExportAttempts();
            var lines = new List<string>();
            Assert.False(attempts.ExportToWorkspace(root.FullName, "Motor", () => throw new ObjectDisposedException("Motor"), () => true, lines.Add));
            Assert.Equal(1, attempts.MappedAfterFailure);
            Assert.False(attempts.MappingProbeFailed);
            Assert.Equal(0, attempts.Unconfirmed);
        }
        finally { root.Delete(true); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Missing_legacy_xml_checks_the_whole_workspace(bool wroteElsewhere)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var blocks = root.CreateSubdirectory("Blocks");
            void Write()
            {
                if (wroteElsewhere) File.WriteAllText(Path.Combine(blocks.FullName, "Motor.xml"), "<Document/>");
                throw new IOException("VCI refused the export");
            }
            var attempts = new VciExportAttempts();
            Assert.False(attempts.ExportToWorkspace(root.FullName, "Motor", Write, () => false, _ => { }));
            Assert.Equal(wroteElsewhere ? 1 : 0, attempts.Unconfirmed);
        }
        finally { root.Delete(true); }
    }
}
