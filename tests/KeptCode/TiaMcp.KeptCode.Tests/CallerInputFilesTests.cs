using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class CallerInputFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tia-cif-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public CallerInputFilesTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, true);

        [Theory]
        [InlineData("ImportHmiScreensFromDirectory", "dir")]
        [InlineData("ImportHmiTagTablesFromDirectory", "dir")]
        [InlineData("ImportTechnologyObjectsFromDirectory", "dir")]
        [InlineData("ScanPlcSourceAnnotations", "directory")]
        public void Missing_directory_is_named_during_validation_and_snapshot(string tool, string parameter)
        {
            var args = new JsonObject { [parameter] = Path.Combine(root, "missing") };
            foreach (Action call in new Action[] { () => TiaMcp.Logic.V4.CallerInputFiles.Validate(tool, args),
                () => TiaMcp.Logic.V4.CallerInputFiles.Snapshot(tool, args) })
            {
                var error = Assert.Throws<AdapterPreconditionException>(call);
                Assert.Equal(parameter, error.ParamName);
                Assert.Contains("input directory does not exist", error.Message);
            }
        }

        [Theory]
        [InlineData("OpenProject", "path")]
        [InlineData("ConnectProject", "projectPath")]
        [InlineData("OpenLocalSession", "localSessionPath")]
        [InlineData("SaveProject", "expectedProjectFile")]
        [InlineData("ManagePlcGitRepository", "repositoryPath")]
        [InlineData("RunPlcCompanionTool", "workingDirectory")]
        [InlineData("PublishGenerationRecord", "recordPath")]
        [InlineData("CloseProject", "secondaryProjectFile")]
        [InlineData("SaveExportContent", "exportFile")]
        public void Network_identity_and_output_paths_pass_input_admission(string tool, string parameter)
        {
            var args = new JsonObject { [parameter] = @"\\server\share\project" };
            TiaMcp.Logic.V4.CallerInputFiles.RequireLocalInputs(tool, args);
        }

        [Theory]
        [InlineData("AnalyzePlcSclSource", "filePath")]
        [InlineData("ImportTechnologyObjectsFromDirectory", "dir")]
        [InlineData("SeedProjectFromReference", "referenceDir")]
        [InlineData("AnalyzeGlobalLibraryPackage", "libraryPath")]
        [InlineData("RetrieveProjectArchive", "archivePath")]
        [InlineData("ImportHmiTagTable", "importPath")]
        [InlineData("ImportPlcProgram", "files")]
        [InlineData("ImportSinumerikAlarmTexts", "files")]
        [InlineData("BuildPlcSymbolManifestFromPath", "inputRoot")]
        [InlineData("RunClassicHmiTemporaryImportPreflight", "workspaceRoot")]
        [InlineData("RunOfflineReleaseValidationSuite", "workspaceRoot")]
        [InlineData("ValidateStandardPackage", "sourcePath")]
        [InlineData("AnalyzePlcReferences", "sourceDirectory")]
        public void Admission_and_snapshot_refuse_network_and_device_inputs(string tool, string parameter)
        {
            foreach (string path in new[] { @"\\localhost\fixture\input.xml", "//localhost/fixture/input.xml",
                @"\\?\UNC\localhost\fixture", @"\\.\NUL", @"/??\NUL" })
            {
                var args = new JsonObject { [parameter] = parameter == "files" ? new JsonArray(path) : JsonValue.Create(path) };
                void Check(Action call)
                {
                    var refusal = Assert.Throws<AdapterPreconditionException>(call);
                    Assert.True(refusal.IsArgument);
                    Assert.Equal(parameter, refusal.ParamName);
                    Assert.Contains("Network and device", refusal.Message);
                }
                Check(() => TiaMcp.Logic.V4.CallerInputFiles.Validate(tool, args));
                Check(() => TiaMcp.Logic.V4.CallerInputFiles.Snapshot(tool, args));
            }
        }

        [Theory]
        [InlineData("ManagePlcDataBlockSnapshot", "exportSnapshot")]
        [InlineData("ManageDriveSafetyAcceptanceTest", "createProtocol")]
        public void Snapshot_and_protocol_outputs_are_absent_from_approval_inputs(string tool, string action)
        {
            var args = new JsonObject { ["action"] = action, ["filePath"] = @"\\server\share\output.xml" };
            TiaMcp.Logic.V4.CallerInputFiles.RequireLocalInputs(tool, args);
            args["filePath"] = Path.Combine(root, "output.xml");
            Assert.Empty(TiaMcp.Logic.V4.CallerInputFiles.Snapshot(tool, args));
        }

        [Theory]
        [InlineData("ExchangeCfcCharts", "filePath")]
        [InlineData("ExchangeUnifiedTags", "directory")]
        [InlineData("ExchangeUnifiedScriptModules", "fileName")]
        [InlineData("ManageStandardPackage", "sourcePath")]
        [InlineData("ManageMachineDescription", "inputPath")]
        public void Direction_of_exchange_controls_network_input_admission(string tool, string parameter)
        {
            var args = new JsonObject { ["action"] = "import", [parameter] = @"\\server\share\fixture" };
            var error = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.RequireLocalInputs(tool, args));
            Assert.Equal(parameter, error.ParamName);
            args["action"] = "export";
            TiaMcp.Logic.V4.CallerInputFiles.RequireLocalInputs(tool, args);
        }

        [Theory]
        [InlineData("CallTool")]
        [InlineData("PreviewToolCall")]
        [InlineData("RunReadOnlyToolBatch")]
        [InlineData("PreviewToolBatch")]
        public void Forwarded_input_is_refused_before_the_outer_administrative_path_check(string tool)
        {
            var operation = new JsonObject { ["name"] = "AnalyzePlcSclSource",
                ["arguments"] = new JsonObject { ["filePath"] = @"\\.\NUL" } };
            var args = tool.EndsWith("Batch", StringComparison.Ordinal)
                ? new JsonObject { ["operations"] = new JsonArray(operation) } : operation;
            var error = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.RequireLocalInputs(tool, args));
            Assert.Equal("filePath", error.ParamName);
        }

        [Fact]
        public void Drive_letter_admission_does_not_probe_the_drive_or_require_a_mapping()
            => TiaOpenness.Shared.NativeInputPolicy.RequireLocal(@"Z:\offline-fixture\input.xml", "filePath");

        [Fact]
        public void Directory_and_filename_and_seed_manifest_paths_are_checked_before_enumeration()
        {
            var pair = new JsonObject { ["directory"] = root, ["fileName"] = @"\\.\NUL", ["action"] = "import" };
            var file = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.Snapshot("ExchangeUnifiedTags", pair));
            Assert.Equal("fileName", file.ParamName);
            File.WriteAllText(Path.Combine(root, "manifest.json"), new JsonObject { ["plcTypesDir"] = @"\\.\NUL" }.ToJsonString());
            var dir = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.SeedReferenceLayout.Read(root, "21", null));
            Assert.Equal("referenceDir", dir.ParamName);
            Assert.True(dir.IsArgument);
        }

        [Fact]
        public void Native_input_checks_refuse_device_paths_and_identify_the_parameter()
        {
            const string path = @"\\.\NUL", parameter = "inputFile";
            void Check(Action call)
            {
                var refusal = Assert.Throws<AdapterPreconditionException>(call);
                Assert.True(refusal.IsArgument);
                Assert.Equal(parameter, refusal.ParamName);
                Assert.Contains("Network and device", refusal.Message);
            }
            Check(() => TiaOpenness.Shared.NativeInputPolicy.RequireReadable(path, parameter, false));
            Check(() => TiaOpenness.Shared.NativeInputPolicy.RequireReadable(path, parameter, true));
            Check(() => TiaOpenness.Shared.NativeInputPolicy.OpenRead(path, parameter).Dispose());
        }

        [Fact]
        public void Seed_directory_overrides_must_stay_within_referenceDir()
        {
            File.WriteAllText(Path.Combine(root, "manifest.json"), """{"plcTypesDir":"../donors"}""");
            var error = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.SeedReferenceLayout.Read(root, "21", null));
            Assert.False(error.IsArgument);
            Assert.Equal("referenceDir", error.ParamName);
            Assert.Contains("must stay inside referenceDir", error.Message);
        }

        [Fact]
        public void Missing_seed_reference_directory_reports_the_expected_layout_at_file_admission()
        {
            var error = Assert.Throws<AdapterPreconditionException>(() =>
                McpServer.ValidateCallerInputFiles("SeedProjectFromReference", Arguments("referenceDir", Path.Combine(root, "missing"))));
            Assert.Equal("referenceDir", error.ParamName);
            Assert.Equal("seed-reference-layout", error.Condition);
            Assert.Contains("manifest.json", error.Message);
            Assert.Contains("hmi/screens", error.Message);
        }

        [Fact]
        public void Hmi_import_reads_Unicode_filename_and_explains_an_escaped_tab()
        {
            const string fileName = "tagtable__默认变量表.xml";
            var path = Path.Combine(root, fileName);
            File.WriteAllText(path, "<Document/>");
            McpServer.ValidateCallerInputFiles("ImportHmiTagTable", Arguments("importPath", path));
            var error = Assert.Throws<AdapterPreconditionException>(() =>
                McpServer.ValidateCallerInputFiles("ImportHmiTagTable", Arguments("importPath", root + "\tagtable__默认变量表.xml")));
            Assert.Equal("importPath", error.ParamName);
            Assert.True(error.IsArgument);
            var refusal = McpServer.TargetFailure("ImportHmiTagTable", error, issued: false);
            Assert.Equal("INVALID_ARGUMENT", (string?)refusal.StructuredContent?["error"]?["code"]);
            Assert.Contains("control characters", error.Message);
        }

        [Fact]
        public void Alarm_readback_compares_worksheet_data_and_ignores_package_timestamps()
        {
            var repo = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            var source = Path.Combine(repo, "tests", "FoundationHost", "TiaMcp.ToolPipeline.Tests", "Fixtures", "TableExchange", "inline-text.xlsx");
            var file = Path.Combine(root, "alarm-readback.xlsx");
            File.Copy(source, file);
            var before = TiaMcp.Adapters.Native.Plc.AlarmWorkbookReadback.Parts(file);
            Assert.NotNull(before);
            using (var archive = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Update))
                archive.GetEntry("xl/worksheets/sheet1.xml")!.LastWriteTime = DateTimeOffset.UtcNow.AddYears(-1);
            Assert.Equal(before, TiaMcp.Adapters.Native.Plc.AlarmWorkbookReadback.Parts(file));
            using (var archive = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
                string xml;
                using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
                writer.Write(xml.Replace("</t>", " changed</t>"));
            }
            Assert.NotEqual(before, TiaMcp.Adapters.Native.Plc.AlarmWorkbookReadback.Parts(file));
        }

        [Theory]
        [InlineData("SW.Tags.PlcTagTable")]
        [InlineData("SW.Blocks.FC")]
        public void Technology_import_hashing_refuses_another_native_document_kind(string element)
        {
            string file = Path.Combine(root, "MCP_B5_Input.xml");
            File.WriteAllText(file, "<Document><" + element + " ID='0'><AttributeList><Name>FC_A</Name></AttributeList></" + element + "></Document>");
            var error = Assert.Throws<AdapterPreconditionException>(() =>
                TiaMcp.Logic.V4.CallerInputFiles.Snapshot("ImportTechnologyObject", new JsonObject { ["importPath"] = file }));
            Assert.True(error.IsArgument);
            Assert.Equal("importPath", error.ParamName);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("a/b", true)]
        [InlineData("a/../b", true)]
        [InlineData("../x", false)]
        [InlineData("a/../../x", false)]
        public void Output_root_normalizes_relative_segments(string relative, bool inside)
        {
            Assert.Equal(inside, TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(Path.Combine(root, relative), root));
            Assert.True(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(root.ToUpperInvariant() + "/a", root));
            Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(root + "-evil/a", root));
            Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(root, null));
        }

        [Theory]
        [InlineData(@"\\127.0.0.1\share\x")]
        [InlineData(@"\\localhost\c$\x")]
        [InlineData(@"\\.\pipe\x")]
        public void Network_and_device_targets_are_outside(string target)
        {
            Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(target, root));
            Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(target, target));
            Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(@"\\?\" + root + @"\x", root));
        }

        [Fact]
        public void Missing_input_keeps_preview_refusal_and_tells_an_apply_to_preview_again()
        {
            string input = Path.Combine(root, "Missing.xml");
            var preview = Assert.Throws<AdapterPreconditionException>(() => McpServer.ValidateCallerInputFiles("ImportHmiScreen",
                new JsonObject { ["importPath"] = input, ["dryRun"] = true }.ToJsonString()));
            Assert.Equal("importPath", preview.ParamName);
            Assert.True(preview.IsArgument);
            Assert.Contains("The input file cannot be read", preview.Message);
            var apply = Assert.Throws<AdapterPreconditionException>(() => McpServer.ValidateCallerInputFiles("ImportHmiScreen",
                new JsonObject { ["importPath"] = input, ["dryRun"] = false, ["expectedPlanHash"] = new string('a', 64) }.ToJsonString()));
            Assert.Equal(preview.ParamName, apply.ParamName);
            Assert.Equal(preview.IsArgument, apply.IsArgument);
            Assert.Contains("Input file changed", apply.Message);
            Assert.Contains("preview again", apply.Message);
        }

        [Fact]
        public void Junction_below_root_is_outside_but_ancestors_are_not_walked()
        {
            string destination = Path.Combine(root, "destination");
            string link = Path.Combine(root, "link");
            Directory.CreateDirectory(destination);
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link); start.ArgumentList.Add(destination);
                using var process = System.Diagnostics.Process.Start(start)!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(link, destination);
            }
            try
            {
                Assert.False(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(Path.Combine(link, "x"), root));
                File.WriteAllText(Path.Combine(root, "manifest.json"), """{"plcTypesDir":"link/types"}""");
                foreach (var seedRoot in new[] { root, link })
                {
                    var error = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.SeedReferenceLayout.Read(seedRoot, "21", null));
                    Assert.Equal("referenceDir", error.ParamName);
                    Assert.Equal("seed-reference-layout", error.Condition);
                    Assert.Contains("junction or symbolic link", error.Message);
                }
                Directory.CreateDirectory(Path.Combine(link, "exports"));
                Assert.True(TiaMcp.Logic.V4.CallerInputFiles.IsInsideOutputRoot(Path.Combine(link, "exports", "x"),
                    Path.Combine(link, "exports")));
            }
            finally { Directory.Delete(link); }
        }

        private static string Arguments(string key, string path) => new JsonObject { [key] = path }.ToJsonString();

        [Fact]
        public void Malformed_selectors_leave_type_validation_to_the_tool_boundary()
        {
            Assert.Empty(TiaMcp.Logic.V4.CallerInputFiles.OutputTargets("ManageHardwareUtilities",
                new JsonObject { ["action"] = true, ["filePath"] = Path.Combine(root, "out.xml") }));
            Assert.Equal(new[] { Path.Combine(root, "out.pdf") }, TiaMcp.Logic.V4.CallerInputFiles.OutputTargets("ExportSafetyPrintout",
                new JsonObject { ["printer"] = true, ["filePath"] = Path.Combine(root, "out.pdf") }));
        }

        [Theory]
        [InlineData("ExportAlarmInstanceTexts", "exportPath", ".xlsx")]
        [InlineData("ExportAlarmTextLists", "exportPath", ".xlsx")]
        [InlineData("ExportAlarmClasses", "exportPath", ".dat")]
        [InlineData("ExportOpcUaInterface", "exportPath", ".xml")]
        [InlineData("ExportTechnologyObject", "exportPath", ".xml")]
        [InlineData("ExportHmiScreen", "exportPath", ".xml")]
        [InlineData("ExportDeviceAml", "exportPath", ".aml")]
        [InlineData("ExportProjectTexts", "filePath", ".xlsx")]
        [InlineData("ExchangePlcSupervisions", "filePath", ".xlsx")]
        [InlineData("ExchangePlcAlarmTextLists", "filePath", ".xlsx")]
        [InlineData("ManageSivarcScreenLayout", "filePath", ".yml")]
        public void Native_export_prechecks_reject_extension_existing_target_and_missing_parent(string tool, string key, string extension)
        {
            string path = Path.Combine(root, "out" + extension);
            JsonObject Input(string output) => new() { [key] = output, ["action"] = "export" };
            var wrong = Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile(tool, Input(Path.Combine(root, "out.invalid"))));
            Assert.Equal(key, wrong.ParamName);
            TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile(tool, Input(path));
            File.WriteAllText(path, "original");
            Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile(tool, Input(path)));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile(tool, Input(Path.Combine(root, "missing", "out" + extension))));
        }

        [Fact]
        public void Aml_directory_alias_tracks_the_resolved_target_for_failure_classification()
        {
            var arguments = new JsonObject { ["exportPath"] = root };
            TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile("ExportDeviceAml", arguments);
            using var observation = TiaMcp.Logic.V4.CallerInputFiles.ObserveExport("ExportDeviceAml", arguments);
            string target = Path.Combine(root, "Device.aml");
            TiaMcp.Logic.V4.CallerInputFiles.ResolveExportTarget(target);
            TiaMcp.Logic.V4.CallerInputFiles.RecordExportFailure(new IOException("Native AML export failed."));
            Assert.True(observation!.NoFileWritten);
            File.WriteAllText(target, "partial AML");
            Assert.False(observation.NoFileWritten);
            Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Logic.V4.CallerInputFiles.ValidateNativeFile("ExportDeviceAml", new JsonObject { ["exportPath"] = target }));
        }

        // V20 finding 35: TIA Portal holds the open project file without read sharing.
        [Theory]
        [InlineData("ConnectProject", "projectPath")]
        [InlineData("OpenProject", "path")]
        [InlineData("OpenLocalSession", "localSessionPath")]
        public void Project_paths_are_checked_for_existence_without_opening_the_file(string tool, string key)
        {
            string project = Path.Combine(root, "Project1.ap20");
            File.WriteAllText(project, "fixture");
            using (new FileStream(project, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                McpServer.ValidateCallerInputFiles(tool, Arguments(key, project));
            var missing = Assert.Throws<AdapterPreconditionException>(() => McpServer.ValidateCallerInputFiles(tool, Arguments(key, Path.Combine(root, "Missing.ap20"))));
            Assert.True(missing.IsArgument);
            Assert.Equal(key, missing.ParamName);
        }

        [Fact]
        public void A_locked_import_file_is_a_precondition_refusal_before_the_operation()
        {
            string input = Path.Combine(root, "Block.xml");
            File.WriteAllText(input, "<Document />");
            using (new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Exception? error = Record.Exception(() => McpServer.ValidateCallerInputFiles("ImportPlcBlock", Arguments("importPath", input)));
                // Only Windows enforces the share mode for another reader.
                if (!OperatingSystem.IsWindows() && error == null) return;
                var refusal = Assert.IsType<AdapterPreconditionException>(error);
                Assert.False(refusal.IsArgument);
                Assert.Equal("importPath", refusal.ParamName);
                Assert.Equal(TiaOpenness.Shared.HostFailureKind.Precondition, TiaOpenness.Shared.HostFailurePolicy.Classify(refusal, true, false));
            }
        }
    }
}
