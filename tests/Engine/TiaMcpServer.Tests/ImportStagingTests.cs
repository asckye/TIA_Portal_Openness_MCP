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
        private readonly string bundle = Path.GetFullPath(Path.Combine("bin-build", "P6-67", "staging-fixture-" + Guid.NewGuid().ToString("N")));
        private ImportStagingStore Store(string release = "17") { Directory.CreateDirectory(bundle); return new ImportStagingStore(bundle, release, Guid.NewGuid().ToString("N")); }
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
            Assert.False(result.Ok); Assert.Equal(TiaMcp.Logic.V4.Outcome.RejectedBeforeOperation, result.Meta.Outcome);
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
            var inconsistent = NativeExportPolicy.SingleImportRecovery(new[] { new NativeExportPolicy.RecoveryTarget {
                Object = "Group/Uncompiled", Blocker = "inconsistent", Export = _ => exports++ } }, () => bundle, bundle);
            Assert.Equal(0, exports); Assert.Contains("Group/Uncompiled: inconsistent", inconsistent.Warning);
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
            var connected = Store(release); var listed = Assert.Single(connected.List()["batches"]!.AsArray())!;
            Assert.True((bool?)listed["identified"]); Assert.False((bool?)listed["currentSession"]);
            Assert.Equal(batch["sessionId"]!.ToString(), listed["sessionId"]!.ToString());
            var preview = connected.Cleanup(id); Assert.True((bool?)preview["folderRetained"]);
            Assert.Equal("caller.xml", preview["unknownEntries"]![0]!.ToString());
            Assert.True(System.IO.File.Exists(Path.Combine(folder, "F.scl")));
            var result = connected.Run("CleanupStagedImportFiles", batchId: id, dryRun: false);
            Assert.True(result.Ok); Assert.Equal(TiaMcp.Logic.V4.Execution.Completed, result.Meta.Execution);
            Assert.Single(result.Meta.Warnings); Assert.False(System.IO.File.Exists(Path.Combine(folder, "F.scl")));
            Assert.Equal("private export", System.IO.File.ReadAllText(Path.Combine(folder, "caller.xml")));
            Assert.True((bool?)JsonNode.Parse(result.Data!.Value.GetRawText())!["folderRetained"]);
            Assert.Empty(connected.List()["batches"]![0]!["files"]!.AsArray());
            System.IO.File.Delete(Path.Combine(folder, "caller.xml")); connected.Cleanup(id, false);
            Assert.False(Directory.Exists(folder)); Assert.Empty(old.List()["batches"]!.AsArray());
        }
        [Fact]
        public void Missing_invalid_or_traversing_manifests_are_listed_but_never_deleted()
        {
            var store = Store(); var batch = store.Stage(new[] { File() }, false);
            string folder = (string)batch["directory"]!, id = (string)batch["batchId"]!;
            string manifest = Path.Combine(folder, ".staging-batch.json");
            var metadata = JsonNode.Parse(System.IO.File.ReadAllText(manifest))!;
            metadata["files"]![0]!["fileName"] = "../outside.scl"; System.IO.File.WriteAllText(manifest, metadata.ToJsonString());
            Assert.False((bool?)Assert.Single(store.List()["batches"]!.AsArray())!["identified"]);
            Assert.Throws<ArgumentException>(() => store.Cleanup(id, false));
            System.IO.File.Delete(manifest);
            Assert.False((bool?)Assert.Single(Store().List()["batches"]!.AsArray())!["identified"]);
            Assert.Throws<ArgumentException>(() => Store().Cleanup(id, false));
            Assert.True(System.IO.File.Exists(Path.Combine(folder, "F.scl")));
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
            // All fixture paths are fixed descendants of the current worktree.
            if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
        }
    }
}
