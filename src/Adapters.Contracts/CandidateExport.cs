using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class PlcExportRequest
    {
        public string SoftwarePath { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string WorkspaceRoot { get; set; } = "";
        public string RegexName { get; set; } = "";
        public bool PreservePath { get; set; }
        public bool Recursive { get; set; }
        public bool Overwrite { get; set; }
        public string OnError { get; set; } = "stop";
        public int MaxItems { get; set; } = 128;
    }
    public sealed class PlcExportObject
    {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Language { get; set; } = "";
        public bool Consistent { get; set; }
        public string? ContentHash { get; set; }
        public string ContentObservation { get; set; } = "unavailable";
    }
    public interface IPlcExportAdapter
    {
        CandidateIdentity ReadIdentity();
        IReadOnlyList<PlcExportObject> ReadObjects(string tool, PlcExportRequest request);
        bool SupportsOverwrite(string tool);
        void BeforeExport(PlcExportObject item);
        void Export(PlcExportObject item, string stagingPath, bool documents);
    }
    public interface IExportCandidateBoundary { PlcExportAttempt Execute(PlcExportCheck check); }
    public sealed class PlcExportCheck
    {
        public string Release { get; set; } = "";
        public string Tool { get; set; } = "";
        public PlcExportRequest Request { get; set; } = new PlcExportRequest();
        public CandidateIdentity Identity { get; set; } = new CandidateIdentity();
        public PlcExportObject[] Objects { get; set; } = Array.Empty<PlcExportObject>();
        public int Index { get; set; }
        public CandidateFile Destination { get; set; } = new CandidateFile();
        public string StagingPath { get; set; } = "";
        public bool Documents { get; set; }
        public bool OverwriteSupported { get; set; }
        public string Digest { get; set; } = "";
    }
    public sealed class PlcExportAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public CandidateFault? Fault { get; set; }
        public CandidateFile[] Staged { get; set; } = Array.Empty<CandidateFile>();
        public CandidateFile[] Residue { get; set; } = Array.Empty<CandidateFile>();
        public string ResidueStatus { get; set; } = "unavailable";
    }
    public sealed class ExportCandidateCall
    {
        public string Action { get; set; } = "identity";
        public string Tool { get; set; } = "";
        public PlcExportRequest Request { get; set; } = new PlcExportRequest();
        public PlcExportCheck? Check { get; set; }
    }
    public sealed class ExportCandidateReply : TiaMcp.Adapters.Contracts.IWorkerOperationReply
    {
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.MayHaveChanged => Attempt?.Issued == true;
        bool TiaMcp.Adapters.Contracts.IWorkerOperationReply.BlockReadsAfterUncertain => true;
        public CandidateIdentity? Identity { get; set; }
        public PlcExportObject[]? Objects { get; set; }
        public bool OverwriteSupported { get; set; }
        public PlcExportAttempt? Attempt { get; set; }
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }

    // Bounded filesystem observations contain no plan, confirmation or JSON state.
    public static class CandidateExportFiles
    {
        public static void SafePath(string path)
        {
            if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path || path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0) CandidatePrimitives.Invalid("exportPath");
            for (var parent = new FileInfo(path) as FileSystemInfo; parent != null; parent = parent is FileInfo file ? file.Directory : ((DirectoryInfo)parent).Parent)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(parent.FullName); }
                catch (FileNotFoundException) /* swallow(probe-optional): a missing output component has no reparse attributes yet */ { continue; }
                catch (DirectoryNotFoundException) /* swallow(probe-optional): a missing parent is represented by the absent output identity */ { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) CandidatePrimitives.Invalid("reparse-point");
            }
        }
        public static CandidateFile Observe(string path)
        {
            SafePath(path);
            if (Directory.Exists(path))
            {
                var entries = new List<CandidateFile>();
                void Walk(string dir, int depth)
                {
                    if (depth > 32) CandidatePrimitives.Invalid("output-depth");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if (entries.Count >= 4096) CandidatePrimitives.Invalid("output-inventory");
                        SafePath(entry);
                        if (Directory.Exists(entry)) { entries.Add(new CandidateFile { Path = entry, Exists = true, ByteLength = 0, Sha256 = CandidatePrimitives.ByteHash(Array.Empty<byte>()) }); Walk(entry, depth + 1); }
                        else entries.Add(Observe(entry));
                    }
                }
                Walk(path, 0);
                return new CandidateFile { Path = path, Exists = true, ByteLength = entries.Sum(f => f.ByteLength!.Value),
                    Sha256 = CandidateDigest.Files(entries.Select(f => new CandidateFile { Path = f.Path.Substring(path.Length), Exists = f.Exists, ByteLength = f.ByteLength, Sha256 = f.Sha256 }).ToArray()) };
            }
            if (!File.Exists(path)) return new CandidateFile { Path = path };
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024 * 1024) CandidatePrimitives.Invalid("output-size");
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            if (stream.Length != bytes.Length) throw new IOException("Output changed while read.");
            return new CandidateFile { Path = path, Exists = true, ByteLength = bytes.Length, Sha256 = CandidatePrimitives.ByteHash(bytes.ToArray()) };
        }
        public static string Objects(IEnumerable<PlcExportObject> rows)
        {
            using var bytes = new MemoryStream(); using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
                foreach (var r in rows) { w.Write(r.Id); w.Write(r.Path); w.Write(r.Name); w.Write(r.Kind); w.Write(r.Language); w.Write(r.Consistent); w.Write(r.ContentHash ?? ""); w.Write(r.ContentObservation); }
            return CandidatePrimitives.ByteHash(bytes.ToArray());
        }
        public static string Digest(PlcExportCheck c)
        {
            using var bytes = new MemoryStream(); using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                w.Write("export-observation-v1"); w.Write(c.Release); w.Write(c.Tool); w.Write(CandidateDigest.Binding(c.Identity)); w.Write(Objects(c.Objects)); w.Write(c.Index);
                w.Write(CandidateDigest.Files(new[] { c.Destination })); w.Write(c.StagingPath); w.Write(c.Documents); w.Write(c.OverwriteSupported);
                var r = c.Request; w.Write(r.SoftwarePath); w.Write(r.ObjectPath); w.Write(r.GroupPath); w.Write(r.OutputPath); w.Write(r.WorkspaceRoot);
                w.Write(r.RegexName); w.Write(r.PreservePath); w.Write(r.Recursive); w.Write(r.Overwrite); w.Write(r.OnError); w.Write(r.MaxItems);
            }
            return CandidatePrimitives.ByteHash(bytes.ToArray());
        }
        public static CandidateFile[] Staged(PlcExportCheck check)
        {
            SafePath(check.StagingPath);
            var files = check.Documents ? Directory.GetFiles(check.StagingPath).OrderBy(p => p, StringComparer.Ordinal).ToArray() : new[] { check.StagingPath };
            if (check.Documents && (Directory.GetDirectories(check.StagingPath).Length != 0 || files.Length < 1 || files.Length > 2
                || !files.Contains(Path.Combine(check.StagingPath, check.Objects[check.Index].Name + ".s7dcl"), StringComparer.Ordinal)
                || files.Any(p => p != Path.Combine(check.StagingPath, check.Objects[check.Index].Name + ".s7dcl") && p != Path.Combine(check.StagingPath, check.Objects[check.Index].Name + ".s7res"))))
                throw new InvalidDataException("Unexpected document output set.");
            if (check.Documents && check.Release == "20" && files.Length != 2) throw new InvalidDataException("V20 document resources are required.");
            var result = files.Select(Observe).ToArray();
            if (result.Any(f => !f.Exists || f.ByteLength < 1)) throw new InvalidDataException("Native export did not produce regular nonempty files.");
            return result;
        }
    }

    public static partial class CandidateExecution
    {
        public static PlcExportAttempt Export(IPlcExportAdapter adapter, PlcExportCheck check)
        {
            var result = new PlcExportAttempt();
            try
            {
                if (check.Index < 0 || check.Index >= check.Objects.Length || check.Digest != CandidateExportFiles.Digest(check)) CandidatePrimitives.Invalid("observation");
                void Recheck()
                {
                    Identity(check.Identity, adapter.ReadIdentity());
                    Stale(CandidateExportFiles.Objects(check.Objects) != CandidateExportFiles.Objects(adapter.ReadObjects(check.Tool, check.Request)), "objects-changed");
                    Stale(CandidateDigest.Files(new[] { check.Destination }) != CandidateDigest.Files(new[] { CandidateExportFiles.Observe(check.Destination.Path) }), "destination-changed");
                    Stale(check.OverwriteSupported != adapter.SupportsOverwrite(check.Tool), "overwrite-changed");
                    Stale(check.Digest != CandidateExportFiles.Digest(check), "arguments-changed");
                }
                if (!check.OverwriteSupported && check.Request.PreservePath && !Directory.Exists(Path.GetDirectoryName(check.Destination.Path)!))
                    CandidatePrimitives.Unsupported(check.Release, "preservePath-parent");
                Recheck(); adapter.BeforeExport(check.Objects[check.Index]); Recheck();
                CandidateExportFiles.SafePath(check.StagingPath);
                result.Issued = true;
                adapter.Export(check.Objects[check.Index], check.StagingPath, check.Documents);
                Identity(check.Identity, adapter.ReadIdentity());
                result.Staged = CandidateExportFiles.Staged(check);
            }
            catch (Exception ex)
            {
                result.Fault = Fault(ex); result.RequiresSessionReset = result.Issued && !(result.Fault.Kind == "native-result" && NativeResultStates.Classify(result.Fault.Subject, result.Fault.Message) == NativeStateKind.Failure);
                try { result.Residue = new[] { CandidateExportFiles.Observe(check.StagingPath), CandidateExportFiles.Observe(check.Destination.Path) }; result.ResidueStatus = "checked"; }
                catch (Exception) /* swallow(privacy): preserve unavailable residue when export-state observation fails */ { }
            }
            return result;
        }
    }
}
