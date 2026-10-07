using System;
using System.IO;
using System.Reflection;
using TiaMcp.Updater;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class UpdaterStagingTests
    {
        [Fact]
        public void Backup_and_rollback_preserve_staging_without_copying_it()
        {
            string root = Path.GetFullPath(Path.Combine("bin-build/P6-67r/updater-fixture", Guid.NewGuid().ToString("N")));
            string install = Path.Combine(root, "install"), backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(Path.Combine(install, "staging", "session"));
            File.WriteAllText(Path.Combine(install, "staging", "session", "A.scl"), "original staged content");
            File.WriteAllText(Path.Combine(install, "owned.txt"), "old owned content");
            var copy = typeof(UpdaterEngine).GetMethod("CopyTree", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                copy.Invoke(null, new object?[] { install, backup, true, null });
                Assert.False(Directory.Exists(Path.Combine(backup, "staging")));
                Assert.Equal("old owned content", File.ReadAllText(Path.Combine(backup, "owned.txt")));
                File.WriteAllText(Path.Combine(install, "staging", "session", "A.scl"), "new staged content");
                File.WriteAllText(Path.Combine(install, "owned.txt"), "new owned content");
                copy.Invoke(null, new object?[] { backup, install, true, null });
                Assert.Equal("new staged content", File.ReadAllText(Path.Combine(install, "staging", "session", "A.scl")));
                Assert.Equal("old owned content", File.ReadAllText(Path.Combine(install, "owned.txt")));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
