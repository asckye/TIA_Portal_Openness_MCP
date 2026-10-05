using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class CandidateIdentity
    {
        public int? ProcessId { get; set; }
        public DateTimeOffset? ProcessStartUtc { get; set; }
        public string? ProjectFile { get; set; }
        public long? BindingEpoch { get; set; }
        public CandidateIdentity() { }
        public CandidateIdentity(int? processId, DateTimeOffset? processStartUtc, string? projectFile, long? bindingEpoch)
        { ProcessId = processId; ProcessStartUtc = processStartUtc?.ToUniversalTime(); ProjectFile = projectFile; BindingEpoch = bindingEpoch; }
    }

    public sealed class CandidateFile
    {
        public string Path { get; set; } = "";
        public bool Exists { get; set; }
        public long? ByteLength { get; set; }
        public string? Sha256 { get; set; }
    }

    // Native observations identify the failed boundary; hosts own V4 errors and envelopes.
    public sealed class CandidateFault
    {
        public string Kind { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Message { get; set; } = "";
        public long Limit { get; set; }
        public long Actual { get; set; }
    }
    public sealed class CandidateObservationException : Exception
    {
        public CandidateFault Fault { get; }
        public CandidateObservationException(CandidateFault fault) : base(fault.Message) { Fault = fault; }
    }
    public static class CandidateImportNames
    {
        public static bool IsDirectory(string entry) => entry.EndsWith("FromDirectory", StringComparison.Ordinal) || entry == "ImportPlcBlocksDocuments";
        public static bool IsDocuments(string entry) => entry == "ImportPlcBlockDocuments" || entry == "ImportPlcBlocksDocuments";
    }
    public static class CandidatePrimitives
    {
        public static void Fail(string kind, string subject, string message = "")
            => throw new CandidateObservationException(new CandidateFault { Kind = kind, Subject = subject, Message = message });
        public static void Invalid(string parameter) => Fail("invalid", parameter, "Invalid PLC import argument.");
        public static void Unsupported(string release, string action) => Fail("unsupported", action, release);
        public static void NotFound(string target) => Fail("not-found", target, "Exact target group not found.");
        public static void Refuse(string message, string subject, long limit, long actual)
            => throw new CandidateObservationException(new CandidateFault { Kind = "limit", Subject = subject, Message = message, Limit = limit, Actual = actual });
        public static string CanonicalProject(string path) => Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        public static string ByteHash(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static byte[] Read(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek || stream.Length < 1 || stream.Length > 16 * 1024 * 1024) Invalid("file-size");
            long length = stream.Length; stream.Position = 0;
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            { if (memory.Length + count > 16 * 1024 * 1024) Invalid("file-size"); memory.Write(buffer, 0, count); }
            if (length != memory.Length || stream.Length != length) throw new IOException("Input changed while locked.");
            return memory.ToArray();
        }
        public static CandidateFile[] Files(IDictionary<string, Stream> locks)
        {
            var files = locks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => { var bytes = Read(p.Value);
                return new CandidateFile { Path = p.Key, Exists = true, ByteLength = bytes.LongLength, Sha256 = ByteHash(bytes) }; }).ToArray();
            long length = files.Sum(f => f.ByteLength!.Value);
            if (length > 128 * 1024 * 1024) Refuse("The aggregate file budget is exceeded.", "inputBytes", 128 * 1024 * 1024, length);
            return files;
        }
    }

    // Versioned, length-prefixed UTF-8 fields and little-endian integers. Null has
    // its own tag; inventory/files sort ordinally, input order remains meaningful.
    // This encoding is only for native observations, never the V4 plan hash.
    public static class CandidateDigest
    {
        private static string Encode(string kind, Action<BinaryWriter> write)
        { using var bytes = new MemoryStream(); using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true)) { Text(writer, "candidate-observation-v1"); Text(writer, kind); write(writer); }
            return CandidatePrimitives.ByteHash(bytes.ToArray()); }
        private static void Text(BinaryWriter writer, string? text)
        { if (text == null) { writer.Write(-1); return; } var bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
        private static void Number(BinaryWriter writer, long? value)
        { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
        private static void Identity(BinaryWriter w, CandidateIdentity i)
        { Number(w, i.ProcessId); Number(w, i.ProcessStartUtc?.UtcDateTime.Ticks); Text(w, i.ProjectFile == null ? null : CandidatePrimitives.CanonicalProject(i.ProjectFile)); Number(w, i.BindingEpoch); }
        private static void Device(BinaryWriter w, DeviceInventoryItem i)
        { Text(w, i.Id); Text(w, i.Name); Text(w, i.ParentId); w.Write(i.IsGroup); }
        private static void Catalog(BinaryWriter w, DeviceCatalogEntry i)
        { Text(w, i.TypeIdentifier); Text(w, i.ArticleNumber); Text(w, i.Version); Text(w, i.TypeName); Text(w, i.Description); Text(w, i.CatalogPath); }
        private static void Object(BinaryWriter w, PlcImportObject i)
        { Text(w, i.Id); Text(w, i.Kind); Text(w, i.Name); Text(w, i.GroupPath); Number(w, i.Number); Text(w, i.ContentHash); }
        private static void Input(BinaryWriter w, PlcImportInput i)
        { Text(w, i.Path); w.Write(i.Files.Length); foreach (var f in i.Files) Text(w, f); Object(w, i.Target); Text(w, i.ContentHash); w.Write(i.Documents); }
        private static void List<T>(BinaryWriter w, IEnumerable<T> rows, Action<BinaryWriter, T> write)
        { var array = rows.ToArray(); w.Write(array.Length); foreach (var row in array) write(w, row); }
        public static string Binding(CandidateIdentity identity) => Encode("binding", w => Identity(w, identity));
        public static string DeviceRow(DeviceInventoryItem row) => Encode("device-row", w => Device(w, row));
        public static string DeviceInventory(IEnumerable<DeviceInventoryItem> rows) => Encode("devices", w => List(w, rows.OrderBy(r => r.Id, StringComparer.Ordinal), Device));
        public static string DeviceObservation(CandidateIdentity identity, DeviceCatalogEntry catalog, IEnumerable<DeviceInventoryItem> rows, string type, string name)
            => Encode("device-create", w => { Identity(w, identity); Catalog(w, catalog); List(w, rows.OrderBy(r => r.Id, StringComparer.Ordinal), Device); Text(w, type); Text(w, name); });
        public static string ImportRow(PlcImportObject row) => Encode("import-row", w => Object(w, row));
        public static string ImportInventory(IEnumerable<PlcImportObject> rows) => Encode("imports", w => List(w, rows.OrderBy(r => r.Id, StringComparer.Ordinal), Object));
        public static string Inputs(IEnumerable<PlcImportInput> rows) => Encode("inputs", w => List(w, rows, Input));
        public static string Files(IEnumerable<CandidateFile> rows) => Encode("files", w => List(w, rows.OrderBy(r => r.Path, StringComparer.Ordinal), (b, f) =>
            { Text(b, f.Path); b.Write(f.Exists); Number(b, f.ByteLength); Text(b, f.Sha256); }));
        public static string ImportObservation(PlcImportCheck check) => Encode("import-item", w =>
        { Identity(w, check.Identity); Text(w, check.Release); Text(w, check.Tool); var r = check.Request;
            Text(w, r.SoftwarePath); Text(w, r.InputPath); Text(w, r.BlockGroupPath); Text(w, r.TypeGroupPath); Text(w, r.TagFolderPath); Text(w, r.TechnologyFolderPath);
            Text(w, r.RegexName); Text(w, r.FileNameWithoutExtension); List(w, r.ImportOrder, Text); Text(w, r.VersionPolicy); Text(w, r.OnError); w.Write(r.CompileAfter); w.Write(r.MaxItems);
            Text(w, Inputs(check.Inputs)); Text(w, Files(check.Files));
            Text(w, ImportInventory(check.CurrentInventory)); w.Write(check.Index); Text(w, check.GroupIdentity); w.Write(check.Request.Overwrite); w.Write(check.OverwriteSupported); });
        public static string Document(string code, string? resource) => Encode("documents", w => { Text(w, code); Text(w, resource); });
    }
}
