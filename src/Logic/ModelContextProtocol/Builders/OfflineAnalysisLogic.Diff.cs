using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class OfflineAnalysisLogic
    {

        // ------------------------------------------------------------------ Myers diff (linear space, middle snake)

        public static List<DiffEntry> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count > MaxDiffLinesPerSide || b.Count > MaxDiffLinesPerSide)
                throw new ArgumentException("Documents exceed the diff bound of " + MaxDiffLinesPerSide + " normalized lines per side.");
            var output = new List<DiffEntry>();
            var stack = new Stack<object>();
            stack.Push(new int[] { 0, a.Count, 0, b.Count });
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (item is List<DiffEntry> deferred) { output.AddRange(deferred); continue; }
                var seg = (int[])item;
                int aStart = seg[0], aEnd = seg[1], bStart = seg[2], bEnd = seg[3];
                while (aStart < aEnd && bStart < bEnd && a[aStart] == b[bStart])
                {
                    output.Add(new DiffEntry { Op = DiffOp.Equal, Text = a[aStart], LeftLine = aStart + 1, RightLine = bStart + 1 });
                    aStart++; bStart++;
                }
                var suffix = new List<DiffEntry>();
                while (aStart < aEnd && bStart < bEnd && a[aEnd - 1] == b[bEnd - 1])
                {
                    suffix.Add(new DiffEntry { Op = DiffOp.Equal, Text = a[aEnd - 1], LeftLine = aEnd, RightLine = bEnd });
                    aEnd--; bEnd--;
                }
                suffix.Reverse();
                if (suffix.Count > 0) stack.Push(suffix);
                if (aStart == aEnd)
                {
                    for (var j = bStart; j < bEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Insert, Text = b[j], RightLine = j + 1 });
                    continue;
                }
                if (bStart == bEnd)
                {
                    for (var j = aStart; j < aEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Delete, Text = a[j], LeftLine = j + 1 });
                    continue;
                }
                var (x, y) = MiddleSnake(a, b, aStart, aEnd, bStart, bEnd);
                if ((x == aStart && y == bStart) || (x == aEnd && y == bEnd))
                {
                    for (var j = aStart; j < aEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Delete, Text = a[j], LeftLine = j + 1 });
                    for (var j = bStart; j < bEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Insert, Text = b[j], RightLine = j + 1 });
                    continue;
                }
                stack.Push(new int[] { x, aEnd, y, bEnd });
                stack.Push(new int[] { aStart, x, bStart, y });
            }
            return output;
        }

        private static (int x, int y) MiddleSnake(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd)
        {
            var n = aEnd - aStart; var m = bEnd - bStart;
            var max = (n + m + 1) / 2 + 1;
            var delta = n - m;
            var vf = new int[2 * max + 2];
            var vb = new int[2 * max + 2];
            var off = max;
            vf[off + 1] = 0; vb[off + 1] = 0;
            var oddDelta = (delta & 1) != 0;
            for (var d = 0; d <= max; d++)
            {
                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && vf[off + k - 1] < vf[off + k + 1])) x = vf[off + k + 1]; else x = vf[off + k - 1] + 1;
                    var y = x - k;
                    while (x < n && y < m && a[aStart + x] == b[bStart + y]) { x++; y++; }
                    vf[off + k] = x;
                    if (oddDelta && delta - k >= -(d - 1) && delta - k <= d - 1)
                    {
                        if (x + vb[off + delta - k] >= n) return (aStart + x, bStart + y);
                    }
                }
                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && vb[off + k - 1] < vb[off + k + 1])) x = vb[off + k + 1]; else x = vb[off + k - 1] + 1;
                    var y = x - k;
                    while (x < n && y < m && a[aEnd - 1 - x] == b[bEnd - 1 - y]) { x++; y++; }
                    vb[off + k] = x;
                    if (!oddDelta && delta - k >= -d && delta - k <= d)
                    {
                        if (vf[off + delta - k] + x >= n) return (aEnd - x, bEnd - y);
                    }
                }
            }
            return (aStart, bStart);
        }

        public static JsonArray BuildHunks(List<DiffEntry> entries, int contextLines)
        {
            var hunks = new JsonArray();
            var i = 0;
            while (i < entries.Count)
            {
                if (entries[i].Op == DiffOp.Equal) { i++; continue; }
                var start = Math.Max(0, i - contextLines);
                var end = i;
                var lastChange = i;
                while (end < entries.Count)
                {
                    if (entries[end].Op != DiffOp.Equal) lastChange = end;
                    else if (end - lastChange > 2 * contextLines) break;
                    end++;
                }
                end = Math.Min(entries.Count, lastChange + contextLines + 1);
                var lines = new JsonArray();
                int leftStart = 0, rightStart = 0, leftCount = 0, rightCount = 0, added = 0, removed = 0;
                for (var j = start; j < end; j++)
                {
                    var e = entries[j];
                    if (e.LeftLine > 0) { if (leftStart == 0) leftStart = e.LeftLine; leftCount++; }
                    if (e.RightLine > 0) { if (rightStart == 0) rightStart = e.RightLine; rightCount++; }
                    if (e.Op == DiffOp.Insert) added++; else if (e.Op == DiffOp.Delete) removed++;
                    lines.Add((e.Op == DiffOp.Equal ? " " : e.Op == DiffOp.Delete ? "-" : "+") + e.Text);
                }
                hunks.Add(new JsonObject
                {
                    ["leftStart"] = leftStart, ["leftCount"] = leftCount, ["rightStart"] = rightStart, ["rightCount"] = rightCount,
                    ["added"] = added, ["removed"] = removed, ["lines"] = lines
                });
                i = end;
            }
            return hunks;
        }

        // ------------------------------------------------------------------ compare

        public static JsonObject CompareFiles(string leftPath, string rightPath, int offset, int limit, int contextLines = 2)
            => Compare(ParseFile(leftPath), ParseFile(rightPath), offset, limit, contextLines);

        public static JsonObject Compare(BlockDocument left, BlockDocument right, int offset, int limit, int contextLines = 2)
        {
            ValidatePage(offset, limit);
            var identical = left.Canonical.SequenceEqual(right.Canonical, StringComparer.Ordinal);
            var structural = CompareStructure(left, right);
            var entries = identical ? new List<DiffEntry>() : Diff(left.Canonical, right.Canonical);
            var hunks = BuildHunks(entries, contextLines);
            var page = new JsonArray();
            for (var i = offset; i < hunks.Count && page.Count < limit; i++) page.Add(hunks[i]!.DeepClone());
            var result = new JsonObject
            {
                ["left"] = Describe(left),
                ["right"] = Describe(right),
                ["identicalAfterNormalization"] = identical,
                ["structurallyIdentical"] = structural["differenceCount"]!.GetValue<int>() == 0,
                ["formatMismatch"] = !string.Equals(left.Format, right.Format, StringComparison.Ordinal),
                ["structural"] = structural,
                ["lineStats"] = new JsonObject
                {
                    ["added"] = entries.Count(e => e.Op == DiffOp.Insert),
                    ["removed"] = entries.Count(e => e.Op == DiffOp.Delete),
                    ["unchanged"] = entries.Count(e => e.Op == DiffOp.Equal),
                },
                ["hunkCount"] = hunks.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["hunks"] = page,
                ["dataComplete"] = offset + page.Count >= hunks.Count,
                ["normalizationRules"] = new JsonArray(NormalizationRules.Select(r => (JsonNode)r).ToArray()),
            };
            if (!string.Equals(left.Format, right.Format, StringComparison.Ordinal))
                result["warning"] = "Documents have different formats (" + left.Format + " vs " + right.Format
                    + "); the line diff compares different canonical renderings, rely on the structural section.";
            return result;
        }

        private static JsonObject Describe(BlockDocument doc) => new JsonObject
        {
            ["path"] = doc.SourcePath, ["format"] = doc.Format, ["blockName"] = doc.BlockName, ["blockType"] = doc.BlockType, ["language"] = doc.Language,
            ["canonicalLines"] = doc.Canonical.Count, ["networkCount"] = doc.Networks.Count, ["memberCount"] = doc.Members.Count,
            ["warnings"] = new JsonArray(doc.Warnings.Select(w => (JsonNode)w).ToArray()),
        };

        private static JsonObject Pair(string l, string r) => new JsonObject {
            ["left"] = l, ["right"] = r, ["equal"] = string.Equals(l, r, StringComparison.Ordinal) };

        public static JsonObject CompareStructure(BlockDocument left, BlockDocument right)
        {
            var differences = 0;
            var sameFormat = string.Equals(left.Format, right.Format, StringComparison.Ordinal);
            var result = new JsonObject();
            foreach (var (key, l, r) in new[] {
                    ("blockName", left.BlockName, right.BlockName), ("blockType", left.BlockType, right.BlockType), ("language", left.Language, right.Language),
                        ("title", left.Title, right.Title), ("comment", left.Comment, right.Comment)
                })
            {
                result[key] = Pair(l, r);
                if (!string.Equals(l, r, StringComparison.Ordinal)) differences++;
            }

            var changed = new JsonArray(); var onlyLeft = new JsonArray(); var onlyRight = new JsonArray();
            foreach (var kv in left.Attributes)
            {
                if (right.Attributes.TryGetValue(kv.Key, out var rv))
                {
                    if (!string.Equals(kv.Value, rv, StringComparison.Ordinal)) {
                        changed.Add(new JsonObject { ["name"] = kv.Key, ["left"] = kv.Value, ["right"] = rv }); differences++; }
                }
                else { onlyLeft.Add(kv.Key); if (sameFormat) differences++; }
            }
            foreach (var kv in right.Attributes.Where(kv => !left.Attributes.ContainsKey(kv.Key))) { onlyRight.Add(kv.Key); if (sameFormat) differences++; }
            result["attributes"] = new JsonObject { ["changed"] = changed, ["onlyLeft"] = onlyLeft, ["onlyRight"] = onlyRight };

            var added = new JsonArray(); var removed = new JsonArray(); var typeChanged = new JsonArray();
            var leftMembers = left.Members.ToDictionary(m => m.Section + "/" + m.Path, m => m, StringComparer.Ordinal);
            var rightMembers = right.Members.ToDictionary(m => m.Section + "/" + m.Path, m => m, StringComparer.Ordinal);
            foreach (var kv in leftMembers)
            {
                if (!rightMembers.TryGetValue(kv.Key, out var rm)) { removed.Add(MemberJson(kv.Value)); differences++; continue; }
                if (!SameType(kv.Value.Datatype, rm.Datatype)) {
                    typeChanged.Add(new JsonObject { ["section"] = rm.Section, ["path"] = rm.Path, ["left"] = kv.Value.Datatype, ["right"] = rm.Datatype });
                    differences++;
                }
            }
            foreach (var kv in rightMembers.Where(kv => !leftMembers.ContainsKey(kv.Key))) { added.Add(MemberJson(kv.Value)); differences++; }
            var leftSections = left.Members.Select(m => m.Section).Distinct().ToList();
            var rightSections = right.Members.Select(m => m.Section).Distinct().ToList();
            result["interface"] = new JsonObject
            {
                ["memberCount"] = new JsonObject { ["left"] = left.Members.Count, ["right"] = right.Members.Count },
                ["added"] = added, ["removed"] = removed, ["typeChanged"] = typeChanged,
                ["sectionsAdded"] = new JsonArray(rightSections.Except(leftSections).Select(s => (JsonNode)s).ToArray()),
                ["sectionsRemoved"] = new JsonArray(leftSections.Except(rightSections).Select(s => (JsonNode)s).ToArray()),
            };

            var titlesChanged = new JsonArray(); var commentsChanged = new JsonArray(); var languagesChanged = new JsonArray();
            var common = Math.Min(left.Networks.Count, right.Networks.Count);
            for (var i = 0; i < common; i++)
            {
                var ln = left.Networks[i]; var rn = right.Networks[i];
                if (!string.Equals(ln.Title, rn.Title, StringComparison.Ordinal)) {
                    titlesChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Title, ["right"] = rn.Title }); differences++; }
                if (!string.Equals(ln.Comment, rn.Comment, StringComparison.Ordinal)) {
                    commentsChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Comment, ["right"] = rn.Comment }); differences++; }
                if (!string.Equals(ln.Language, rn.Language, StringComparison.OrdinalIgnoreCase)) {
                    languagesChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Language, ["right"] = rn.Language }); differences++; }
            }
            if (left.Networks.Count != right.Networks.Count) differences++;
            result["networks"] = new JsonObject
            {
                ["count"] = new JsonObject {
                    ["left"] = left.Networks.Count, ["right"] = right.Networks.Count, ["equal"] = left.Networks.Count == right.Networks.Count },
                ["added"] = new JsonArray(right.Networks.Skip(common).Select(n => (JsonNode)n.Index).ToArray()),
                ["removed"] = new JsonArray(left.Networks.Skip(common).Select(n => (JsonNode)n.Index).ToArray()),
                ["titlesChanged"] = titlesChanged, ["commentsChanged"] = commentsChanged, ["languagesChanged"] = languagesChanged,
                ["titles"] = new JsonObject
                {
                    ["left"] = new JsonArray(left.Networks.Select(n => (JsonNode)n.Title).ToArray()),
                    ["right"] = new JsonArray(right.Networks.Select(n => (JsonNode)n.Title).ToArray()),
                },
            };
            result["differenceCount"] = differences;
            return result;
        }

        private static JsonObject MemberJson(InterfaceMember m) => new JsonObject { ["section"] = m.Section, ["path"] = m.Path, ["datatype"] = m.Datatype };

        private static bool SameType(string l, string r) => string.Equals(CollapseWhitespace(l), CollapseWhitespace(r), StringComparison.OrdinalIgnoreCase);

        public static void ValidatePage(int offset, int limit)
        {
            if (offset < 0) throw new ArgumentException("offset must be >= 0.");
            if (limit < 1 || limit > MaxLimit) throw new ArgumentException("limit must be between 1 and " + MaxLimit + ".");
        }
    }
}
