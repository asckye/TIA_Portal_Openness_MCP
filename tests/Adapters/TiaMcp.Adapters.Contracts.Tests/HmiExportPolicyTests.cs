using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class HmiExportPolicyTests
    {
        [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
        public void Served_unified_exchange_operations_satisfy_native_file_and_selection_rules(string release)
        {
            var catalog = new TiaMcp.Engine.Tests.Shared.ServedToolOperations(release);
            string fixture = Directory.CreateTempSubdirectory("unified-exchange-").FullName;
            try
            {
                foreach (string name in new[] { "ExchangeUnifiedTags", "ExchangeUnifiedScriptModules", "ImportUnifiedOpcUaAlarms" })
                {
                    var requests = catalog.For(name).ToArray();
                    Assert.NotEmpty(requests);
                    foreach (var (_, usage) in requests)
                    {
                        var args = usage["example"]!["request"]!["params"]!["arguments"]!;
                        string Text(string field) => (string?)args[field] ?? "";
                        string action = Text("action");
                        if (name == "ImportUnifiedOpcUaAlarms")
                        {
                            Assert.Contains(action, UnifiedExchangeLogic.OpcUaAlarmActions);
                            Assert.NotEmpty(Text("connectionName"));
                            if (action == "import") UnifiedExchangeLogic.ValidateXmlPath(Text("xmlPath"));
                            else Assert.Empty(Text("xmlPath"));
                            continue;
                        }
                        Assert.Contains(action, UnifiedExchangeLogic.ExchangeActions);
                        bool exporting = action == "export";
                        string path = Text(exporting ? "exportDirectory" : "sourceDirectory");
                        Assert.Empty(Text(exporting ? "sourceDirectory" : "exportDirectory"));
                        if (!exporting || path != "") Assert.True(Path.IsPathFullyQualified(path), name + "/" + action);
                        // Bind the example to a fresh export child or an existing import directory on this host.
                        UnifiedExchangeLogic.ValidateDirectory(exporting ? Path.Combine(fixture, "export") : fixture, exporting);
                        string file = UnifiedExchangeLogic.ValidateFileName(Text("fileName"));
                        if (name == "ExchangeUnifiedTags")
                        {
                            var expected = UnifiedExchangeLogic.ParseExpectedNames(args["expectedTagNames"]?.ToJsonString() ?? "[]");
                            if (exporting) Assert.Empty(expected);
                        }
                        else if (!exporting) Assert.Empty(Text("moduleName"));
                        else if (file != "") Assert.NotEmpty(Text("moduleName"));
                    }
                }
            }
            finally { Directory.Delete(fixture); }
        }

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
