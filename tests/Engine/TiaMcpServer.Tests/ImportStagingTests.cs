using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Shared;
using TiaMcp.Logic.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class ImportStagingTests : IDisposable
    {
        internal static async Task VerifyApproval(PendingApproval pending)
        {
            pending.Validate();
            var display = JsonNode.Parse(pending.ParametersJson)!;
            Assert.Null(display["files"]![0]!["content"]);
            Assert.NotNull(display["files"]![0]!["sha256"]);
            Assert.NotNull(display["stagingLocation"]);
            using var wire = new MemoryStream();
            await ApprovalFrames.Write(wire, pending, CancellationToken.None);
            Assert.True(wire.Length < ApprovalFrames.MaximumBytes);
            wire.Position = 0;
            var received = await ApprovalFrames.Read<PendingApproval>(wire, CancellationToken.None);
            received.Validate(); Assert.Equal(pending.ArgumentDigest, received.ArgumentDigest);
            wire.SetLength(0); wire.Position = 0;
            await ApprovalFrames.Write(wire, new ApprovalDecision { RequestId = received.RequestId, PlanHash = received.PlanHash, ArgumentDigest = received.ArgumentDigest, Decision = "granted" }, CancellationToken.None);
            wire.Position = 0;
            Assert.True((await ApprovalFrames.Read<ApprovalDecision>(wire, CancellationToken.None)).Matches(pending));
        }
        [Fact]
        public async Task Approval_display_is_bounded_but_digest_covers_full_file_content()
        {
            var args = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["fileName"] = "F.scl", ["kind"] = "scl", ["content"] = new string('x', 4194304) }) };
            var pending = PendingApproval.Create("fixture", "21", "StageImportFiles", args.ToJsonString(), "{}", 120);
            await VerifyApproval(pending);
            Assert.Equal(4194304, (int)JsonNode.Parse(pending.ParametersJson)!["files"]![0]!["byteLength"]!);
            args["files"]![0]!["content"] = new string('y', 4194304);
            Assert.NotEqual(pending.ArgumentDigest, PendingApproval.Create("fixture", "21", "StageImportFiles", args.ToJsonString(), "{}", 120).ArgumentDigest);
            var large = new JsonObject();
            for (int i = 0; i < 128; i++) large[i.ToString()] = new string('a', 4096);
            var generic = PendingApproval.Create("fixture", "21", "AnyWrite", large.ToJsonString(), large.ToJsonString(), 120);
            generic.Validate(); using var frame = new MemoryStream(); await ApprovalFrames.Write(frame, generic, CancellationToken.None);
            Assert.True(frame.Length < ApprovalFrames.MaximumBytes);
            var inline = PendingApproval.Create("fixture", "21", "AnyWrite", "{\"sourceText\":\"private source\",\"xmlContent\":\"<Document/>\"}", "{}", 120);
            var inlineDisplay = JsonNode.Parse(inline.ParametersJson)!;
            Assert.Equal(14, (int)inlineDisplay["sourceText"]!["byteLength"]!);
            Assert.NotNull(inlineDisplay["xmlContent"]!["sha256"]);
        }
        private readonly string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private ImportStagingStore Store(string release = "17") { Directory.CreateDirectory(bundle); return new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public async Task Manifest_replace_retries_transient_delete_sharing_locks_and_keeps_partial_batches_cleanable(bool persistent)
        {
            if (Path.DirectorySeparatorChar != '\\') return;
            using var store = Store("19");
            FileStream? held = null; Task? release = null; bool locked = false;
            store.BeforeManifestPublishForTests = path =>
            {
                if (locked || !System.IO.File.Exists(path)) return;
                locked = true;
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!persistent) release = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });
            };
            try
            {
                if (persistent)
                {
                    var partial = store.Run("StageImportFiles", new[] { File() }, dryRun: false);
                    Assert.False(partial.Ok);
                    Assert.Equal(TiaMcp.Logic.V4.Outcome.Partial, partial.Meta.Outcome);
                    Assert.Equal(TiaMcp.Logic.V4.Execution.Partial, partial.Meta.Execution);
                    Assert.False(partial.Meta.RequiresSessionReset);
                    Assert.Equal(1, Assert.IsType<TiaMcp.Logic.V4.PartialFailureDetails>(partial.Error!.Details).Succeeded);
                    Assert.Contains("Staging publication failed after batch reservation:", partial.Error.Message);
                    held!.Dispose();
                    Assert.Single(store.List()["batches"]!.AsArray());
                    Assert.NotNull(store.Cleanup((string)JsonNode.Parse(partial.Data!.Value.GetRawText())!["batchId"]!, false));
                }
                else
                {
                    var batch = store.Stage(new[] { File() }, false);
                    Assert.Equal(1, (int)batch["writtenFileCount"]!);
                    Assert.Null(batch["partial"]);
                    Assert.Equal(1, (int)JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine((string)batch["directory"]!, ".staging-batch.json")))!["writtenFileCount"]!);
                    Assert.Single(store.List()["batches"]!.AsArray());
                }
                Assert.True(locked);
            }
            finally { if (release != null) await release; held?.Dispose(); }
        }
        [Theory]
        [InlineData("stdio", "open")][InlineData("stdio", "closed")][InlineData("stdio", "restarted")][InlineData("stdio", "unknown")]
        [InlineData("http", "open")][InlineData("http", "closed")][InlineData("http", "restarted")][InlineData("http", "unknown")]
        public void Transport_sessions_protect_live_batches_and_release_ended_batches(string transport, string state)
        {
            Directory.CreateDirectory(bundle);
            using var owner = new ImportStagingSession(transport + "-A");
            var old = new ImportStagingStore(bundle, "18", owner);
            var batch = old.Stage(new[] { File() }, false);
            string id = (string)batch["batchId"]!, folder = (string)batch["directory"]!;
            Assert.Equal("current", (string?)old.List()["batches"]![0]!["ownerState"]);
            Assert.Equal(transport + "-A", (string?)batch["mcpSessionId"]);
            Assert.Equal(owner.HostInstanceId, (string?)batch["hostInstanceId"]);
            if (state == "closed") owner.Dispose();
            if (state is "unknown" or "restarted")
            {
                foreach (string name in new[] { ".staging-batch.json", ".staging-batch.previous.json" })
                {
                    string path = Path.Combine(folder, name); var manifest = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
                    manifest["hostInstanceId"] = Guid.NewGuid().ToString("N");
                    if (state == "restarted") manifest["hostStartedUtc"] = DateTimeOffset.UtcNow.AddYears(-1).ToString("O");
                    System.IO.File.WriteAllText(path, manifest.ToJsonString());
                }
            }
            using var current = new ImportStagingSession(transport + "-B");
            var connected = new ImportStagingStore(bundle, "18", current);
            var listed = Assert.Single(connected.List()["batches"]!.AsArray())!;
            Assert.Equal(state is "closed" or "restarted" ? "ended" : state == "open" ? "live-other" : "unknown", (string?)listed["ownerState"]);
            var preview = connected.Run("CleanupStagedImportFiles", batchId: id);
            var result = connected.Run("CleanupStagedImportFiles", batchId: id, dryRun: false);
            Assert.Equal(state is "closed" or "restarted", preview.Ok);
            Assert.Equal(state is "closed" or "restarted", result.Ok);
            Assert.Equal(state is "open" or "unknown", System.IO.File.Exists(Path.Combine(folder, "F.scl")));
        }
        [Fact]
        public void Closing_session_keeps_in_flight_operations_protected_until_they_finish()
        {
            Directory.CreateDirectory(bundle);
            using var owner = new ImportStagingSession("http-A");
            var old = new ImportStagingStore(bundle, "18", owner); var batch = old.Stage(new[] { File() }, false);
            var connected = Store("18"); string id = (string)batch["batchId"]!;
            var active = owner.EnterRequest(); owner.Dispose();
            Assert.Throws<InvalidOperationException>(() => owner.EnterRequest());
            Assert.Equal("live-other", (string?)connected.List()["batches"]![0]!["ownerState"]);
            Assert.Throws<ArgumentException>(() => connected.Cleanup(id, false));
            active.Dispose();
            Assert.Equal("ended", (string?)connected.List()["batches"]![0]!["ownerState"]);
            connected.Cleanup(id, false); Assert.Empty(connected.List()["batches"]!.AsArray());
        }
        private static StagedTextFile File(string name = "F.scl", string kind = "scl", string content = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION\n") => new StagedTextFile { FileName = name, Kind = kind, Content = content };
        [Theory]
        [InlineData("../F.scl")][InlineData("..\\F.scl")][InlineData("C:\\F.scl")][InlineData("F/F.scl")][InlineData("F\\F.scl")]
        [InlineData("CON .scl")][InlineData("LPT1 .scl")][InlineData("CON.scl")][InlineData("nul.scl")][InlineData("LPT1.scl")][InlineData("COM¹.scl")][InlineData("F.scl ")]
        [InlineData("F.scl.")][InlineData("F..scl")][InlineData("F:scl")][InlineData("F\0.scl")][InlineData("CONIN$.scl")]
        public void Unsafe_names_refuse_before_creating_files(string name)
        {
            var store = Store(); Assert.Throws<ArgumentException>(() => store.Stage(new[] { File(name) }, false)); Assert.Empty(Directory.GetDirectories(bundle));
        }
        [Theory]
        [InlineData("F.xml", "scl")][InlineData("F.scl", "simaticml")][InlineData("F.txt", "tagtable")][InlineData("F.s7dcl", "s7dcl")][InlineData("F.exe", "udt")][InlineData("F.xml", "unknown")]
        public void Kind_and_extension_are_release_bounded(string name, string kind)
        { Assert.Throws<ArgumentException>(() => Store().Stage(new[] { File(name, kind) })); }
        [Theory]
        [InlineData("\ufeffFUNCTION F : Void")][InlineData("中文")][InlineData("F\0")][InlineData("F\u0001")][InlineData("")]
        public void External_sources_require_nonempty_ASCII_without_BOM(string content)
        { Assert.Throws<ArgumentException>(() => Store().Stage(new[] { File(content: content) })); }
        [Theory]
        [InlineData("<x/>")][InlineData("<Document xmlns=\"urn:foreign\"/>")][InlineData("<!DOCTYPE Document [<!ENTITY e 'x'>]><Document>&e;</Document>")]
        [InlineData("<?xml version=\"1.0\" encoding=\"utf-16\"?><Document/>")][InlineData("<Document>")]
        public void XML_requires_bounded_Document_without_DTD(string content)
        { var body = Store().Run("StageImportFiles", new[] { File("F.xml", "simaticml", content) }); Assert.False(body.Ok); Assert.Empty(Directory.GetDirectories(bundle)); }
        [Fact]
        public void XML_depth_and_kind_are_checked()
        {
            var store = Store(); var deep = "<Document>" + string.Concat(Enumerable.Repeat("<x>", 65)) + string.Concat(Enumerable.Repeat("</x>", 65)) + "</Document>";
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File("F.xml", "simaticml", deep) }));
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File("F.xml", "tagtable", "<Document><SW.Types.PlcStruct/></Document>") }));
        }
        [Fact]
        public void Byte_count_and_case_insensitive_duplicate_quota_are_checked()
        {
            var store = Store(); Assert.Throws<ArgumentException>(() => store.Stage(new[] { File(content: new string('x', 4194305)) }));
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File("F.xml", "simaticml", "<Document>" + new string('中', 1400000) + "</Document>") }));
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File("F.scl"), File("f.scl") }));
            Assert.Throws<ArgumentException>(() => store.Stage(Enumerable.Range(0, 129).Select(i => File(i + ".scl")).ToArray()));
            Assert.Throws<ArgumentException>(() => store.Stage(Array.Empty<StagedTextFile>()));
        }
        [Fact]
        public void Preview_stage_list_cleanup_round_trip_owns_bundle_paths_and_hashes()
        {
            var store = Store("21"); var source = File(); var xml = File("Tags.xml", "tagtable", "<Document><SW.Tags.PlcTagTable><AttributeList><Name>中文</Name></AttributeList></SW.Tags.PlcTagTable></Document>");
            var files = new[] { source, xml, File("DB.s7dcl", "s7dcl", "DATA_BLOCK DB\nEND_DATA_BLOCK\n") };
            Assert.False(store.Stage(files)["executed"]!.GetValue<bool>()); Assert.Empty(Directory.GetDirectories(bundle));
            var staged = store.Stage(files, false); string dir = staged["directory"]!.GetValue<string>();
            Assert.StartsWith(Path.Combine(bundle, "staging") + Path.DirectorySeparatorChar, dir);
            foreach (var entry in staged["files"]!.AsArray())
            {
                string path = entry!["path"]!.GetValue<string>(); var bytes = System.IO.File.ReadAllBytes(path);
                Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
                Assert.Equal(TiaMcp.Logic.V4.PlcImportSession.ByteHash(bytes), entry["sha256"]!.GetValue<string>());
            }
            Assert.Single(store.List()["batches"]!.AsArray());
            var again = store.Stage(new[] { source }, false); Assert.NotEqual(dir, (string?)again["directory"]);
            var id = staged["batchId"]!.GetValue<string>(); Assert.Throws<ArgumentException>(() => store.Cleanup("../" + id, false));
            Assert.Throws<ArgumentException>(() => store.Cleanup(Guid.NewGuid().ToString("N"), false));
            store.Cleanup(id); Assert.True(Directory.Exists(dir)); store.Cleanup(id, false); Assert.False(Directory.Exists(dir));
            Assert.Single(store.List()["batches"]!.AsArray());
        }
        [Fact]
        public void Quota_rechecks_after_preview_and_cleanup_releases_capacity()
        {
            var store = Store(); var files = Enumerable.Range(0, 128).Select(i => File(i + ".scl")).ToArray(); var batch = store.Stage(files, false);
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File() }, false));
            store.Cleanup(batch["batchId"]!.GetValue<string>(), false); Assert.True(store.Stage(new[] { File() }, false)["executed"]!.GetValue<bool>());
        }
        [Fact]
        public void Batch_count_and_aggregate_bytes_are_bounded_before_writes()
        {
            var store = Store(); var content = new string('x', (int)ImportStagingStore.MaximumFileBytes);
            Assert.Throws<ArgumentException>(() => store.Stage(Enumerable.Range(0, 9).Select(i => File(i + ".scl", content: content)).ToArray(), false));
            Assert.Empty(Directory.GetDirectories(bundle));
            for (int i = 0; i < ImportStagingStore.MaximumBatches; i++) store.Stage(new[] { File() }, false);
            Assert.Throws<ArgumentException>(() => store.Stage(new[] { File() }, false));
            Assert.Equal(ImportStagingStore.MaximumBatches, store.List()["batches"]!.AsArray().Count);
        }
        [Fact]
        public void Unavailable_bundle_staging_reports_attempted_path_before_creation()
        {
            var store = Store(); System.IO.File.WriteAllText(Path.Combine(bundle, "staging"), "occupied");
            var result = store.Run("StageImportFiles", new[] { File() }); Assert.False(result.Ok);
            var data = JsonNode.Parse(result.Data!.Value.GetRawText())!; Assert.StartsWith(Path.Combine(bundle, "staging"), (string?)data["attemptedPath"]);
            Assert.Equal(TiaMcp.Logic.V4.Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        }
        [Theory]
        [InlineData("20")][InlineData("21")]
        public void Document_resource_pairs_stage_for_both_document_importers(string release)
        {
            var store = Store(release);
            var result = store.Stage(new[] { File("DB.s7dcl", "s7dcl", "DATA_BLOCK DB\nEND_DATA_BLOCK"), File("DB.s7res", "s7res", "<Resources/>") }, false);
            Assert.Equal(2, result["files"]!.AsArray().Count);
            Assert.All(result["files"]!.AsArray(), f => Assert.True(System.IO.File.Exists((string)f!["path"]!)));
            Assert.Throws<ArgumentException>(() => Store("19").Stage(new[] { File("DB.s7res", "s7res", "<Resources/>") }));
        }
        [Fact]
        public void Preview_refuses_paths_the_net48_importer_cannot_open()
        {
            var nested = Path.Combine(bundle, new string('d', 100)); Directory.CreateDirectory(nested);
            var store = new ImportStagingStore(nested, "21", Guid.NewGuid().ToString("N"));
            var result = store.Run("StageImportFiles", new[] { File(new string('f', 124) + ".scl") });
            // Only Windows hosts hand staged paths to net48 importers; elsewhere the preview has no such limit.
            Assert.Equal(!OperatingSystem.IsWindows(), result.Ok);
            if (!OperatingSystem.IsWindows()) return;
            Assert.Equal(TiaMcp.Logic.V4.Outcome.RejectedBeforeOperation, result.Meta.Outcome);
            Assert.False(Directory.Exists(Path.Combine(nested, "staging")));
        }
        [Fact]
        public void Publication_guard_failure_is_partial_and_the_reserved_batch_is_cleanable()
        {
            var store = Store(); int writes = 0;
            store.BeforeWriteForTests = _ => { if (++writes == 2) throw new ArgumentException("Path changed to a reparse point.", "files"); };
            var result = store.Run("StageImportFiles", new[] { File("A.scl"), File("B.scl") }, dryRun: false);
            Assert.False(result.Ok); Assert.Equal(TiaMcp.Logic.V4.Outcome.Partial, result.Meta.Outcome);
            Assert.Equal(TiaMcp.Logic.V4.Execution.Partial, result.Meta.Execution);
            var batch = Assert.Single(store.List()["batches"]!.AsArray())!;
            Assert.True(System.IO.File.Exists((string)batch["files"]![0]!["path"]!));
            Assert.Equal(2, batch["files"]!.AsArray().Count);
            store.Cleanup((string)batch["batchId"]!, false); Assert.Empty(store.List()["batches"]!.AsArray());
        }
        [Fact]
        public void Single_import_recovery_is_best_effort_and_identifies_skipped_objects()
        {
            Directory.CreateDirectory(bundle); int exports = 0;
            var missing = NativeExportPolicy.SingleImportRecovery(new Action<FileInfo>[] { _ => exports++ },
                () => throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("No writable recovery location.", "overwrite", false), bundle);
            Assert.Equal(0, exports); Assert.Equal("backup-skipped", missing.Status); Assert.Contains(bundle, missing.Warning);
            var failed = NativeExportPolicy.SingleImportRecovery(new Action<FileInfo>[] {
                file => System.IO.File.WriteAllText(file.FullName,"<Document/>"), _ => throw new IOException("Export failed before import.") }, () => bundle, bundle);
            Assert.Equal("backup-skipped", failed.Status); Assert.Single(failed.Files);
            var typedExport = NativeExportPolicy.SingleImportRecovery(new[] { new NativeExportPolicy.RecoveryTarget {
                Object = "Group/Ready", Export = _ => throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Recovery output is unavailable.", "exportPath") } }, () => bundle, bundle);
            Assert.Equal("backup-skipped", typedExport.Status); Assert.Contains("Group/Ready", typedExport.Warning);
            Assert.Equal("export-or-validation-refused (AdapterPreconditionException)", typedExport.Skipped[0]["reason"]);
            var typedLocation = NativeExportPolicy.SingleImportRecovery(new[] { new NativeExportPolicy.RecoveryTarget { Object = "Group/Ready" } },
                () => throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Recovery output is unavailable.", "exportPath"), bundle);
            Assert.Equal("backup-skipped", typedLocation.Status); Assert.Null(typedLocation.Directory); Assert.Contains("Group/Ready", typedLocation.Warning);
            var inconsistent = NativeExportPolicy.SingleImportRecovery(new[] { new NativeExportPolicy.RecoveryTarget {
                Object = "Group/Uncompiled", Blocker = "inconsistent", Export = _ => exports++ } }, () => bundle, bundle);
            Assert.Equal(0, exports); Assert.Contains("Group/Uncompiled: inconsistent", inconsistent.Warning);
            var mixed = NativeExportPolicy.SingleImportRecovery(new[] {
                new NativeExportPolicy.RecoveryTarget { Object = "Group/Blocked", Blocker = "inconsistent" },
                new NativeExportPolicy.RecoveryTarget { Object = "Group/Ready", Export = file => { Assert.Equal("001.xml", file.Name); System.IO.File.WriteAllText(file.FullName, "<Document/>"); } }
            }, () => bundle, bundle);
            Assert.Single(mixed.Files);
            var oversized = NativeExportPolicy.SingleImportRecovery(new Action<FileInfo>[] {
                file => System.IO.File.WriteAllText(file.FullName,new string('x',4194305)) }, () => bundle, bundle);
            Assert.Equal("backup-skipped", oversized.Status);
        }
        [Theory]
        [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
        public void Reconnected_store_lists_and_cleans_old_batches_but_retains_unknown_content(string release)
        {
            var old = Store(release); var batch = old.Stage(new[] { File() }, false);
            string folder = (string)batch["directory"]!, id = (string)batch["batchId"]!;
            System.IO.File.WriteAllText(Path.Combine(folder, "caller.xml"), "private export");
            var connected = Store(release); connected.OwnerAliveForTests = (_, _) => false; var listed = Assert.Single(connected.List()["batches"]!.AsArray())!;
            Assert.True((bool?)listed["identified"]); Assert.False((bool?)listed["currentSession"]);
            Assert.Equal(batch["sessionId"]!.ToString(), listed["sessionId"]!.ToString());
            var preview = connected.Cleanup(id); Assert.True((bool?)preview["folderRetained"]);
            Assert.Equal("caller.xml", preview["unknownEntries"]![0]!.ToString());
            Assert.True(System.IO.File.Exists(Path.Combine(folder, "F.scl")));
            var result = connected.Run("CleanupStagedImportFiles", batchId: id, dryRun: false);
            Assert.True(result.Ok); Assert.Equal(TiaMcp.Logic.V4.Execution.Completed, result.Meta.Execution);
            Assert.Equal(TiaMcp.Logic.V4.WarningCode.StagingFolderRetained, Assert.Single(result.Meta.Warnings).Code); Assert.False(System.IO.File.Exists(Path.Combine(folder, "F.scl")));
            Assert.Equal("private export", System.IO.File.ReadAllText(Path.Combine(folder, "caller.xml")));
            Assert.True((bool?)JsonNode.Parse(result.Data!.Value.GetRawText())!["folderRetained"]);
            Assert.Empty(connected.List()["batches"]![0]!["files"]!.AsArray());
            System.IO.File.Delete(Path.Combine(folder, "caller.xml")); connected.Cleanup(id, false);
            Assert.False(Directory.Exists(folder)); Assert.Empty(old.List()["batches"]!.AsArray());
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Modified_or_locked_owned_files_are_retained_as_unknown(bool locked)
        {
            var store = Store(); var batch = store.Stage(new[] { File() }, false);
            string folder = (string)batch["directory"]!, id = (string)batch["batchId"]!, leaf = Path.Combine(folder, "F.scl");
            string replacement = new string('x', (int)new FileInfo(leaf).Length);
            if (!locked) System.IO.File.WriteAllText(leaf, replacement);
            using var stream = locked ? new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            var result = store.Cleanup(id, false);
            Assert.True((bool)result["folderRetained"]!); Assert.Contains(result["unknownEntries"]!.AsArray(), x => x!.ToString() == "F.scl");
            Assert.True(System.IO.File.Exists(leaf));
            if (!locked) Assert.Equal(replacement, System.IO.File.ReadAllText(leaf));
        }
        [Fact]
        public void Live_owner_is_refused_but_a_reused_PID_or_dead_owner_is_cleanable()
        {
            var old = Store(); var batch = old.Stage(new[] { File() }, false); var store = Store();
            Assert.Throws<ArgumentException>(() => store.Cleanup((string)batch["batchId"]!, false));
            string manifest = Path.Combine((string)batch["directory"]!, ".staging-batch.json");
            var metadata = JsonNode.Parse(System.IO.File.ReadAllText(manifest))!;
            metadata["hostStartedUtc"] = DateTimeOffset.UtcNow.AddYears(-1).ToString("O"); System.IO.File.WriteAllText(manifest, metadata.ToJsonString());
            store.Cleanup((string)batch["batchId"]!, false); Assert.Empty(store.List()["batches"]!.AsArray());
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Truncated_primary_uses_last_valid_companion_without_following_mismatched_identity(bool structural)
        {
            var store = Store(); var batch = store.Stage(new[] { File() }, false);
            string folder = (string)batch["directory"]!, id = (string)batch["batchId"]!, manifest = Path.Combine(folder, ".staging-batch.json");
            System.IO.File.WriteAllText(manifest, structural ? System.IO.File.ReadAllText(manifest).Replace("\"manifestVersion\":2", "\"manifestVersion\":999") : "{\"manifestVersion\":");
            var listed = Assert.Single(store.List()["batches"]!.AsArray())!;
            Assert.Equal("manifest-invalid", (string?)listed["reason"]); Assert.Single(listed["files"]!.AsArray());
            var metadata = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(folder, ".staging-batch.previous.json")))!;
            metadata["sessionId"] = Guid.NewGuid().ToString("N"); System.IO.File.WriteAllText(manifest, metadata.ToJsonString());
            Assert.False((bool?)Assert.Single(store.List()["batches"]!.AsArray())!["identified"]);
            Assert.Throws<ArgumentException>(() => store.Cleanup(id, false));
            System.IO.File.WriteAllText(manifest, "{"); store.Cleanup(id, false); Assert.False(Directory.Exists(folder));
        }
        [Fact]
        public void Interrupted_atomic_publication_keeps_valid_metadata_and_hash_matching_files_cleanable()
        {
            var store = Store(); int publications = 0;
            store.BeforeManifestPublishForTests = _ => { if (++publications == 3) throw new IOException("Interrupted before replacement"); };
            var result = store.Run("StageImportFiles", new[] { File() }, dryRun: false); Assert.False(result.Ok);
            var batch = Assert.Single(store.List()["batches"]!.AsArray())!; Assert.True((bool?)batch["identified"]);
            Assert.Single(batch["files"]!.AsArray()); Assert.Empty(batch["unknownEntries"]!.AsArray());
            store.BeforeManifestPublishForTests = null; store.Cleanup((string)batch["batchId"]!, false); Assert.Empty(store.List()["batches"]!.AsArray());
        }
        [Fact]
        public void Manifest_fields_are_whitelisted_and_other_session_faults_do_not_block_stage_or_cleanup()
        {
            var old = Store(); var batch = old.Stage(new[] { File() }, false); string folder = (string)batch["directory"]!;
            string manifest = Path.Combine(folder, ".staging-batch.json"); var json = JsonNode.Parse(System.IO.File.ReadAllText(manifest))!;
            json["privateCallerContent"] = "secret"; json["files"]![0]!["callerContent"] = "secret"; System.IO.File.WriteAllText(manifest, json.ToJsonString());
            var store = Store(); var listed = Assert.Single(store.List()["batches"]!.AsArray())!;
            Assert.Null(listed["privateCallerContent"]); Assert.Null(listed["files"]![0]!["callerContent"]);
            store.DiscoverForTests = path => { if (path == Path.GetDirectoryName(folder)) throw new UnauthorizedAccessException(); };
            Assert.Contains(store.List()["batches"]!.AsArray(), b => (string?)b?["reason"] == "unreadable-or-missing-session");
            var unknown = Assert.Single(store.List()["batches"]!.AsArray())!;
            Assert.Equal((string?)batch["sessionId"], (string?)unknown["sessionId"]); Assert.Null(unknown["batchId"]);
            var own = store.Stage(new[] { File() }, false); store.Cleanup((string)own["batchId"]!, false);
            store.DiscoverForTests = path => { if (path == folder) throw new DirectoryNotFoundException(); };
            Assert.Contains(store.List()["batches"]!.AsArray(), b => (string?)b?["reason"] == "unreadable-or-missing-batch");
            string owner = Path.GetDirectoryName(folder)!;
            Assert.Equal(Path.Combine(bundle, "staging", (string)batch["sessionId"]!), owner);
            store.DiscoverForTests = path => { if (path == owner) Directory.Move(owner, owner + "-gone"); };
            var vanished = Assert.Single(store.List()["batches"]!.AsArray())!;
            Assert.Equal("unreadable-or-missing-session", (string?)vanished["reason"]);
            Assert.Equal((string?)batch["sessionId"], (string?)vanished["sessionId"]); Assert.Null(vanished["batchId"]);
        }
        [Fact]
        public void List_is_bounded_and_keeps_newest_batches_with_total_counts()
        {
            var store = Store();
            for (int i = 0; i < 201; i++)
            {
                var batch = Store().Stage(new[] { File() }, false);
                string path = Path.Combine((string)batch["directory"]!, ".staging-batch.json");
                var json = JsonNode.Parse(System.IO.File.ReadAllText(path))!; json["createdUtc"] = DateTimeOffset.UtcNow.AddDays(i).ToString("O");
                System.IO.File.WriteAllText(path, json.ToJsonString());
            }
            var result = store.List(); Assert.Equal(201, (int)result["totalBatches"]!);
            Assert.Equal(200, result["batches"]!.AsArray().Count); Assert.True((bool)result["truncated"]!);
            Assert.True(DateTimeOffset.Parse((string)result["batches"]![0]!["createdUtc"]!) > DateTimeOffset.Parse((string)result["batches"]![199]!["createdUtc"]!));
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Cleanup_failure_counts_its_own_deletions_and_guards_after_deletion_are_partial(bool guard)
        {
            var store = Store(); int writes = 0;
            store.BeforeWriteForTests = _ => { if (++writes == 2) throw new IOException("Unrelated partial stage"); };
            store.Run("StageImportFiles", new[] { File("X.scl"), File("Y.scl"), File("Z.scl") }, dryRun: false);
            store.BeforeWriteForTests = null;
            var batch = store.Stage(new[] { File("A.scl"), File("B.scl") }, false); string id = (string)batch["batchId"]!;
            store.BeforeDeleteForTests = _ => throw new IOException("First deletion refused");
            var initial = store.Run("CleanupStagedImportFiles", batchId: id, dryRun: false);
            Assert.Equal(TiaMcp.Logic.V4.Execution.NotStarted, initial.Meta.Execution); Assert.IsType<TiaMcp.Logic.V4.IoFailedDetails>(initial.Error!.Details);
            int deletes = 0; store.BeforeDeleteForTests = _ => { if (++deletes == 2) throw guard ? new ArgumentException("Ancestry changed") : new IOException("Second deletion refused"); };
            var partial = store.Run("CleanupStagedImportFiles", batchId: id, dryRun: false);
            Assert.Equal(TiaMcp.Logic.V4.Execution.Partial, partial.Meta.Execution); Assert.Equal(TiaMcp.Logic.V4.Outcome.Partial, partial.Meta.Outcome);
            var details = Assert.IsType<TiaMcp.Logic.V4.PartialFailureDetails>(partial.Error!.Details);
            Assert.Equal(1, details.Succeeded); Assert.Equal(1, details.Failed); Assert.Equal(0, details.NotExecuted);
            Assert.Equal(id, (string?)JsonNode.Parse(partial.Data!.Value.GetRawText())!["batchId"]);
        }
        [Theory]
        [InlineData("groupPath")][InlineData("softwarePath")]
        public void Single_import_keeps_typed_target_refusals_and_never_creates_a_directory_for_all_blocked_targets(string parameter)
        {
            int directories = 0;
            System.Collections.Generic.IEnumerable<NativeExportPolicy.RecoveryTarget> InvalidTargets()
            { yield return Fail(); }
            NativeExportPolicy.RecoveryTarget Fail() => throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact destination unavailable", parameter, false);
            var refusal = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => NativeExportPolicy.SingleImportRecovery(InvalidTargets(), () => { directories++; return bundle; }, bundle));
            Assert.Equal(parameter, refusal.ParamName); Assert.Equal(0, directories);
            var result = NativeExportPolicy.SingleImportRecovery(new[] { new NativeExportPolicy.RecoveryTarget { Object = "Group/A", InspectBlocker = () => throw new IOException() } }, () => { directories++; return bundle; }, bundle);
            Assert.Equal(0, directories); Assert.Equal("unknown-consistency", result.Skipped[0]["reason"]); Assert.Contains("Group/A", result.Warning);
        }
        [Fact]
        public void Existing_staging_directory_needs_no_add_file_right_on_bundle_root()
        {
            var store = Store(); Directory.CreateDirectory(Path.Combine(bundle, "staging"));
            string? checkedPath = null;
            store.WriteAccessForTests = path => { checkedPath = path; if (path == bundle) throw new IOException("Bundle root denies add-file rights."); };
            Assert.True(store.Stage(new[] { File() }, false)["executed"]!.GetValue<bool>());
            Assert.Equal(Path.Combine(bundle, "staging"), checkedPath);
        }
        public void Dispose()
        {
            // The fixture bundle is a fresh short folder under the temp root (CI checkouts are deep).
            if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
        }
    }
}
