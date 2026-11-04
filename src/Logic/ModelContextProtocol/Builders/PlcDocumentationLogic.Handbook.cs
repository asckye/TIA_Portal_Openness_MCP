using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class PlcDocumentationLogic
    {

        // ------------------------------------------------------------------ handbook over a directory

        public sealed class HandbookEntry
        {
            public string File = "";
            public RenderedBlock? Block;
            public string Error = "";
        }

        public sealed class Handbook
        {
            public List<HandbookEntry> Entries = new List<HandbookEntry>();
            public string Markdown = "";
            public int Failed;
        }

        public static Handbook RenderHandbook(string directory, bool recursive, string[] extensions, string title, string direction = "LR")
        {
            var files = OfflineAnalysisLogic.EnumerateDocuments(directory, recursive, extensions);
            if (files.Count > MaxHandbookFiles)
                throw new InvalidDataException(files.Count + " documents exceed the handbook limit of " + MaxHandbookFiles + ".");
            var hb = new Handbook();
            foreach (var file in files)
            {
                var entry = new HandbookEntry { File = file };
                try { entry.Block = RenderFile(file, direction); }
                catch (Exception ex) { entry.Error = ex.Message; hb.Failed++; }
                hb.Entries.Add(entry);
            }
            hb.Markdown = HandbookMarkdown(hb, directory, title);
            return hb;
        }

        public static string HandbookMarkdown(Handbook hb, string directory, string title)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "PLC program documentation" : title).Append("\n\n");
            sb.Append("Source directory: `").Append(directory).Append("` — ").Append(hb.Entries.Count).Append(" document(s)");
            if (hb.Failed > 0) sb.Append(", ").Append(hb.Failed).Append(" failed to parse");
            sb.Append(". Generated offline from exported documents; not a Siemens compile verdict.\n\n");

            var blocks = hb.Entries.Where(e => e.Block != null).Select(e => e.Block!).OrderBy(b => TypeRank(b.Document.BlockType))
                .ThenBy(b => b.Document.BlockName, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("## Index\n\n| Block | Type | Number | Language | Networks | Interface members | Calls |\n|---|---|---|---|---|---|---|\n");
            foreach (var b in blocks)
            {
                var d = b.Document;
                sb.Append("| [`").Append(d.BlockName).Append("`](#").Append(Anchor(Heading(d))).Append(") | ").Append(d.BlockType).Append(" | ")
                  .Append(d.Attributes.TryGetValue("Number", out var n) ? n : "").Append(" | ").Append(d.Language).Append(" | ").Append(d.Networks.Count)
                      .Append(" | ")
                  .Append(d.Members.Count).Append(" | ").Append(d.BlockCalls.Distinct().Count()).Append(" |\n");
            }
            sb.Append('\n');

            var byName = blocks.GroupBy(b => b.Document.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var calledBy = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in blocks)
                foreach (var call in b.Document.BlockCalls.Distinct())
                {
                    var target = call.Contains(':') ? call.Substring(call.IndexOf(':') + 1) : call;
                    if (target.Contains('.')) target = target.Substring(target.LastIndexOf('.') + 1);
                    if (!calledBy.TryGetValue(target, out var set)) calledBy[target] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(b.Document.BlockName);
                }
            if (calledBy.Count > 0)
            {
                sb.Append("## Call cross-reference\n\n| Called block | In program export | Callers |\n|---|---|---|\n");
                foreach (var kv in calledBy)
                    sb.Append("| `").Append(kv.Key).Append("` | ").Append(byName.ContainsKey(kv.Key) ? "yes" : "no (library/system/other export)").Append(" | ")
                        .Append(string.Join(", ", kv.Value.Select(c => "`" + c + "`"))).Append(" |\n");
                sb.Append('\n');
            }
            foreach (var entry in hb.Entries.Where(e => e.Block == null))
                sb.Append("- Failed: `").Append(entry.File).Append("` — ").Append(entry.Error).Append('\n');
            if (hb.Failed > 0) sb.Append('\n');

            foreach (var b in blocks)
            {
                sb.Append("---\n\n");
                // Demote headings of the single-block render by one level.
                foreach (var line in b.Markdown.Split('\n'))
                    sb.Append(line.StartsWith("#", StringComparison.Ordinal) ? "#" + line : line).Append('\n');
                sb.Append("Source file: `").Append(b.Document.SourcePath).Append("`\n\n");
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        public static string Heading(OfflineAnalysisLogic.BlockDocument doc)
        {
            var number = doc.Attributes.TryGetValue("Number", out var num) ? " " + num : "";
            var name = doc.BlockName.Length > 0 ? doc.BlockName : Path.GetFileNameWithoutExtension(doc.SourcePath);
            return doc.BlockType + number + " `" + name + "`" + (doc.Language.Length > 0 ? " (" + doc.Language + ")" : "");
        }

        private static int TypeRank(string type) => type == "OB" ? 0 : type == "FB" ? 1 : type == "FC" ? 2 : type == "GlobalDB" ? 3 : type == "InstanceDB" ? 4
            : type.StartsWith("Plc", StringComparison.Ordinal) ? 5 : 6;

        private static string Anchor(string heading)
        {
            // GitHub-style slug of the block heading: lower-case, punctuation dropped, spaces to hyphens.
            var s = Regex.Replace(heading.ToLowerInvariant(), @"[^\w\- ]", "").Replace(' ', '-');
            return s;
        }
    }
}
