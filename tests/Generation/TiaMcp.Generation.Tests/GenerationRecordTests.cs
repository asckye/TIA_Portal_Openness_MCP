using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class GenerationRecordTests
    {
        private static GenerationRecord Record() => new GenerationRecord { Project = new GenerationRecordProject { File = "C:/Projects/Line/Line.ap21", Release = "21" } };
        [Fact]
        public void RecordHashExcludesOnlyItselfAndDetectsEveryEditedOwnershipField()
        {
            var record = Record(); record.RecordHash = GenerationRecord.Hash(record);
            Assert.Equal(record.RecordHash, GenerationRecord.Parse(GenerationDocuments.Canonical(record)).RecordHash);
            var before = record.RecordHash; record.RecordHash = "sha256:" + new string('f', 64); Assert.Equal(before, GenerationRecord.Hash(record));
            record.Project.File = "C:/Projects/Other/Other.ap21";
            Assert.Throws<ArgumentException>(() => GenerationRecord.Parse(GenerationDocuments.Canonical(record)));
        }
        [Fact]
        public void RecordSchemaRejectsUnknownFieldsAndDuplicateOwnershipKeys()
        {
            var record = Record(); record.RecordHash = GenerationRecord.Hash(record);
            var node = JsonNode.Parse(GenerationDocuments.Canonical(record))!; node["bypass"] = true;
            Assert.Throws<GenerationValidationException>(() => GenerationRecord.Parse(node.ToJsonString()));
            record.Objects.Add(new GenerationRecordObject { Key = "PLC1/block/FB_A", Path = "FB_A", ExpectedFingerprint = "sha256:" + new string('a', 64) });
            record.Objects.Add(new GenerationRecordObject { Key = "plc1/block/fb_a", Path = "FB_A", ExpectedFingerprint = "sha256:" + new string('a', 64) });
            record.RecordHash = GenerationRecord.Hash(record);
            Assert.Throws<ArgumentException>(() => GenerationRecord.Parse(GenerationDocuments.Canonical(record)));
        }
        [Fact]
        public void FileSnapshotUsesRawBytesForPreviousRecordRaceRefusal()
        {
            string root = Path.Combine(Path.GetTempPath(), "tiagen-record-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "Line.tiagen.json"); var record = Record(); record.RecordHash = GenerationRecord.Hash(record);
                GenerationRecordFile.Write(path, record, "absent"); var snapshot = GenerationRecordFile.Read(path);
                File.AppendAllText(path, " "); var bytes = File.ReadAllBytes(path);
                Assert.Equal(record.RecordHash, GenerationRecordFile.Read(path).Record!.RecordHash);
                Assert.NotEqual(snapshot.Token, GenerationRecordFile.Read(path).Token);
                Assert.Throws<ArgumentException>(() => GenerationRecordFile.Write(path, record, snapshot.Token)); Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            finally { Directory.Delete(root, true); }
        }
        [Fact]
        public void RecordLivesBesideTheProjectOnEveryExactRelease()
        {
            foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                Assert.Equal("C:/Projects/Line/Line.tiagen.json", GenerationRecordFile.DefaultPath(GenerationProjectPath.CreationFile(release, "C:/Projects", "Line")));
        }
        [Fact]
        public void WitnessComparisonIncludesKindToolAndEveryValue()
        {
            var first = new GenerationWitness { Kind = "modified-date", Tool = "GetPlcBlockInfo" }; first.Values["modifiedDate"] = JsonSerializer.SerializeToElement("2026-10-10");
            var same = new GenerationWitness { Kind = first.Kind, Tool = first.Tool, Values = first.Values.ToDictionary(p => p.Key, p => p.Value) };
            Assert.True(first.Matches(same)); same.Values["modifiedDate"] = JsonSerializer.SerializeToElement("2026-10-11"); Assert.False(first.Matches(same));
            same.Values = first.Values; same.Tool = "GetPlcTypeInfo"; Assert.False(first.Matches(same)); Assert.False(first.Matches(null));
        }
    }
}
