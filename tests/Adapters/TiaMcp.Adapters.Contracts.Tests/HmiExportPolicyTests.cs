using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class HmiExportPolicyTests
    {
        [Theory]
        [InlineData("None")]
        [InlineData("WithDefaults")]
        [InlineData("WithReadOnly")]
        public void Explicit_HMI_options_reach_the_native_overload_and_the_file_is_committed(string option)
        {
            var target = new ExportTarget();
            string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            try
            {
                var evidence = EngineeringFileExport.Export(target, file, false, error => throw error, option);
                Assert.Equal(true, evidence["success"]);
                Assert.Equal(option, target.Option);
                Assert.Equal(option, File.ReadAllText(file));
                Assert.False(target.OneArgumentCalled);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Non_HMI_callers_keep_their_existing_overload_selection()
        {
            var target = new ExportTarget();
            string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            try
            {
                var evidence = EngineeringFileExport.Export(target, file, false, error => throw error);
                Assert.Equal(true, evidence["success"]);
                Assert.True(target.OneArgumentCalled);
                Assert.Equal("one-argument", File.ReadAllText(file));
            }
            finally { File.Delete(file); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Export_start_is_reported_only_after_directory_preparation(bool failDirectory)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "export-start-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string parent = Path.Combine(root, "target");
            if (failDirectory) File.WriteAllText(parent, "directory blocker");
            var target = new ExportTarget(); bool started = false; Exception? failure = null;
            try
            {
                var evidence = EngineeringFileExport.Export(target, Path.Combine(parent, "item.xml"), false, error => failure = error,
                    exportStarting: () => { Assert.False(target.OneArgumentCalled); started = true; });
                Assert.Equal(!failDirectory, started);
                Assert.Equal(!failDirectory, target.OneArgumentCalled);
                Assert.Equal(!failDirectory, evidence["success"]);
                if (failDirectory) Assert.IsAssignableFrom<IOException>(failure);
                else Assert.Null(failure);
            }
            finally { Directory.Delete(root, true); }
        }

        public sealed class ExportTarget
        {
            public bool OneArgumentCalled { get; private set; }
            public string? Option { get; private set; }
            public void Export(FileInfo file) { OneArgumentCalled = true; File.WriteAllText(file.FullName, "one-argument"); }
            public void Export(FileInfo file, Siemens.Engineering.ExportOptions options)
            {
                Option = options.ToString();
                File.WriteAllText(file.FullName, Option);
            }
        }
    }
}

namespace Siemens.Engineering
{
    public enum ExportOptions { None, WithDefaults, WithReadOnly }
}
