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
        private const int LadderWidth = 640, ContactHeight = 24, ContactGap = 16, TimerWidth = 150, TimerHeight = 100;

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

        private const string Styles = @"
:root{color-scheme:light dark;--bg:#F6F8FA;--card:#FFFFFF;--cardSoft:#F6F8FA;--cardSel:#F0F8FA;--cardBorder:#D0D7DE;--divider:#D8DEE4;--input:#FFFFFF;--inputBorder:#D0D7DE;--pill:#F6F8FA;--pillHover:#EAEEF2;--menuBg:#FFFFFF;--text:#1F2328;--textMuted:#656D76;--textFaint:#8C959F;--checkBorder:#8C959F;--accent:#0B7A99;--onAccent:#FFFFFF;--primaryBg:#1F883D;--warn:#9A6700;--warnBg:#FFF8C5;--ok:#1A7F37;--okBg:#DAFBE1;--noteBg:#DDF4FF;--noteBorder:rgba(84,174,255,.4);--noteAccent:#0969DA;--codeBg:#F6F8FA;--codeText:var(--text);--diffGreen:#1A7F37;--diffRed:#CF222E;--font:system-ui,'Segoe UI','Noto Sans SC',sans-serif;--mono:'JetBrains Mono',ui-monospace,Consolas,monospace}
@media(prefers-color-scheme:dark){:root{--bg:#0D1117;--card:#161B22;--cardSoft:#0D1117;--cardSel:#1A2730;--cardBorder:#30363D;--divider:#21262D;--input:#0D1117;--inputBorder:#30363D;--pill:#21262D;--pillHover:#30363D;--menuBg:#161B22;--text:#E6EDF3;--textMuted:#8D96A0;--textFaint:#6E7681;--checkBorder:#6E7681;--accent:#4FC3E0;--onAccent:#0D1117;--primaryBg:#238636;--warn:#D29922;--warnBg:rgba(210,153,34,.15);--ok:#3FB950;--okBg:rgba(63,185,80,.15);--noteBg:rgba(56,139,253,.15);--noteBorder:rgba(56,139,253,.4);--noteAccent:#58A6FF;--codeBg:#0D1117;--codeText:var(--text);--diffGreen:#3FB950;--diffRed:#F85149;--font:system-ui,'Segoe UI','Noto Sans SC',sans-serif;--mono:'JetBrains Mono',ui-monospace,Consolas,monospace}}
*{box-sizing:border-box}body{font:12.5px/1.55 var(--font);margin:0;color:var(--text);background:var(--bg)}main{max-width:1440px;margin:auto;padding:20px 24px}h1,h2,p{margin:0}h1{font-size:24px;font-weight:600;letter-spacing:-.02em}h2{font-size:13px;font-weight:600}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}a:focus-visible,summary:focus-visible{outline:2px solid var(--accent);outline-offset:3px}.mono{font-family:var(--mono)}.scope,.comment,.meta,.footer{color:var(--textMuted)}.comment{white-space:pre-wrap;overflow-wrap:anywhere}.comment:empty{display:none}
.doc{background:var(--card);border:1px solid var(--cardBorder);border-radius:6px;padding:26px 36px;margin-bottom:20px}.doc>*+*{margin-top:18px}.page{break-before:page}.page-nav{font-size:11px;font-weight:600}.block-header{display:flex;flex-direction:column;gap:10px;border-bottom:1px solid var(--divider);padding-bottom:16px}.block-heading{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.chip{font-size:11px;padding:3px 8px;border-radius:6px;background:var(--pill);color:var(--textMuted)}.language{background:var(--cardSel);color:var(--accent);font-weight:600}.block-title{font-size:13px;color:var(--textMuted)}.meta{display:flex;gap:8px 24px;flex-wrap:wrap;font-size:11.5px}.meta span{overflow-wrap:anywhere}.meta .value{color:var(--text)}.scope{font-size:11px;line-height:1.6}
.catalog-heading{display:flex;align-items:flex-end;justify-content:space-between;gap:20px;flex-wrap:wrap}.eyebrow{font:11px var(--mono);letter-spacing:.14em;color:var(--accent);margin-bottom:4px}.stats{display:flex;gap:22px;font-size:12px;color:var(--textMuted)}.stat{display:flex;flex-direction:column;gap:2px;white-space:nowrap}.stat strong{font-size:20px;font-weight:600;letter-spacing:-.02em;color:var(--text)}.table-scroll{overflow:auto}table{border-collapse:collapse;width:100%;text-align:left}th{font-size:10px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:var(--textMuted);height:28px}td,th{border-bottom:1px solid var(--divider);padding:6px 10px;vertical-align:top;overflow-wrap:anywhere}td{font-size:11.5px;min-height:34px}thead{border-top:1px solid var(--divider)}.catalog{min-width:900px}.catalog tr{display:grid;grid-template-columns:52px 1.5fr 60px 56px 1.1fr 1fr 1fr 1.3fr;column-gap:12px}.catalog td,.catalog th{border:0;padding:8px 0;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.catalog tr{border-bottom:1px solid var(--divider);padding:0 10px;min-height:38px}.catalog thead tr{min-height:32px}.catalog a{font-weight:600}.catalog .source{font-size:11px}.uncalled{background:var(--warnBg)}.uncalled .called-by{color:var(--warn);font-weight:600}
.interface{border:1px solid var(--cardBorder);border-radius:6px;overflow:hidden}.interface summary{cursor:pointer;padding:10px 14px;background:var(--cardSoft);font-size:12.5px;font-weight:600}.interface table{min-width:640px}.interface tr{display:grid;grid-template-columns:80px 1.2fr 90px 1fr 1.6fr;column-gap:12px;padding:0 14px;border-top:1px solid var(--divider)}.interface thead{border:0}.interface td,.interface th{border:0;padding:6px 0}.interface td:first-child{font-size:10.5px;font-weight:600;color:var(--accent)}.interface td:nth-child(2),.interface td:nth-child(3),.interface td:nth-child(4){font-family:var(--mono)}.interface td:nth-child(n+3){color:var(--textMuted)}
.network{border:1px solid var(--cardBorder);border-radius:6px;padding:14px 16px}.network>*+*{margin-top:8px}.network-heading{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.network-id{font:11px var(--mono);color:var(--textMuted)}.network .chip{font-size:10px;padding:2px 7px}.network .comment{font-size:11.5px}pre{background:var(--codeBg);color:var(--codeText);padding:12px 14px;font:11px/1.65 var(--mono);border:1px solid var(--cardBorder);border-radius:6px;white-space:pre;overflow:auto}code{font:inherit}.empty{padding:14px;border:1px dashed var(--divider);border-radius:6px;color:var(--textMuted);text-align:center;font-size:11.5px}.diagram{overflow:auto}.diagram svg{display:block;width:640px;max-width:100%;height:auto;overflow:visible}svg text{font:11px var(--mono);fill:var(--text)}svg path{fill:none;stroke:var(--text);stroke-width:1.5}svg .symbol,svg .power-rail{stroke-width:2}svg .box{fill:var(--card);stroke:var(--text);stroke-width:2}svg .box-divider{stroke:var(--divider)}svg .junction{fill:var(--text)}svg .instruction-name,svg .symbol-mark{font-weight:600}svg .instance{fill:var(--accent)}svg .symbol-comment,svg .instruction-type{fill:var(--textMuted);font-size:10px}svg .finding{fill:var(--warnBg);stroke:var(--warn);stroke-width:1.5}svg .has-finding .symbol{stroke:var(--warn)}svg .has-finding .operand{fill:var(--warn);font-weight:600}.inspection-note{display:flex;gap:8px;align-items:flex-start;padding:8px 10px;border-radius:6px;background:var(--warnBg);color:var(--warn);font-size:11.5px}.inspection-note strong{font-weight:600}.render-notes{border:1px solid var(--noteBorder);border-radius:6px;padding:12px;background:var(--noteBg)}.footer{font-size:11px;line-height:1.6;border-top:1px solid var(--divider);padding-top:12px}
@media(max-width:700px){main{padding:12px}.doc{padding:26px 18px}.stats{gap:14px}.meta{gap:8px 16px}}
@media print{body{color-scheme:light;background:var(--card)}main{padding:0}.doc{border:0;border-radius:0}.page-nav{display:none}.table-scroll,.diagram{overflow:visible}.catalog,.interface table{min-width:0}.network{break-inside:avoid}}
";

        private static Page[] Callers(Page page, List<Page> pages)
            => pages.Where(p => p.Calls.Contains(page.Block.Name ?? "") && pages.Count(q => q.Block.Name == page.Block.Name) == 1).ToArray();
        private static string CallerLinks(Page[] callers)
            => callers.Length + (callers.Length == 0 ? "" : ": " + string.Join(", ", callers.Select(p => "<a href=\"#" + p.Id + "\">" + E(p.Block.Name) + "</a>")));
        private const string Footer = "Drawn offline from exported SimaticML files — not a TIA Portal screenshot.";

        private static string Html(List<Page> pages, bool atlas)
        {
            var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>PLC program atlas</title><style>");
            b.Append(Styles).Append("</style></head><body><main><header class=\"doc\" id=\"contents\"><div class=\"catalog-heading\"><div><p class=\"eyebrow\">")
                .Append(atlas ? "PROGRAM ATLAS" : "PLC BLOCK PAGE").Append("</p><h1>").Append(atlas ? "PLC program atlas" : "PLC block page").Append("</h1></div><div class=\"stats\">");
            Stat(b, pages.Count, "blocks");
            foreach (string kind in new[] { "ladder", "code", "empty" }) Stat(b, pages.Sum(p => p.Networks.Count(n => n.Kind == kind)), kind == "empty" ? "empty" : kind + " nets");
            b.Append("</div></div><p class=\"scope\">Offline SimaticML exports. Static logic only; no online power-flow display. Simplified layout; source exports remain the engineering authority.</p>");
            b.Append("<div class=\"table-scroll\"><table class=\"catalog\" aria-label=\"Contents\"><thead><tr><th>No.</th><th>Name</th><th>Lang.</th><th>Nets</th><th>L / C / E</th><th>Calls</th><th>Called by</th><th>Source file</th></tr></thead><tbody>");
            foreach (var page in pages)
            {
                var callers = Callers(page, pages);
                b.Append("<tr").Append(callers.Length == 0 ? " class=\"uncalled\"" : "").Append("><td class=\"mono\">").Append(E(page.Block.BlockKind)).Append(' ').Append(page.Block.Number)
                    .Append("</td><td><a href=\"#").Append(page.Id).Append("\">").Append(E(page.Block.Name)).Append("</a></td><td>").Append(E(page.Block.ProgrammingLanguage))
                    .Append("</td><td class=\"mono\">").Append(page.Networks.Count).Append("</td><td class=\"mono\">")
                    .Append(string.Join(" / ", new[] { "ladder", "code", "empty" }.Select(k => page.Networks.Count(n => n.Kind == k))))
                    .Append("</td><td>").Append(string.Join(", ", page.Calls.Select(n => Link(n, pages))))
                    .Append("</td><td class=\"called-by\">").Append(CallerLinks(callers)).Append(callers.Length == 0 ? " · Not called" : "")
                    .Append("</td><td class=\"mono source\" title=\"").Append(E(page.Source)).Append("\">").Append(E(Path.GetFileName(page.Source))).Append("</td></tr>");
            }
            b.Append("</tbody></table></div><p class=\"footer\">No.: block type and number. Lang.: programming language. Nets: network count. L / C / E: ladder / code / empty networks. Called by: distinct caller blocks inside this atlas only; external callers are not counted. Source file: full path in the tooltip. · ").Append(Footer).Append("</p></header>");
            foreach (var page in pages)
            {
                var block = page.Block;
                var unknownParts = new SortedSet<string>(StringComparer.Ordinal);
                b.Append("<article class=\"doc page\" id=\"").Append(page.Id).Append("\"><nav class=\"page-nav\"><a href=\"#contents\">← Back to catalog</a></nav>")
                    .Append("<header class=\"block-header\"><div class=\"block-heading\"><span class=\"chip mono\">").Append(E(block.BlockKind)).Append(' ').Append(block.Number)
                    .Append("</span><h1>").Append(E(block.Name)).Append("</h1><span class=\"chip language\">").Append(E(block.ProgrammingLanguage))
                    .Append("</span><span class=\"block-title comment\">").Append(E(Texts(block.Texts, "Title"))).Append("</span></div><p class=\"comment\">")
                    .Append(E(Texts(block.Texts, "Comment"))).Append("</p><div class=\"meta\"><span>Source file · <span class=\"mono value\">").Append(E(page.Source))
                    .Append("</span></span><span>Export version · <span class=\"mono value\">").Append(E(page.Version))
                    .Append("</span></span><span>Called by · <span class=\"value\">").Append(CallerLinks(Callers(page, pages)))
                    .Append("</span></span><span>Calls · <span class=\"value\">").Append(string.Join(", ", page.Calls.Select(n => Link(n, pages)))).Append("</span></span></div></header>");
                b.Append("<details class=\"interface\" open><summary>Block interface</summary><div class=\"table-scroll\"><table aria-label=\"Block interface\"><thead><tr><th>Section</th><th>Name</th><th>Type</th><th>Start / default</th><th>Comment</th></tr></thead><tbody>");
                foreach (var section in block.InterfaceSections) Members(b, section.Name, "", section.Members);
                b.Append("</tbody></table></div></details>");
                if (page.Networks.Count == 0) b.Append("<p class=\"empty\">No networks (declaration-only or empty block).</p>");
                for (int i = 0; i < page.Networks.Count; i++)
                {
                    var n = page.Networks[i];
                    var title = Texts(n.Unit.Texts, "Title");
                    b.Append("<section class=\"network\"><header class=\"network-heading\"><span class=\"network-id\">N").Append(i + 1).Append("</span><h2>")
                        .Append(string.IsNullOrWhiteSpace(title) ? "Network " + (i + 1) : E(title)).Append("</h2><span class=\"chip\">").Append(E(n.Language)).Append("</span>");
                    if (n.Language == "FBD") b.Append("<span class=\"scope\">FBD shown as ladder equivalent</span>");
                    b.Append("</header><p class=\"comment\">").Append(E(Texts(n.Unit.Texts, "Comment"))).Append("</p>");
                    if (n.Kind == "empty") b.Append("<p class=\"empty\">Empty network.</p>");
                    else if (n.Kind == "code") b.Append("<pre><code>").Append(E(n.Code)).Append("</code></pre>");
                    else
                    {
                        string drawing = PlcLadderDrawing.NetworkSvg(n.Unit.Network!, name =>
                        {
                            var match = pages.Where(p => p.Block.Name == name).ToArray();
                            return match.Length == 1 ? "#" + match[0].Id : null;
                        }, unknownParts);
                        b.Append("<div class=\"diagram\">").Append(PrimerSvg(drawing, out var findings)).Append("</div>");
                        foreach (string finding in findings) b.Append("<aside class=\"inspection-note\"><strong aria-label=\"Inspection\">!</strong><span>").Append(E(finding)).Append("</span></aside>");
                    }
                    if (n.Calls.Length > 0) b.Append("<p class=\"scope\">Calls in this network: ").Append(string.Join(", ", n.Calls.Select(name => Link(name, pages)))).Append("</p>");
                    b.Append("</section>");
                }
                if (unknownParts.Count > 0) b.Append("<aside class=\"render-notes\"><h2>Rendering notes</h2><p>Unknown parts shown as labelled boxes: ")
                    .Append(string.Join(", ", unknownParts.Select(E))).Append(".</p></aside>");
                b.Append("<footer class=\"footer\">").Append(Footer).Append("</footer></article>");
            }
            return b.Append("</main></body></html>").ToString();
        }

        private static void Stat(StringBuilder b, int count, string label)
            => b.Append("<div class=\"stat\"><strong>").Append(count).Append("</strong><span>").Append(label).Append("</span></div>");

        // Presentation only: retain the shared routing, node identities, labels and inspection findings.
        private static string PrimerSvg(string drawing, out string[] findings)
        {
            var svg = XElement.Parse(drawing);
            XNamespace ns = svg.Name.Namespace;
            int width = (int)svg.Attribute("width")!, height = (int)svg.Attribute("height")!;
            svg.SetAttributeValue("width", LadderWidth);
            var notes = new List<string>();
            var endpoints = new Dictionary<(int x, int y), (int x, int y)>();
            foreach (var node in svg.Descendants().Where(e => e.Attribute("data-uid") != null))
            {
                string kind = (string)node.Attribute("class")!;
                var symbol = node.Elements().FirstOrDefault(e => (string?)e.Attribute("class") == "symbol");
                if (symbol != null && (kind.StartsWith("contact-", StringComparison.Ordinal) || kind.StartsWith("coil", StringComparison.Ordinal)))
                {
                    var start = System.Text.RegularExpressions.Regex.Match((string)symbol.Attribute("d")!, @"^M (-?\d+) (-?\d+)");
                    int x = int.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture) + 30, y = int.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture);
                    int half = ContactGap / 2, tall = ContactHeight / 2;
                    symbol.SetAttributeValue("d", kind.StartsWith("contact-", StringComparison.Ordinal)
                        ? $"M {x - 30} {y} H {x - half} M {x + half} {y} H {x + 30} M {x - half} {y - tall} V {y + tall} M {x + half} {y - tall} V {y + tall}"
                        : $"M {x - 30} {y} H {x - 18} M {x + 18} {y} H {x + 30} M {x - 18} {y} Q {x - 12} {y - 12} {x} {y - 12} Q {x + 12} {y - 12} {x + 18} {y} Q {x + 12} {y + 12} {x} {y + 12} Q {x - 12} {y + 12} {x - 18} {y}");
                    var finding = node.Elements().FirstOrDefault(e => (string?)e.Attribute("class") == "finding");
                    if (finding != null)
                    {
                        notes.Add(finding.Value);
                        node.SetAttributeValue("class", kind + " has-finding");
                        finding.ReplaceWith(new XElement(ns + "rect", new XAttribute("class", "finding"), new XAttribute("x", x - 18), new XAttribute("y", y - 20),
                            new XAttribute("width", 36), new XAttribute("height", 40), new XAttribute("rx", 4), new XElement(ns + "title", finding.Value)));
                        var highlight = node.Elements().Single(e => (string?)e.Attribute("class") == "finding");
                        highlight.Remove(); symbol.AddBeforeSelf(highlight);
                    }
                }
                if (kind == "instruction" && node.Elements().Any(e => (string?)e.Attribute("class") == "instruction-name" && e.Value == "TON"))
                {
                    var box = node.Elements().Single(e => (string?)e.Attribute("class") == "box");
                    int left = (int)box.Attribute("x")!, right = left + (int)box.Attribute("width")!, center = (left + right) / 2;
                    int newLeft = center - TimerWidth / 2, newRight = newLeft + TimerWidth;
                    var pins = node.Elements().Where(e => (string?)e.Attribute("class") == "pin").ToArray();
                    int y = pins.Select(p => int.Parse(((string)p.Attribute("d")!).Split(' ')[2], CultureInfo.InvariantCulture)).Min();
                    int top = y - 28;
                    box.SetAttributeValue("x", newLeft); box.SetAttributeValue("y", top); box.SetAttributeValue("width", TimerWidth); box.SetAttributeValue("height", TimerHeight); box.SetAttributeValue("rx", 3);
                    node.Elements().Where(e => (string?)e.Attribute("class") == "box-divider").Remove();
                    foreach (var pin in pins)
                    {
                        var point = System.Text.RegularExpressions.Regex.Match((string)pin.Attribute("d")!, @"^M (-?\d+) (-?\d+) H (-?\d+)$");
                        int oldX = int.Parse(point.Groups[1].Value, CultureInfo.InvariantCulture), pinY = int.Parse(point.Groups[2].Value, CultureInfo.InvariantCulture);
                        int edge = oldX == left ? newLeft : newRight;
                        endpoints[(oldX, pinY)] = (edge, pinY);
                        pin.SetAttributeValue("d", $"M {edge} {pinY} H {edge + (oldX == left ? -16 : 16)}");
                    }
                    foreach (var label in node.Elements().Where(e => e.Name.LocalName == "text"))
                    {
                        string labelKind = (string)label.Attribute("class")!;
                        if (labelKind == "instance" || labelKind == "instruction-name" || labelKind == "instruction-type")
                            label.SetAttributeValue("y", top + (labelKind == "instance" ? -8 - Math.Max(0, label.Elements().Count() - 1) * 16 : labelKind == "instruction-name" ? 20 : 34));
                        else if (labelKind == "pin-name" || labelKind == "pin-value")
                        {
                            int x = (int)label.Attribute("x")!;
                            int shift = x < center ? newLeft - left : newRight - right;
                            label.SetAttributeValue("x", x + shift);
                            foreach (var line in label.Elements()) line.SetAttributeValue("x", (int)line.Attribute("x")! + shift);
                        }
                    }
                    height = Math.Max(height, top + TimerHeight + 30);
                }
            }
            foreach (var wire in svg.Elements().Where(e => (string?)e.Attribute("class") == "wire"))
                wire.SetAttributeValue("d", MoveEndpoints((string)wire.Attribute("d")!, endpoints));
            AlignRungOutputs(svg, width);
            svg.SetAttributeValue("viewBox", $"0 0 {LadderWidth} {height}");
            svg.SetAttributeValue("height", height);
            svg.Elements().Single(e => (string?)e.Attribute("class") == "power-rail").SetAttributeValue("d", $"M 24 16 V {height - 16}");
            svg.Add(new XElement(ns + "path", new XAttribute("class", "power-rail"), new XAttribute("d", $"M {LadderWidth} 16 V {height - 16}")));
            findings = notes.ToArray();
            return svg.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
        }

        private static void AlignRungOutputs(XElement svg, int sourceWidth)
        {
            var wires = svg.Elements().Where(e => (string?)e.Attribute("class") == "wire").Select(e =>
            {
                var match = System.Text.RegularExpressions.Regex.Match((string)e.Attribute("d")!, @"^M (-?\d+) (-?\d+) H (-?\d+)$");
                return (element: e, x: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    y: int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), bus: int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
            }).ToArray();
            var endpoints = new Dictionary<(int x, int y), (int x, int y)>();
            var outputs = new List<(int x, int y)>();
            int Fit(int x) => 24 + (int)Math.Round((double)(x - 24) * (LadderWidth - 48) / (sourceWidth - 48));
            foreach (var node in svg.Descendants().Where(e => e.Attribute("data-uid") != null))
            {
                string kind = (string)node.Attribute("class")!;
                var box = node.Elements().FirstOrDefault(e => (string?)e.Attribute("class") == "box");
                var symbol = node.Elements().FirstOrDefault(e => (string?)e.Attribute("class") == "symbol");
                int left, right, y;
                int? outputY = null;
                bool terminal = false;
                if (box != null)
                {
                    left = (int)box.Attribute("x")!; right = left + (int)box.Attribute("width")!;
                    var flow = node.Elements().FirstOrDefault(e => (string?)e.Attribute("class") == "pin-name" && (e.Value == "ENO" || e.Value == "Q" || e.Value == "QU" || e.Value == "QD") && (int)e.Attribute("x")! > (left + right) / 2);
                    y = flow == null ? (int)box.Attribute("y")! : (int)flow.Attribute("y")! - 4;
                    terminal = !wires.Any(w => w.x == right);
                    if (terminal && flow != null) outputY = y;
                }
                else if (symbol != null)
                {
                    var leads = System.Text.RegularExpressions.Regex.Matches((string)symbol.Attribute("d")!, @"M (-?\d+) (-?\d+) H (-?\d+)");
                    left = int.Parse(leads[0].Groups[1].Value, CultureInfo.InvariantCulture);
                    right = int.Parse(leads[1].Groups[3].Value, CultureInfo.InvariantCulture);
                    y = int.Parse(leads[0].Groups[2].Value, CultureInfo.InvariantCulture);
                    terminal = kind.StartsWith("coil", StringComparison.Ordinal) && !wires.Any(w => w.x == right && w.y == y);
                    if (terminal) outputY = y;
                }
                else continue;
                int shift = terminal ? LadderWidth - 24 - right : Fit((left + right) / 2) - (left + right) / 2;
                if (box != null)
                {
                    foreach (var pin in node.Elements().Where(e => (string?)e.Attribute("class") == "pin"))
                    {
                        var point = ((string)pin.Attribute("d")!).Split(' ');
                        int pinX = int.Parse(point[1], CultureInfo.InvariantCulture), pinY = int.Parse(point[2], CultureInfo.InvariantCulture);
                        endpoints[(pinX, pinY)] = (pinX + shift, pinY);
                    }
                }
                else
                {
                    endpoints[(left, y)] = (left + shift, y);
                    endpoints[(right, y)] = (right + shift, y);
                }
                foreach (var element in node.Descendants())
                {
                    foreach (string coordinate in new[] { "x", "cx" })
                        if (element.Attribute(coordinate) != null) element.SetAttributeValue(coordinate, (int)element.Attribute(coordinate)! + shift);
                    if (element.Attribute("d") != null) element.SetAttributeValue("d", ShiftPath((string)element.Attribute("d")!, shift));
                }
                if (outputY.HasValue) outputs.Add((right + shift, outputY.Value));
            }
            // Move each shared bus between its relocated sources and targets, including every branch/junction.
            foreach (var group in wires.GroupBy(w => w.bus))
            {
                int bus = group.Key;
                int[] sources = group.Where(w => w.x < bus).Select(w => endpoints.TryGetValue((w.x, w.y), out var p) ? p.x : Fit(w.x)).ToArray();
                int[] targets = group.Where(w => w.x > bus).Select(w => endpoints.TryGetValue((w.x, w.y), out var p) ? p.x : Fit(w.x)).ToArray();
                int newBus = bus == 24 ? 24 : sources.Length > 0 && targets.Length > 0 ? (sources.Max() + targets.Min()) / 2 : Fit(bus);
                var stub = group.First();
                if (svg.Elements().Any(e => (string?)e.Attribute("class") == "open-pin" && (int)e.Attribute("cx")! == bus && (int)e.Attribute("cy")! == stub.y))
                    newBus = endpoints[(stub.x, stub.y)].x + Math.Sign(bus - stub.x) * 18;
                foreach (int rung in group.Select(w => w.y).Distinct()) endpoints[(bus, rung)] = (newBus, rung);
                foreach (var junction in svg.Elements().Where(e => (string?)e.Attribute("class") == "junction" && (int)e.Attribute("cx")! == bus))
                    junction.SetAttributeValue("cx", newBus);
            }
            foreach (var path in svg.Elements().Where(e => (string?)e.Attribute("class") == "wire" || (string?)e.Attribute("class") == "branch"))
                path.SetAttributeValue("d", MoveEndpoints((string)path.Attribute("d")!, endpoints));
            foreach (var pin in svg.Elements().Where(e => (string?)e.Attribute("class") == "open-pin"))
            {
                int x = (int)pin.Attribute("cx")!, y = (int)pin.Attribute("cy")!;
                pin.SetAttributeValue("cx", endpoints.TryGetValue((x, y), out var point) ? point.x : Fit(x));
            }
            foreach (var output in outputs)
                svg.Add(new XElement(svg.Name.Namespace + "path", new XAttribute("class", "wire"), new XAttribute("d", $"M {output.x} {output.y} H {LadderWidth}")));
        }

        private static string ShiftPath(string path, int shift)
        {
            string command = ""; int coordinate = 0;
            return System.Text.RegularExpressions.Regex.Replace(path, @"[A-Z]|-?\d+", m =>
            {
                if (char.IsLetter(m.Value[0])) { command = m.Value; coordinate = 0; return m.Value; }
                bool horizontal = command == "H" || (command != "V" && coordinate % 2 == 0);
                coordinate++;
                return horizontal ? (int.Parse(m.Value, CultureInfo.InvariantCulture) + shift).ToString(CultureInfo.InvariantCulture) : m.Value;
            });
        }

        private static string MoveEndpoints(string path, Dictionary<(int x, int y), (int x, int y)> endpoints)
        {
            int x = 0, y = 0;
            return System.Text.RegularExpressions.Regex.Replace(path, @"M (-?\d+) (-?\d+)|([HV]) (-?\d+)", m =>
            {
                if (m.Groups[1].Success) { x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
                else if (m.Groups[3].Value == "H") x = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                else y = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (!endpoints.TryGetValue((x, y), out var point)) return m.Value;
                return m.Groups[1].Success ? $"M {point.x} {point.y}" : m.Groups[3].Value == "H" ? "H " + point.x : "V " + point.y;
            });
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
