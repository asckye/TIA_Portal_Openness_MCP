using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class GenerationArtifactTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tiagen-artifacts-" + Guid.NewGuid().ToString("N"));
        private readonly string id = "gen-" + new string('a', 64);
        private string DirectoryPath => Path.Combine(root, "artifacts", "staging", id);
        private string FilePath => Path.Combine(DirectoryPath, "source_" + new string('b', 16) + "_" + new string('c', 16) + ".scl");
        public GenerationArtifactTests() { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(FilePath, "FUNCTION_BLOCK FB_A\nEND_FUNCTION_BLOCK\n"); }
        private GenerationArtifacts Store => new GenerationArtifacts(root);
        private string Reference(bool ended)
        {
            var store = new GenerationRunStore(Path.Combine(root, "runs"));
            var run = store.Begin(new JsonObject { ["steps"] = new JsonArray(), ["plan"] = new JsonObject {
                ["artifacts"] = new JsonArray(new JsonObject { ["path"] = "staging/" + id + "/" + Path.GetFileName(FilePath) }) } });
            if (ended) store.Append(run, new JsonObject { ["event"] = "end", ["status"] = "partial" });
            return run;
        }
        [Fact]
        public void PreviewDoesNotDeleteAndCleanupRetainsRunLogsClaimsAndBackups()
        {
            string run = Reference(true), backup = Path.Combine(root, "backup.zap21"), claim = Path.Combine(root, "runs", id + ".consumed");
            File.WriteAllText(backup, "backup"); File.WriteAllText(claim, "consumed");
            var preview = Store.Manage("cleanup", id, true, ""); Assert.True(File.Exists(FilePath));
            Assert.True((bool)Store.Manage("cleanup", id, false, (string)preview["planHash"]!)["executed"]!);
            Assert.False(Directory.Exists(DirectoryPath)); Assert.True(File.Exists(backup)); Assert.True(File.Exists(claim));
            Assert.Equal("partial", (string)new GenerationRunStore(Path.Combine(root, "runs")).Read(run)["status"]!);
        }
        [Fact]
        public void ChangedArtifactRefusesThePreviouslyApprovedHash()
        {
            var preview = Store.Manage("cleanup", id, true, ""); File.AppendAllText(FilePath, "changed");
            Assert.Throws<ArgumentException>(() => Store.Manage("cleanup", id, false, (string)preview["planHash"]!)); Assert.True(File.Exists(FilePath));
        }
        [Fact]
        public void InterruptedRunReferencesPreventExplicitAndAutomaticCleanup()
        {
            Reference(false); File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddDays(-3)); Directory.SetLastWriteTimeUtc(DirectoryPath, DateTime.UtcNow.AddDays(-3));
            Assert.Throws<ArgumentException>(() => Store.Manage("cleanup", id, true, "")); Store.ExpireUnreferenced(DateTimeOffset.UtcNow); Assert.True(File.Exists(FilePath));
        }
        [Fact]
        public void CompletedRunReferencesRequireExplicitCleanupAfterRetentionDeadline()
        {
            Reference(true); File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddDays(-3)); Directory.SetLastWriteTimeUtc(DirectoryPath, DateTime.UtcNow.AddDays(-3));
            Store.ExpireUnreferenced(DateTimeOffset.UtcNow); Assert.True(File.Exists(FilePath));
        }
        [Fact]
        public void OnlyExpiredUnreferencedStagingIsAutomaticallyRemoved()
        {
            Store.ExpireUnreferenced(DateTimeOffset.UtcNow); Assert.True(File.Exists(FilePath));
            File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddDays(-3)); Directory.SetLastWriteTimeUtc(DirectoryPath, DateTime.UtcNow.AddDays(-3));
            Store.ExpireUnreferenced(DateTimeOffset.UtcNow); Assert.False(Directory.Exists(DirectoryPath));
        }
        [Fact]
        public void UnexpectedFilesAndTraversalIdsAreRetainedAndRefused()
        {
            File.WriteAllText(Path.Combine(DirectoryPath, "user.scl"), "user");
            Assert.Throws<IOException>(() => Store.Manage("list", "", true, ""));
            Assert.Throws<ArgumentException>(() => Store.Manage("cleanup", "../runs", false, "")); Assert.True(File.Exists(FilePath));
        }
        [Fact]
        public void ReparseDirectoryRefusesRecordPublicationAndArtifactEnumeration()
        {
            string link = Path.Combine(root, "artifacts", "staging", "gen-" + new string('d', 64)), target = DirectoryPath;
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
                using var process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.True(process.ExitCode == 0, output);
            }
            else Directory.CreateSymbolicLink(link, target);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "fixture link is not a reparse point");
            try
            {
                Assert.Throws<IOException>(() => Store.Manage("list", "", true, ""));
                Assert.Throws<IOException>(() => GenerationRecordFile.Read(Path.Combine(link, "Line.tiagen.json")));
                var record = new GenerationRecord { Project = new GenerationRecordProject { File = "C:/Line/Line.ap21", Release = "21" } }; record.RecordHash = GenerationRecord.Hash(record);
                Assert.Throws<IOException>(() => GenerationRecordFile.Write(Path.Combine(link, "Line.tiagen.json"), record, "absent")); Assert.True(File.Exists(FilePath));
            }
            finally { Directory.Delete(link, false); }
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
