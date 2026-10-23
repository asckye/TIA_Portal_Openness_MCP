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
