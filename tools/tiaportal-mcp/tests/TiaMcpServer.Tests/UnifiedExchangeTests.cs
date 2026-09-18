using System;
using System.IO;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Unified native exchange family (tags WinCC ML, script modules, OPC UA alarm xml): directory/file rules,
    // expected-name parsing and native export file verification, exercised on a temp directory.
    internal static class UnifiedExchangeTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.Combine(Path.GetTempPath(), "tia-mcp-exchange-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                // Directories: export needs a new directory under an existing parent, import needs an existing one.
                var fresh = Path.Combine(temp, "export1");
                check(UnifiedExchangeLogic.ValidateDirectory(fresh, true).FullName == Path.GetFullPath(fresh), "exchange: new export directory accepted (not created yet)");
                check(!Directory.Exists(fresh), "exchange: validation does not create the export directory");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.ValidateDirectory(temp, true)), "exchange: existing export directory refused (native Export overwrites)");
                check(Fails<DirectoryNotFoundException>(() => UnifiedExchangeLogic.ValidateDirectory(Path.Combine(temp, "missing", "deep"), true)), "exchange: export under a missing parent refused");
                check(UnifiedExchangeLogic.ValidateDirectory(temp, false).Exists, "exchange: existing import directory accepted");
                check(Fails<DirectoryNotFoundException>(() => UnifiedExchangeLogic.ValidateDirectory(fresh, false)), "exchange: missing import directory refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateDirectory("relative\\dir", true)), "exchange: relative directory refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateDirectory("", false)), "exchange: empty directory refused");

                // File names.
                check(UnifiedExchangeLogic.ValidateFileName(" Tags_1 ") == "Tags_1" && UnifiedExchangeLogic.ValidateFileName("") == "", "exchange: file name trimmed, empty allowed");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateFileName("a/b")), "exchange: path separator in file name refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateFileName("..")), "exchange: dot-dot file name refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateFileName(new string('x', 129))), "exchange: oversized file name refused");

                // Expected names.
                check(UnifiedExchangeLogic.ParseExpectedNames("[]").Length == 0 && UnifiedExchangeLogic.ParseExpectedNames("").Length == 0, "exchange: empty expected names");
                check(UnifiedExchangeLogic.ParseExpectedNames("[\"Tag_1\",\"Tag_2\"]").SequenceEqual(new[] { "Tag_1", "Tag_2" }), "exchange: expected names parsed");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ParseExpectedNames("[\"a\",\"A\"]")), "exchange: duplicate names (case-insensitive) refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ParseExpectedNames("[1]")), "exchange: non-string name refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ParseExpectedNames("{}")), "exchange: non-array refused");

                // OPC UA alarm xml path.
                check(UnifiedExchangeLogic.ValidateXmlPath(Path.Combine(temp, "alarms.XML")).EndsWith("alarms.XML"), "exchange: xml path accepted case-insensitively");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateXmlPath(Path.Combine(temp, "alarms.txt"))), "exchange: non-xml refused");
                check(Fails<ArgumentException>(() => UnifiedExchangeLogic.ValidateXmlPath("alarms.xml")), "exchange: relative xml path refused");

                // Native export files: inside the directory, existing, nonempty, hashed.
                var exportDir = new DirectoryInfo(Path.Combine(temp, "out")); exportDir.Create();
                var good = new FileInfo(Path.Combine(exportDir.FullName, "Table.hmi.yml")); File.WriteAllText(good.FullName, "#Version: 2.0\nSimpleTags:\n");
                var rows = UnifiedExchangeLogic.VerifyNativeFiles(new[] { good }, exportDir);
                check(rows.Count == 1 && rows[0]!["bytes"]!.GetValue<long>() > 0 && rows[0]!["sha256"]!.GetValue<string>().Length == 64, "exchange: native file hashed");
                var empty = new FileInfo(Path.Combine(exportDir.FullName, "Empty.hmi.yml")); File.WriteAllText(empty.FullName, "");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.VerifyNativeFiles(new[] { empty }, exportDir)), "exchange: empty native file refused");
                var outside = new FileInfo(Path.Combine(temp, "Outside.hmi.yml")); File.WriteAllText(outside.FullName, "x");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.VerifyNativeFiles(new[] { outside }, exportDir)), "exchange: file outside the export directory refused");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.VerifyNativeFiles(new FileInfo[0], exportDir)), "exchange: no files refused");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.VerifyNativeFiles("not a sequence", exportDir)), "exchange: non-sequence native result refused");
                check(Fails<InvalidOperationException>(() => UnifiedExchangeLogic.VerifyNativeFiles(new object[] { 5 }, exportDir)), "exchange: non-FileInfo entry refused");

                // Import candidates by extension.
                var candidates = UnifiedExchangeLogic.ListImportCandidates(exportDir, new[] { ".hmi.yml" });
                check(candidates.Count == 2 && candidates[0]!["name"]!.GetValue<string>() == "Empty.hmi.yml", "exchange: import candidates listed by extension, sorted");
                check(UnifiedExchangeLogic.ListImportCandidates(exportDir, new[] { ".js" }).Count == 0, "exchange: no candidates for another extension");
                check(!UnifiedExchangeLogic.IsUnifiedTagCollection("x") && !UnifiedExchangeLogic.IsUnifiedTagCollection(null), "exchange: tag collection type check is exact");
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
    }
}
