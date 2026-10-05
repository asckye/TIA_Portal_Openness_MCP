using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Injectable boundary permits failure-order tests without loading Openness.
    public static class PlcVerifiedImport
    {
        private static readonly string Session = Guid.NewGuid().ToString("N");
        public static string Execute(string candidateXml, string binding, string evidenceRoot, bool dryRun, string expectedToken,
            Action<string> export, Action<string> import, Action verifyBinding, JsonObject meta, Action? compile = null)
        {
            meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["operationSuccess"] = false;
            meta["verificationSuccess"] = false; meta["automaticRollback"] = false; meta["compileRequested"] = compile != null;
            PlcDocumentEditing.Parse(candidateXml);
            if (!dryRun && string.IsNullOrWhiteSpace(expectedToken)) throw new ArgumentException("Preview expectedToken is required.");
            if (!Path.IsPathRooted(evidenceRoot)) throw new ArgumentException("Absolute evidence directory required.");
            var root = new DirectoryInfo(Path.GetFullPath(evidenceRoot));
            for (var p = root; p != null; p = p.Parent)
                if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse evidence paths refused.");
            var folder = Path.Combine(root.FullName, "plc-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            meta["evidenceDirectory"] = folder;
            var beforePath = Path.Combine(folder, "before.xml");
            var plannedPath = Path.Combine(folder, "planned.xml");
            var afterPath = Path.Combine(folder, "after.xml");
            meta["phase"] = "exportBefore"; verifyBinding(); export(beforePath);
            var before = PlcDocumentEditing.Read(beforePath);
            meta["backup"] = new JsonObject { ["path"] = beforePath, ["sha256"] = PlcDocumentEditing.Hash(File.ReadAllBytes(beforePath)) };
            var preserved = new JsonArray();
            var planned = PlcDocumentEditing.PrepareImport(before, candidateXml, preserved);
            var expected = PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(planned));
            var tokenData = new JsonObject { ["session"] = Session, ["binding"] = binding, ["compileRequested"] = compile != null,
                ["before"] = PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(before)), ["planned"] = expected };
            var token = PlcDocumentEditing.HashText(tokenData.ToJsonString());
            meta["token"] = token; meta["preservedOmittedBlockAttributes"] = preserved;
            meta["plannedFingerprint"] = PlcDocumentEditing.HashText(expected);
            using (var file = new FileStream(plannedPath, FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = new UTF8Encoding(true).GetPreamble(); file.Write(bytes, 0, bytes.Length);
                bytes = Encoding.UTF8.GetBytes(planned); file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            meta["plannedPath"] = plannedPath;
            meta["scope"] = "Existing non-library root/user-group PLC block. Full replacement; missing scalar block attributes are preserved, but missing interface members/networks are intentional deletions. Compile only when explicitly requested. No save, download, automatic retry or rollback.";
            if (dryRun) { meta["phase"] = "preview"; meta["operationSuccess"] = true; return "Preview and original backup retained. Inspect planned.xml; apply the same candidate and token to overwrite."; }
            if (!string.Equals(token, expectedToken, StringComparison.Ordinal)) throw new InvalidOperationException("Preview no longer matches binding/current block/candidate. No import attempted.");
            verifyBinding();
            meta["phase"] = "import"; meta["mayHaveChanged"] = true;
            import(plannedPath); // An exception exits immediately: no second native call or automatic retry.
            meta["importReturned"] = true;
            if (compile != null) { meta["phase"] = "compile"; verifyBinding(); compile(); meta["compileReturned"] = true; }
            meta["phase"] = "exportReadback";
            verifyBinding(); export(afterPath);
            var actual = PlcDocumentEditing.Canonical(PlcDocumentEditing.Parse(PlcDocumentEditing.Read(afterPath)));
            var matches = expected == actual;
            meta["afterPath"] = afterPath; meta["actualFingerprint"] = PlcDocumentEditing.HashText(actual);
            meta["verificationSuccess"] = matches; meta["operationSuccess"] = matches;
            meta["status"] = matches ? "DocumentReadbackMatched" : "DocumentReadbackMismatch";
            meta["phase"] = "complete";
            return matches ? "Imported and re-exported document matches after identifier normalization. Inspect compileRequested/compileReturned for compilation; project was not saved."
                : "Import returned, but document readback differs. Original backup and both documents retained. No automatic repair or rollback was attempted.";
        }
    }
}
