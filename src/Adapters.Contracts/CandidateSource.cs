using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class PlcPathTarget<T>
    {
        public T Value { get; set; } = default!;
        public string Path { get; set; } = "";
        public string[] QueryNames { get; set; } = Array.Empty<string>();
    }
    public sealed class PlcPathException : Exception
    {
        public string Path { get; }
        public string[] Candidates { get; }
        public bool Ambiguous { get; }
        public PlcPathException(string path, string[] candidates, bool ambiguous)
            : base(ambiguous ? "PLC target is ambiguous." : "Exact PLC target not found.")
        { Path = path; Candidates = candidates; Ambiguous = ambiguous; }
    }
    public static class PlcPathSelection
    {
        // Query addresses are opaque: do not trim, decode, fold case or remove segments.
        public static PlcPathTarget<T> Select<T>(IEnumerable<PlcPathTarget<T>> inventory, string path)
        {
            var all = inventory.ToArray();
            if (all.Length > 4096 || all.Any(r => r == null || r.Path.Length == 0)) CandidatePrimitives.Fail("precondition", "complete-plc-inventory");
            var matches = path == "" ? all : all.Where(r => r.Path == path || r.QueryNames.Contains(path, StringComparer.Ordinal)).ToArray();
            if (matches.Length != 1) throw new PlcPathException(path, (path == "" ? all : matches).Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray(), path == "" || matches.Length > 1);
            return matches[0];
        }
    }
    public sealed class SourceItem
    {
        public string Action { get; set; } = "import";
        public string SourceName { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
    public sealed class SourceRequest
    {
        public string SoftwarePath { get; set; } = "";
        public string ReadGroup { get; set; } = "";
        public string ReadSource { get; set; } = "";
        public SourceItem[] Items { get; set; } = Array.Empty<SourceItem>();
        public bool Overwrite { get; set; }
        public string OnError { get; set; } = "stop";
        public string MissingPolicy { get; set; } = "reject";
    }
    public sealed class SourceRow
    {
        public string Id { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ContentHash { get; set; } = "";
        // Only retained for a source imported by this native adapter in this session.
        public string[] Declarations { get; set; } = Array.Empty<string>();
    }
    public sealed class SourceObservation
    {
        public CandidateIdentity Identity { get; set; } = new CandidateIdentity();
        public string PlcId { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string[] Groups { get; set; } = Array.Empty<string>();
        public string[] GroupIds { get; set; } = Array.Empty<string>();
        public SourceRow[] Sources { get; set; } = Array.Empty<SourceRow>();
        public string[] Objects { get; set; } = Array.Empty<string>();
    }
    public sealed class SourceCheck
    {
        public string Release { get; set; } = "";
        public SourceRequest Request { get; set; } = new SourceRequest();
        public SourceObservation Before { get; set; } = new SourceObservation();
        public CandidateFile[] Files { get; set; } = Array.Empty<CandidateFile>();
        public int Index { get; set; }
        public string Digest { get; set; } = "";
    }
    public sealed class SourceAttempt
    {
        public bool Issued { get; set; }
        public bool RequiresSessionReset { get; set; }
        public SourceObservation? After { get; set; }
        public string[]? NativeResult { get; set; }
        public string[]? Observation { get; set; }
        public CandidateFault? Fault { get; set; }
    }
    public sealed class SourceCall
    {
        public string Action { get; set; } = "observe";
        public SourceRequest Request { get; set; } = new SourceRequest();
        public SourceCheck? Check { get; set; }
    }
    public sealed class SourceReply
    {
        public SourceObservation? Observation { get; set; }
        public SourceAttempt? Attempt { get; set; }
        public string[] PathCandidates { get; set; } = Array.Empty<string>();
        public CandidateFault? Fault { get; set; }
        public bool RequiresSessionReset { get; set; }
    }
    public interface ISourceAdapter
    {
        SourceObservation Observe();
        void BeforeAction(SourceItem item);
        SourceRow Import(SourceItem item, string[] declarations);
        string[]? Generate(SourceRow source);
        void Delete(SourceRow source);
    }
    public interface ISourceBoundary { SourceAttempt Execute(SourceCheck check, IDictionary<string, Stream> locks); }
    public static class SourceDigest
    {
        private static void Text(BinaryWriter w, string? value) { w.Write(value != null); if (value != null) w.Write(value); }
        private static void Row(BinaryWriter w, SourceObservation o)
        {
            Text(w, CandidateDigest.Binding(o.Identity)); Text(w, o.PlcId); Text(w, o.SoftwarePath);
            w.Write(o.Groups.Length); foreach (var g in o.Groups.OrderBy(x => x, StringComparer.Ordinal)) Text(w, g);
            w.Write(o.GroupIds.Length); foreach (var g in o.GroupIds) Text(w, g);
            w.Write(o.Sources.Length); foreach (var s in o.Sources.OrderBy(x => x.Id, StringComparer.Ordinal))
            { Text(w, s.Id); Text(w, s.GroupPath); Text(w, s.Name); Text(w, s.FilePath); Text(w, s.ContentHash); w.Write(s.Declarations.Length); foreach (var d in s.Declarations) Text(w, d); }
            w.Write(o.Objects.Length); foreach (var n in o.Objects.OrderBy(x => x, StringComparer.Ordinal)) Text(w, n);
        }
        public static string Observation(SourceObservation o)
        { using var bytes = new MemoryStream(); using (var w = new BinaryWriter(bytes, Encoding.UTF8, true)) Row(w, o); return CandidatePrimitives.ByteHash(bytes.ToArray()); }
        public static string Check(SourceCheck c)
        {
            using var bytes = new MemoryStream(); using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                Text(w, "source-check-v1"); Text(w, c.Release); Row(w, c.Before); w.Write(c.Index);
                Text(w, c.Request.SoftwarePath); Text(w, c.Request.ReadGroup); Text(w, c.Request.ReadSource); w.Write(c.Request.Overwrite); Text(w, c.Request.OnError); Text(w, c.Request.MissingPolicy);
                w.Write(c.Request.Items.Length); foreach (var i in c.Request.Items) { Text(w, i.Action); Text(w, i.SourceName); Text(w, i.GroupPath); Text(w, i.FilePath); }
                Text(w, CandidateDigest.Files(c.Files));
            }
            return CandidatePrimitives.ByteHash(bytes.ToArray());
        }
    }
    public static partial class CandidateExecution
    {
        public static void ValidateSourceObservation(SourceObservation o)
        {
            if (o == null || o.PlcId.Length == 0 || o.SoftwarePath.Length == 0 || o.Groups.Length > 4096 || o.GroupIds.Length != o.Groups.Length || o.GroupIds.Distinct(StringComparer.Ordinal).Count() != o.GroupIds.Length || o.Sources.Length > 4096 || o.Objects.Length > 4096
                || o.Groups.Distinct(StringComparer.Ordinal).Count() != o.Groups.Length
                || o.Sources.Any(s => s == null || s.Id.Length == 0 || s.Name.Length == 0 || !o.Groups.Contains(s.GroupPath, StringComparer.Ordinal))
                || o.Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != o.Sources.Length
                || o.Sources.Select(s => s.GroupPath + "\0" + s.Name).Distinct(StringComparer.Ordinal).Count() != o.Sources.Length)
                CandidatePrimitives.Fail("precondition", "complete-source-inventory");
        }
        public static SourceRow? SourceTarget(SourceObservation o, SourceItem i)
            => o.Sources.SingleOrDefault(s => s.GroupPath == i.GroupPath && s.Name == i.SourceName);
        public static void VerifySourceReadback(SourceCheck c, SourceAttempt a)
        {
            var after = a.After ?? throw new InvalidDataException("Missing source readback.");
            ValidateSourceObservation(after); Identity(c.Before.Identity, after.Identity);
            if (after.PlcId != c.Before.PlcId || after.SoftwarePath != c.Before.SoftwarePath) throw new InvalidDataException("PLC changed after source write.");
            var i = c.Request.Items[c.Index]; var before = SourceTarget(c.Before, i); var actual = SourceTarget(after, i);
            if (i.Action == "import" && (actual == null || before != null || after.Sources.Length != c.Before.Sources.Length + 1)
                || i.Action == "delete" && (actual != null || before == null || after.Sources.Length != c.Before.Sources.Length - 1)
                || i.Action == "generate" && (actual == null || before == null || actual.Id != before.Id || after.Sources.Length != c.Before.Sources.Length))
                throw new InvalidDataException("Source write delta differs from the plan.");
            if (i.Action == "import")
            {
                var file = c.Files.Single(f => f.Path == i.FilePath);
                if (actual!.FilePath != file.Path || actual.ContentHash != file.Sha256) throw new InvalidDataException("Imported source input identity differs from the plan.");
            }
            foreach (var s in c.Before.Sources.Where(s => s.Id != before?.Id || i.Action != "delete"))
                if (!after.Sources.Any(t => t.Id == s.Id && t.GroupPath == s.GroupPath && t.Name == s.Name && t.FilePath == s.FilePath && t.ContentHash == s.ContentHash && t.Declarations.SequenceEqual(s.Declarations))) throw new InvalidDataException("Unplanned source change.");
            if (!after.GroupIds.SequenceEqual(c.Before.GroupIds)) throw new InvalidDataException("Source group identity changed.");
            if (!after.Groups.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(c.Before.Groups.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidDataException("Source group changed.");
            if (i.Action == "generate")
            {
                if (before!.Declarations.Length == 0 || before.Declarations.Any(n => !after.Objects.Contains(n, StringComparer.Ordinal)) || c.Before.Objects.Any(n => !after.Objects.Contains(n, StringComparer.Ordinal))) throw new InvalidDataException("Generated objects could not be verified.");
                if (c.Release == "14sp1" ? a.NativeResult != null || a.Observation == null : a.NativeResult == null || a.Observation != null) throw new InvalidDataException("Generation evidence origin differs from release.");
                var result = a.NativeResult ?? a.Observation!;
                if (!result.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(before.Declarations.OrderBy(n => n, StringComparer.Ordinal))
                    || !after.Objects.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(c.Before.Objects.Concat(before.Declarations).OrderBy(n => n, StringComparer.Ordinal))) throw new InvalidDataException("Unexpected generation result or inventory delta.");
            }
            else if (!after.Objects.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(c.Before.Objects.OrderBy(n => n, StringComparer.Ordinal))) throw new InvalidDataException("Unplanned block/type change.");
        }
        public static SourceAttempt Source(ISourceAdapter adapter, SourceCheck check, IDictionary<string, Stream> locks)
        {
            var a = new SourceAttempt();
            try
            {
                if (check.Index < 0 || check.Index >= check.Request.Items.Length || check.Digest != SourceDigest.Check(check)) CandidatePrimitives.Invalid("source-check");
                void Recheck()
                {
                    var fresh = adapter.Observe(); ValidateSourceObservation(fresh); Identity(check.Before.Identity, fresh.Identity);
                    Stale(SourceDigest.Observation(check.Before) != SourceDigest.Observation(fresh), "source-inventory-changed");
                    Stale(CandidateDigest.Files(check.Files) != CandidateDigest.Files(CandidatePrimitives.Files(locks)), "source-files-changed");
                    Stale(check.Digest != SourceDigest.Check(check), "source-arguments-changed");
                }
                Recheck(); var i = check.Request.Items[check.Index]; var target = SourceTarget(check.Before, i);
                if (!check.Before.Groups.Contains(i.GroupPath, StringComparer.Ordinal)) CandidatePrimitives.NotFound(i.GroupPath);
                if (i.Action == "import" && target != null) CandidatePrimitives.Fail("exists", i.SourceName);
                if (i.Action != "import" && target == null) CandidatePrimitives.NotFound(i.SourceName);
                if (i.Action == "generate" && (target!.Declarations.Length == 0 || target.Declarations.Any(n => check.Before.Objects.Contains(n, StringComparer.OrdinalIgnoreCase)))) CandidatePrimitives.Fail("precondition", "verified-new-generation-targets");
                var declarations = i.Action == "import" ? SourceDeclarations.TryRead(CandidatePrimitives.Read(locks[i.FilePath])) : Array.Empty<string>();
                adapter.BeforeAction(i); Recheck();
                a.Issued = true;
                if (i.Action == "import")
                { var created = adapter.Import(i, declarations); if (created.Name != i.SourceName || created.GroupPath != i.GroupPath) throw new InvalidDataException("Native source import returned a different identity."); }
                else if (i.Action == "delete") adapter.Delete(target!);
                else if (i.Action == "generate") a.NativeResult = adapter.Generate(target!);
                else throw new InvalidDataException("Unexpected source action.");
                a.After = adapter.Observe();
                if (i.Action == "generate" && check.Release == "14sp1") a.Observation = a.After.Objects.Except(check.Before.Objects, StringComparer.Ordinal).ToArray();
                Stale(CandidateDigest.Files(check.Files) != CandidateDigest.Files(CandidatePrimitives.Files(locks)), "source-files-changed-after-native");
                VerifySourceReadback(check, a);
            }
            catch (Exception ex)
            { a.Fault = Fault(ex); a.RequiresSessionReset = a.Issued; }
            return a;
        }
    }
    public static class SourceDeclarations
    {
        // A bounded SCL subset establishes collision names; native syntax acceptance remains L5.
        public static string[] TryRead(byte[] bytes)
        {
            try { return Read(bytes); }
            catch (CandidateObservationException) /* swallow(probe-optional): native import remains available; unproven declarations refuse later generation */ { return Array.Empty<string>(); }
            catch (DecoderFallbackException) /* swallow(probe-optional): preserve original native source bytes without guessing a non-UTF8 declaration name */ { return Array.Empty<string>(); }
        }
        public static string[] Read(byte[] bytes)
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var tokens = new List<string>();
            int offset = 0;
            while (offset < text.Length)
            {
                char c = text[offset];
                if (char.IsWhiteSpace(c)) { offset++; continue; }
                if (c == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                if (c == '(' && offset + 1 < text.Length && text[offset + 1] == '*')
                {
                    int end = text.IndexOf("*)", offset + 2, StringComparison.Ordinal);
                    if (end < 0 || text.IndexOf("(*", offset + 2, end - offset - 2, StringComparison.Ordinal) >= 0) CandidatePrimitives.Fail("unsupported", "source-comments");
                    offset = end + 2; continue;
                }
                if (c == '\'' || c == '"')
                {
                    int start = offset++; bool closed = false;
                    while (offset < text.Length)
                    {
                        if (text[offset] == '$') { offset += 2; continue; }
                        if (text[offset++] == c)
                        { if (offset < text.Length && text[offset] == c) { offset++; continue; } closed = true; break; }
                    }
                    if (!closed) CandidatePrimitives.Fail("unsupported", "source-quoted-token");
                    tokens.Add(text.Substring(start, offset - start)); continue;
                }
                if (char.IsLetter(c) || c == '_')
                { int start = offset++; while (offset < text.Length && (char.IsLetterOrDigit(text[offset]) || text[offset] == '_')) offset++; tokens.Add(text.Substring(start, offset - start)); continue; }
                tokens.Add(c.ToString()); offset++;
            }
            var declarations = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string kind = tokens[i].ToUpperInvariant();
                if (!new[] { "FUNCTION", "FUNCTION_BLOCK", "DATA_BLOCK", "TYPE" }.Contains(kind)) continue;
                if (++i >= tokens.Count) CandidatePrimitives.Fail("unsupported", "source-declarations");
                string name = tokens[i];
                if (i + 1 < tokens.Count && char.IsDigit(tokens[i + 1][0])) CandidatePrimitives.Fail("unsupported", "numbered-source-declaration");
                if (name.StartsWith("\"", StringComparison.Ordinal))
                {
                    // Escaped identifiers need an actual native identity proof, rather than a guessed unescape.
                    name = name.Substring(1, name.Length - 2);
                    if (name.Contains("$") || name.Contains("\"")) CandidatePrimitives.Fail("unsupported", "source-identifier-escape");
                }
                else if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) CandidatePrimitives.Fail("unsupported", "source-declarations");
                if (name.Length == 0) CandidatePrimitives.Fail("unsupported", "source-declarations");
                declarations.Add((kind == "TYPE" ? "type:" : "block:") + name);
            }
            var names = declarations.ToArray();
            if (names.Length == 0 || names.Length > 256 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) CandidatePrimitives.Fail("unsupported", "source-declarations");
            return names;
        }
    }
}
