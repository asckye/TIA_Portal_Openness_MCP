using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using TiaGitAddIn.Services.SimaticMl;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>One offline SimaticML rendering implementation for all hosts and the workbench.</summary>
    public static class PlcProgramRenderer
    {
        public const int MaxFiles = 256;
        public const int MaxNetworks = 512;
        public const int MaxElements = 256;
        private const long MaxBytes = 64 * 1024 * 1024;

        private sealed class Rejected : Exception
        {
            internal ErrorDetails Details { get; }
            internal Rejected(string message, ErrorDetails details) : base(message) => Details = details;
        }

        private sealed class Page
        {
            internal BlockDefinition Block = null!;
            internal string Source = "", Version = "", Id = "";
            internal List<Network> Networks = new List<Network>();
            internal IEnumerable<string> Calls => Networks.SelectMany(n => n.Calls).Distinct(StringComparer.Ordinal);
        }

        private sealed class Network
        {
            internal CompileUnitDefinition Unit = null!;
            internal XElement Xml = null!;
            internal string Language = "", Kind = "", Code = "";
            internal string[] Calls = Array.Empty<string>();
        }

        public static Envelope Write(string inputPath, string outputPath, bool atlas, string? releaseKey,
            string? requestId = null, CancellationToken cancellationToken = default)
        {
            string tool = atlas ? "RenderPlcProgramAtlas" : "RenderPlcBlock";
            string id = Meta.Correlate(requestId);
            bool reading = false, created = false;
            Envelope Result(object? data, Error? error, Outcome outcome, Execution execution, Completeness completeness)
                => Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, releaseKey, tool, id, outcome, execution,
                    false, BehaviorPolicy.NotApplicable, completeness, null, Array.Empty<Warning>()));
            try
            {
                Absolute(inputPath, "inputPath"); Absolute(outputPath, "outputPath");
                if (!outputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) Invalid("outputPath", "Output must end in .html.");
                if (File.Exists(outputPath) || Directory.Exists(outputPath))
                    throw new Rejected("Existing output is never overwritten.", new AlreadyExistsDetails(outputPath));
                if (!Directory.Exists(Path.GetDirectoryName(outputPath)))
                    throw new Rejected("Output parent directory does not exist.", new NotFoundDetails(Path.GetDirectoryName(outputPath)));
                if (!File.Exists(inputPath) && !(atlas && Directory.Exists(inputPath)))
                    throw new Rejected("Input export does not exist.", new NotFoundDetails(inputPath));
                cancellationToken.ThrowIfCancellationRequested();
                reading = true;
                var pages = Load(inputPath, atlas, cancellationToken);
                string html = Html(pages, atlas);
                byte[] bytes = new UTF8Encoding(false).GetBytes(html);
                Limit("outputPath", bytes.LongLength, MaxBytes);
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                return Result(new { outputPath = Path.GetFullPath(outputPath), mediaType = "text/html", byteLength = bytes.Length,
                    sha256 = hash, blockCount = pages.Count, networkCount = pages.Sum(p => p.Networks.Count),
                    calledByScope = "Distinct caller blocks inside this atlas only; external callers are not counted.",
                    sources = pages.Select(p => new { path = p.Source, engineeringVersion = p.Version }).ToArray() },
                    null, Outcome.Succeeded, Execution.Completed, Completeness.Complete);
            }
            catch (Rejected ex)
            { return Result(null, new Error(ex.Message, ex.Details), Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None); }
            catch (OperationCanceledException) /* swallow(privacy): expose the stable V4 cancellation code without exception text */
            { return Result(null, new Error("Rendering cancelled before file creation.", new CancelledDetails("render")), Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Xml.XmlException)
            {
                if (created) return Result(new { outputPath, outputComplete = false },
                    new Error("Output creation started but did not complete. Inspect the retained file.", new PartialFailureDetails(0, 0, 0)),
                    Outcome.Partial, Execution.Partial, Completeness.Partial);
                if (ex is IOException && (File.Exists(outputPath) || Directory.Exists(outputPath)))
                    return Result(null, new Error("Existing output is never overwritten.", new AlreadyExistsDetails(outputPath)),
                        Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None);
                Error error = ex is UnauthorizedAccessException ? new Error("File access denied.", new AccessDeniedDetails("render", null))
                    : ex is ArgumentException || ex is System.Xml.XmlException || ex is InvalidDataException
                    ? new Error("Invalid or unsupported SimaticML input.", new InvalidArgumentDetails("inputPath", Array.Empty<string>()))
                    : new Error("Local render file operation failed.", new IoFailedDetails("render", null));
                return Result(null, error, reading ? Outcome.ReadFailed : Outcome.RejectedBeforeOperation,
                    reading ? Execution.ReadOnly : Execution.NotStarted, Completeness.None);
            }
        }

        private static void Absolute(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                || (Path.DirectorySeparatorChar == '\\' && (Path.GetPathRoot(path)?.Length ?? 0) < 3))
                Invalid(parameter, "An absolute local path is required.");
            // UNC exports would contact a service, rather than perform a local offline render.
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                Invalid(parameter, "Use a local filesystem path.");
        }

        private static void Invalid(string parameter, string message)
            => throw new Rejected(message, new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        private static void Limit(string parameter, long actual, long maximum)
        {
            if (actual > maximum) throw new Rejected("Render budget exceeded.", new LimitExceededDetails(parameter, maximum, actual));
        }

        private static IEnumerable<string> Files(string directory, int depth = 0)
        {
            Limit("inputPath.depth", depth, 32);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) Invalid("inputPath", "Export directories must not contain links.");
            foreach (string file in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
                if (string.Equals(Path.GetExtension(file), ".xml", StringComparison.OrdinalIgnoreCase)) yield return file;
            foreach (string child in Directory.EnumerateDirectories(directory).OrderBy(p => p, StringComparer.Ordinal))
                foreach (string file in Files(child, depth + 1)) yield return file;
        }

        private static List<Page> Load(string input, bool atlas, CancellationToken token)
        {
            var files = Directory.Exists(input) ? Files(input).Take(MaxFiles + 1).ToArray() : new[] { input };
            Limit("inputPath.files", files.Length, MaxFiles);
            var pages = new List<Page>();
            long bytes = 0;
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) Invalid("inputPath", "Export files must not be links.");
                bytes += new FileInfo(file).Length; Limit("inputPath.bytes", bytes, MaxBytes);
                var parsed = SimaticMlParser.Parse(file);
                if (parsed.Blocks.Count == 0) Invalid("inputPath", "Every XML input must contain a PLC block; select a block export directory.");
                foreach (var block in parsed.Blocks)
                {
                    var page = new Page { Block = block, Source = Path.GetFullPath(file), Version = parsed.EngineeringVersion ?? "unspecified" };
                    foreach (var unit in block.CompileUnits)
                    {
                        var xml = XElement.Parse(unit.Network?.RawXml ?? "<NetworkSource />");
                        string language = unit.ProgrammingLanguage ?? block.ProgrammingLanguage ?? "unknown";
                        string code = LadTextRenderer.Code(new XElement("NetworkSource", xml));
                        bool graph = unit.Network?.Format == "FlgNet" && (language == "LAD" || language == "FBD");
                        int elements = (unit.Network?.Parts.Count ?? 0) + (unit.Network?.Calls.Count ?? 0);
                        Limit("network.elements", elements, MaxElements);
                        bool empty = graph ? elements == 0 : string.IsNullOrWhiteSpace(code) && !xml.Elements().Any();
                        if ((language == "SCL" || language == "STL") && string.IsNullOrWhiteSpace(code)) empty = true;
                        if (!empty && !graph && language != "SCL" && language != "STL")
                            Invalid("inputPath", "A nonempty network uses an unsupported language or representation.");
                        var network = new Network { Unit = unit, Xml = xml, Language = language, Code = code,
                            Kind = empty ? "empty" : graph ? "ladder" : "code",
                            Calls = xml.DescendantsAndSelf().Where(e => e.Name.LocalName == "CallInfo")
                                .Select(e => (string?)e.Attribute("Name") ?? "").Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray() };
                        page.Networks.Add(network);
                    }
                    pages.Add(page); Limit("inputPath.blocks", pages.Count, MaxFiles);
                    Limit("inputPath.networks", pages.Sum(p => p.Networks.Count), MaxNetworks);
                }
            }
            if (pages.Count == 0 || (!atlas && pages.Count != 1)) Invalid("inputPath", "Select exactly one block for a block page, or at least one for an atlas.");
            pages = pages.OrderBy(p => p.Block.Number ?? int.MaxValue).ThenBy(p => p.Block.Name, StringComparer.Ordinal).ThenBy(p => p.Source, StringComparer.Ordinal).ToList();
            for (int i = 0; i < pages.Count; i++) pages[i].Id = "block-" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return pages;
        }

        private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        private static string Texts(IEnumerable<MultilingualTextDefinition> texts, string kind)
            => string.Join("\n", texts.Where(t => t.CompositionName == kind).SelectMany(t => t.Items).Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text));
        private static string Link(string name, List<Page> pages)
        {
            var matches = pages.Where(p => p.Block.Name == name).ToArray();
            return matches.Length == 1 ? "<a href=\"#" + matches[0].Id + "\">" + E(name) + "</a>"
                : E(name) + (matches.Length == 0 ? " (outside atlas)" : " (ambiguous in atlas)");
        }

        private static string Html(List<Page> pages, bool atlas)
        {
            var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>PLC program atlas</title><style>");
            b.Append("body{font:15px system-ui,sans-serif;margin:0;color:#172b40;background:#eef2f6}main{max-width:1440px;margin:auto;padding:28px}h1{font-size:30px}h2{margin-top:28px}.page{background:white;border:1px solid #cbd5e1;border-radius:10px;margin:24px 0;padding:24px;break-before:page}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #cbd5e1;text-align:left;padding:9px;vertical-align:top;overflow-wrap:anywhere}a{color:#075c96}pre,.comment{white-space:pre-wrap;overflow-wrap:anywhere}pre{background:#f1f5f9;padding:16px}.diagram{overflow:auto;border:1px solid #cbd5e1}svg text{font:13px monospace}.empty{padding:16px;border:1px dashed #94a3b8;color:#475569}.scope{color:#475569}dl{display:grid;grid-template-columns:120px 1fr;gap:8px}dd{margin:0;overflow-wrap:anywhere}@media print{body{background:white}main{padding:0}.page{border:0;border-radius:0}.diagram{overflow:visible}svg{width:100%;height:auto}}");
            b.Append(".diagram{overflow:hidden;background:white;padding:8px}.diagram svg{display:block;width:100%;height:auto}svg path{fill:none;stroke:#243746;stroke-width:1.5}svg .symbol,svg .power-rail{stroke-width:2}svg .box{fill:#fff;stroke:#243746;stroke-width:1.5}svg .junction{fill:#243746}svg .open-pin{fill:white;stroke:#243746}svg .finding{fill:#c48a12}svg text{fill:#172b40}svg .operand,svg .instance{font-size:14px}svg .instruction-name,svg .symbol-mark{font-weight:bold;font-size:14px}svg .symbol-comment,svg .instruction-type{fill:#64748b;font-size:12px}@media print{.diagram{overflow:visible}}");
            b.Append("</style></head><body><main><header id=\"contents\"><h1>").Append(atlas ? "PLC program atlas" : "PLC block page")
                .Append("</h1><p class=\"scope\">Offline SimaticML exports. Static logic only; no online power-flow display. Simplified layout; source exports remain the engineering authority.</p><p>Called by: distinct caller blocks inside this atlas only; external callers are not counted.</p></header>");
            b.Append("<table aria-label=\"Contents\"><thead><tr><th>Number / type</th><th>Name</th><th>Language</th><th>Networks: ladder / code / empty</th><th>Calls</th><th>Called by (inside atlas)</th><th>Source file</th></tr></thead><tbody>");
            foreach (var page in pages)
            {
                var callers = pages.Where(p => p.Calls.Contains(page.Block.Name ?? "") && pages.Count(q => q.Block.Name == page.Block.Name) == 1).ToArray();
                b.Append("<tr><td>").Append(page.Block.Number).Append(' ').Append(E(page.Block.BlockKind)).Append("</td><td><a href=\"#").Append(page.Id).Append("\">")
                    .Append(E(page.Block.Name)).Append("</a></td><td>").Append(E(page.Block.ProgrammingLanguage)).Append("</td><td>")
                    .Append(string.Join(" / ", new[] { "ladder", "code", "empty" }.Select(k => page.Networks.Count(n => n.Kind == k))))
                    .Append("</td><td>").Append(string.Join("<br>", page.Calls.Select(n => Link(n, pages))))
                    .Append("</td><td>").Append(callers.Length).Append(callers.Length == 0 ? "" : ": " + string.Join(", ", callers.Select(p => "<a href=\"#" + p.Id + "\">" + E(p.Block.Name) + "</a>")))
                    .Append("</td><td>").Append(E(page.Source)).Append("</td></tr>");
            }
            b.Append("</tbody></table>");
            foreach (var page in pages)
            {
                var block = page.Block;
                var unknownParts = new SortedSet<string>(StringComparer.Ordinal);
                b.Append("<article class=\"page\" id=\"").Append(page.Id).Append("\"><a href=\"#contents\">Contents</a><h1>").Append(E(block.Name)).Append("</h1><dl>");
                foreach (var field in new[] { ("Number", block.Number?.ToString(CultureInfo.InvariantCulture)), ("Type", block.BlockKind), ("Language", block.ProgrammingLanguage),
                    ("Title", Texts(block.Texts, "Title")), ("Comment", Texts(block.Texts, "Comment")), ("Source file", page.Source), ("Export version", page.Version) })
                    b.Append("<dt>").Append(field.Item1).Append("</dt><dd class=\"comment\">").Append(E(field.Item2)).Append("</dd>");
                b.Append("</dl><h2>Interface</h2><table><thead><tr><th>Section</th><th>Name</th><th>Type</th><th>Start / default</th><th>Comment</th></tr></thead><tbody>");
                foreach (var section in block.InterfaceSections) Members(b, section.Name, "", section.Members);
                b.Append("</tbody></table>");
                if (page.Networks.Count == 0) b.Append("<p class=\"empty\">No networks (declaration-only or empty block).</p>");
                for (int i = 0; i < page.Networks.Count; i++)
                {
                    var n = page.Networks[i];
                    var title = Texts(n.Unit.Texts, "Title");
                    b.Append("<section><h2>Network ").Append(i + 1).Append(string.IsNullOrWhiteSpace(title) ? "" : " — " + E(title)).Append("</h2><p>").Append(E(n.Language));
                    if (n.Language == "FBD") b.Append(" — FBD shown as ladder equivalent");
                    b.Append("</p><p class=\"comment\">").Append(E(Texts(n.Unit.Texts, "Comment"))).Append("</p>");
                    if (n.Kind == "empty") b.Append("<p class=\"empty\">Empty network.</p>");
                    else if (n.Kind == "code") b.Append("<pre><code>").Append(E(n.Code)).Append("</code></pre>");
                    else b.Append("<div class=\"diagram\">").Append(PlcLadderDrawing.NetworkSvg(n.Unit.Network!, name =>
                    {
                        var match = pages.Where(p => p.Block.Name == name).ToArray();
                        return match.Length == 1 ? "#" + match[0].Id : null;
                    }, unknownParts)).Append("</div>");
                    if (n.Calls.Length > 0) b.Append("<p>Calls in this network: ").Append(string.Join(", ", n.Calls.Select(name => Link(name, pages)))).Append("</p>");
                    b.Append("</section>");
                }
                if (unknownParts.Count > 0) b.Append("<aside class=\"render-notes\"><h2>Rendering notes</h2><p>Unknown parts shown as labelled boxes: ")
                    .Append(string.Join(", ", unknownParts.Select(E))).Append(".</p></aside>");
                b.Append("</article>");
            }
            return b.Append("</main></body></html>").ToString();
        }

        private static void Members(StringBuilder b, string section, string parent, IEnumerable<InterfaceMember> members)
        {
            foreach (var member in members)
            {
                string name = parent + member.Name;
                b.Append("<tr><td>").Append(E(section)).Append("</td><td>").Append(E(name)).Append("</td><td>").Append(E(member.Datatype))
                    .Append("</td><td>").Append(E(member.StartValue ?? member.DefaultValue)).Append("</td><td>")
                    .Append(E(string.Join("\n", member.Comments.Values))).Append("</td></tr>");
                Members(b, section, name + ".", member.Children);
            }
        }
    }
}
