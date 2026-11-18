using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcp.ReleaseTool.CatalogJson;

namespace TiaMcp.ReleaseTool;

internal sealed class PlcPatternLibrary
{
    private static readonly string[] Families = ["S7-1200", "S7-1500"];
    private static readonly string[] KnownInstructions = ["TON_TIME", "TOF_TIME", "TP_TIME", "CTU_INT", "CTD_INT", "CTUD_INT",
        "R_TRIG", "F_TRIG", "NORM_X", "SCALE_X", "MB_COMM_LOAD", "MB_MASTER", "Modbus_Comm_Load", "Modbus_Master", "MC_POWER"];
    private readonly JsonArray patterns = [];
    private readonly JsonArray rows;

    private PlcPatternLibrary(JsonArray evidence) => rows = evidence;
    internal JsonArray Patterns => (JsonArray)patterns.DeepClone();
    internal JsonArray Evidence => (JsonArray)rows.DeepClone();

    internal static PlcPatternLibrary Load(string root)
    {
        try { return LoadCore(root); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException or ArgumentException)
        { throw new ReleaseException("Invalid PLC pattern library: " + ex.Message); }
    }

    private static PlcPatternLibrary LoadCore(string root)
    {
        string directory = Path.Combine(root, "reference/plc-patterns");
        var evidence = Read(root, "reference/plc-patterns/evidence.json");
        Shape(evidence, ["schemaVersion", "rows"]);
        Require(evidence["schemaVersion"]!.GetValue<int>() == 1, "Unsupported pattern evidence schema");
        var library = new PlcPatternLibrary(evidence["rows"]!.AsArray());
        var rosters = Read(root, "manifest/version-tools.json")["releases"]!;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory, "pattern.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var pattern = JsonNode.Parse(File.ReadAllText(path))!;
            Shape(pattern, ["schemaVersion", "id", "title", "summary", "notesZhCn", "variants"]);
            Require(pattern["schemaVersion"]!.GetValue<int>() == 1, "Unsupported pattern schema");
            string id = Nonempty(pattern, "id");
            Require(Regex.IsMatch(id, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z") && ids.Add(id), "Invalid or duplicate pattern id: " + id);
            Require(Path.GetFullPath(path) == Path.GetFullPath(Path.Combine(directory, id, "pattern.json")), "Pattern directory differs from id: " + id);
            Nonempty(pattern, "title"); Nonempty(pattern, "summary"); StringList(pattern["notesZhCn"]);
            var variants = pattern["variants"]!.AsArray();
            Require(variants.Count > 0, "Pattern needs a variant: " + id);
            var variantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variant in variants)
            {
                ValidateVariant(root, id, variant!, rosters);
                string variantId = Text(variant!["id"]);
                Require(variantIds.Add(variantId), "Duplicate pattern variant: " + id + "/" + variantId);
                foreach (string release in Releases(variant))
                    foreach (string family in Strings(variant["cpuFamilies"])) expected.Add(Cell(id, variantId, release, family));
            }
            library.patterns.Add(pattern);
        }
        Require(ids.Count > 0, "Empty PLC pattern library");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in library.rows)
        {
            ValidateEvidence(row!);
            string cell = Cell(Text(row!["pattern"]), Text(row["variant"]), Text(row["releaseKey"]), Text(row["cpuFamily"]));
            Require(expected.Contains(cell) && actual.Add(cell), "Extra or duplicate pattern evidence: " + cell);
        }
        Require(expected.SetEquals(actual), "Missing pattern evidence: " + string.Join(", ", expected.Except(actual)));
        return library;
    }

    private static void ValidateVariant(string root, string id, JsonNode variant, JsonNode rosters)
    {
        Shape(variant, ["id", "kind", "cpuFamilies", "files", "contains", "instructions", "importSequence", "tools"], ["notReleased"]);
        string variantId = Nonempty(variant, "id");
        Require(new[] { "scl", "scl-s7-1200", "scl-s7-1500", "lad" }.Contains(variantId), "Unknown pattern variant: " + variantId);
        // LAD variants have their own V21 gate; builder registration is never evidence of an absent instruction.
        Require(Text(variant["kind"]) == (variantId == "lad" ? "lad-rung-builder" : "scl-external-source"), "Pattern kind differs from variant");
        string[] families = StringList(variant["cpuFamilies"], nonempty: true);
        Require(families.All(Families.Contains), "Unknown pattern CPU family");
        if (variantId.StartsWith("scl-s7-", StringComparison.Ordinal))
            Require(families.SequenceEqual(new[] { variantId == "scl-s7-1200" ? "S7-1200" : "S7-1500" }), "Variant CPU family mismatch");
        string[] tools = StringList(variant["tools"], nonempty: true);
        foreach (string release in Releases(variant))
            Require(tools.All(Set(Strings(rosters[release]!["tools"])).Contains), "Pattern tool not registered on " + release);
        Require(Text(variant["importSequence"]) == "sequence/stage-scl-function-plc-foundation" || variantId == "lad",
            "Unknown pattern import sequence");
        var instructions = variant["instructions"]!.AsArray();
        var instructionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instruction in instructions)
        {
            Shape(instruction!, ["name", "cpuFamilies", "sourceUrl", "verification"], ["version", "verificationNote"]);
            Require(instructionNames.Add(Nonempty(instruction!, "name")), "Duplicate instruction declaration");
            Require(StringList(instruction!["cpuFamilies"], nonempty: true).All(families.Contains), "Instruction family outside variant");
            string verification = Nonempty(instruction, "verification");
            Require(verification is "verified" or "unverified", "Unknown instruction verification status");
            Require(OfficialUrl(Text(instruction["sourceUrl"])), "Instruction needs an official Siemens URL");
            if (verification == "unverified") Nonempty(instruction, "verificationNote");
            else
            {
                Require(new Uri(Text(instruction["sourceUrl"])).AbsolutePath != "/", "Verified instruction needs its exact manual page");
                Require(!Text(instruction["version"]).Trim().Equals("unverified", StringComparison.OrdinalIgnoreCase),
                    "Verified instruction cannot have an unverified version");
            }
            if (instruction["version"] != null) Nonempty(instruction, "version");
            if (instruction["verificationNote"] != null) Nonempty(instruction, "verificationNote");
        }
        var declarations = variant["contains"]!.AsArray();
        Require(declarations.Count > 0, "Pattern declares no blocks");
        var blockNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in declarations)
        {
            Shape(block!, ["kind", "name"], ["instanceOf"]);
            Require(new[] { "UDT", "FB", "FC", "DB" }.Contains(Text(block!["kind"])), "Unknown pattern block kind");
            Require(Regex.IsMatch(Nonempty(block, "name"), @"\A(?:UDT|FB|FC|DB)_Pat[A-Za-z0-9_]+\z")
                && blockNames.Add(Text(block["name"])), "Invalid or duplicate pattern block name");
            if (block["instanceOf"] != null)
                Require(Text(block["kind"]) == "DB" && declarations.Any(b => Text(b!["kind"]) == "FB"
                    && Text(b["name"]) == Text(block["instanceOf"])), "Unknown instance DB type");
        }
        foreach (var absence in variant["notReleased"]?.AsArray() ?? [])
        {
            Shape(absence!, ["releaseKey", "cpuFamily", "reason", "sourceUrl"]);
            Require(Releases(variant).Contains(Text(absence!["releaseKey"])) && families.Contains(Text(absence["cpuFamily"])), "Invalid static absence cell");
            Nonempty(absence, "reason"); Require(OfficialUrl(Text(absence["sourceUrl"])), "Static absence needs official URL");
        }
        var files = variant["files"]!.AsArray();
        Require(files.Count > 0, "Pattern has no files");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = new StringBuilder();
        foreach (var file in files)
        {
            Shape(file!, ["path", "encoding"]);
            string relative = Nonempty(file!, "path");
            string prefix = "reference/plc-patterns/" + id + "/";
            string absolute = Path.GetFullPath(Path.Combine(root, relative));
            string boundary = Path.GetFullPath(Path.Combine(root, prefix)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(relative.StartsWith(prefix, StringComparison.Ordinal) && !relative.Contains('\\')
                && !relative.Split('/').Contains("..") && absolute.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                && paths.Add(absolute) && File.Exists(absolute), "Pattern file outside directory, duplicate or missing: " + relative);
            if (variantId != "lad")
            {
                Require(Text(file!["encoding"]) == "ASCII" && Path.GetExtension(relative) == ".scl", "Pattern source must be ASCII SCL");
                byte[] bytes = File.ReadAllBytes(absolute);
                Require(bytes.All(b => b < 128 && b != 13 && b != 9), "Pattern source needs ASCII without BOM, LF and no tabs: " + relative);
                source.Append(Encoding.ASCII.GetString(bytes)).Append('\n');
            }
        }
        if (variantId == "lad") PlcPatternLadderInputs.Build(root, variant);
        else ValidateScl(source.ToString(), variant);
    }

    private static void ValidateScl(string source, JsonNode variant)
    {
        var findings = PlcDocumentationLogic.LintScl(source).Where(f => f.Severity is "error" or "warning").ToArray();
        Require(findings.Length == 0, "Pattern SCL lint: " + string.Join("; ", findings.Select(f => $"{f.Rule}:{f.Line} {f.Message}")));
        string code = Regex.Replace(source, @"//[^\n]*|\(\*.*?\*\)", "", RegexOptions.Singleline);
        Require(!Regex.IsMatch(code, @"%\s*(?:I|Q|M|DB)", RegexOptions.IgnoreCase), "Absolute operand in pattern source");
        if (Strings(variant["cpuFamilies"]).Contains("S7-1200"))
            Require(!Regex.IsMatch(code, @"\b(?:LTIME|LTON|LTOF|LTP)(?:_[A-Z0-9]+)?\b|\bLT#", RegexOptions.IgnoreCase),
                "Long timer type not covered on S7-1200");
        var used = Regex.Matches(code, @"\b(?:" + string.Join("|", KnownInstructions) + @")\b", RegexOptions.IgnoreCase);
        foreach (Match token in used)
        {
            var declaration = variant["instructions"]!.AsArray().SingleOrDefault(i => Text(i!["name"]).Equals(token.Value, StringComparison.OrdinalIgnoreCase));
            Require(declaration != null, "Undeclared pattern instruction: " + token.Value);
            Require(Set(Strings(declaration!["cpuFamilies"])).SetEquals(Strings(variant["cpuFamilies"])), "Instruction does not cover variant CPU families: "
                    + token.Value);
        }
        var blocks = Regex.Matches(code, "^\\s*(TYPE|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\\s+\"([^\"]+)\"", RegexOptions.Multiline);
        var declared = variant["contains"]!.AsArray();
        Require(blocks.Count == declared.Count, "Pattern source block count differs from contains");
        for (int i = 0; i < blocks.Count; i++)
        {
            string kind = Text(declared[i]!["kind"]);
            string keyword = kind switch { "UDT" => "TYPE", "FB" => "FUNCTION_BLOCK", "FC" => "FUNCTION", _ => "DATA_BLOCK" };
            Require(blocks[i].Groups[1].Value == keyword && blocks[i].Groups[2].Value == Text(declared[i]!["name"]),
                    "Pattern block order/name differs from contains");
            if (declared[i]!["instanceOf"] != null)
            {
                int end = i + 1 < blocks.Count ? blocks[i + 1].Index : code.Length;
                string body = code[(blocks[i].Index + blocks[i].Length)..end];
                Require(Regex.IsMatch(body, "^\\s*\"" + Regex.Escape(Text(declared[i]!["instanceOf"])) + "\"\\s*;?\\s*$", RegexOptions.Multiline),
                    "Instance DB type line differs from contains");
            }
        }
    }

    private static void ValidateEvidence(JsonNode row)
    {
        Shape(row, ["pattern", "variant", "releaseKey", "cpuFamily", "status"],
            ["date", "package", "commit", "tiaUpdate", "cpu", "firmware", "compile", "instructionVersions", "exportSha256", "nativeMessage", "sourceUrl",
                    "runId"]);
        foreach (string field in new[] { "pattern", "variant", "releaseKey", "cpuFamily", "status" }) Nonempty(row, field);
        string status = Text(row["status"]);
        Require(status is "NOT RUN" or "verified" or "failed" or "absent", "Unknown pattern evidence status");
        if (status == "verified")
        {
            Require(DateOnly.TryParseExact(Nonempty(row, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "Invalid evidence date");
            Require(!string.IsNullOrWhiteSpace(Text(row["package"])) || !string.IsNullOrWhiteSpace(Text(row["commit"])),
                    "Verified evidence needs package or commit");
            foreach (string field in new[] { "tiaUpdate", "cpu", "firmware", "runId" }) Nonempty(row, field);
            Shape(row["compile"]!, ["errors", "warnings"]);
            Require(row["compile"]!["errors"]!.GetValue<int>() == 0 && row["compile"]!["warnings"]!.GetValue<int>() >= 0, "Verified compile needs zero errors");
        }
        if (status is "absent" or "failed") Nonempty(row, "nativeMessage");
        if (status == "absent") Require(OfficialUrl(Nonempty(row, "sourceUrl")), "Absent evidence needs official Siemens URL");
        if (row["instructionVersions"] != null)
            foreach (var version in row["instructionVersions"]!.AsObject())
                Require(!string.IsNullOrWhiteSpace(Text(version.Value)), "Empty instruction version");
        if (row["exportSha256"] != null)
            foreach (var hash in row["exportSha256"]!.AsObject()) Require(Regex.IsMatch(Text(hash.Value), @"\A[0-9a-f]{64}\z"), "Invalid export SHA-256");
    }

    internal static JsonArray Served(string root) => Load(root).Examples();

    private JsonArray Examples()
    {
        var examples = new JsonArray();
        foreach (var pattern in patterns.OrderBy(p => Text(p!["id"]), StringComparer.Ordinal))
            foreach (var variant in pattern!["variants"]!.AsArray().OrderBy(v => Text(v!["id"]), StringComparer.Ordinal))
            {
                var cells = rows.Where(r => Text(r!["pattern"]) == Text(pattern["id"]) && Text(r["variant"]) == Text(variant!["id"])).ToArray();
                var verified = cells.Where(r => Text(r!["status"]) == "verified").ToArray();
                if (verified.Length == 0 || cells.Any(r => Text(r!["status"]) is not ("verified" or "absent"))) continue;
                if (variant!["instructions"]!.AsArray().Any(i => Text(i!["verification"]) != "verified")) continue;
                var availability = new JsonObject();
                foreach (string release in Releases(variant))
                {
                    var families = new JsonObject();
                    foreach (var row in cells.Where(r => Text(r!["releaseKey"]) == release))
                        families[Text(row!["cpuFamily"])] = Text(row["status"]) == "verified"
                            ? "verified " + Text(row["cpu"]) + " " + Text(row["firmware"]) : "absent: " + Text(row["nativeMessage"]);
                    availability[release] = families;
                }
                string[] releases = Releases(variant).Where(k => verified.Any(r => Text(r!["releaseKey"]) == k)).ToArray();
                string absent = string.Join(", ", cells.Where(r => Text(r!["status"]) == "absent")
                    .Select(r => Text(r!["releaseKey"]) + "/" + Text(r["cpuFamily"])));
                var example = new JsonObject {
                    ["id"] = "pattern/" + Text(pattern["id"])
                        + (Text(variant["id"]) == "scl" ? "" : "/" + Text(variant["id"])),
                    ["language"] = "plc-pattern", ["sourceLanguage"] = Text(variant["id"]) == "lad" ? "lad" : "scl",
                    ["kind"] = Text(variant["id"]) == "lad" ? "builder-input" : "complete-source",
                    ["title"] = Copy(pattern["title"]!), ["summary"] = Copy(pattern["summary"]!), ["origin"] = "project pattern",
                    ["releaseKeys"] = new JsonArray(releases.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()), ["availability"] = availability,
                    ["validation"] = "Real-machine verified " + string.Join(", ", releases) + " (" +
                        string.Join(", ", verified.Select(r => Text(r!["package"]) is { Length: > 0 } package ? package : Text(r!["commit"])).Distinct())
                            + "; " +
                        string.Join(", ", verified.Select(r => Text(r!["date"])).Distinct())
                            + (Text(variant["id"]) == "lad"
                                ? "): V21 only; global DB and LAD FC XML import and software compile with 0 errors on "
                                : "): external-source import, block generation and software compile with 0 errors on ") +
                        string.Join(", ", Strings(variant["cpuFamilies"])) + ". Absent: " + (absent.Length == 0 ? "none" : absent)
                            + ". Behaviour on a running PLC NOT RUN."
                };
                foreach (string field in new[] { "cpuFamilies", "contains", "instructions", "importSequence", "tools", "files" }) example[field] =
                    Copy(variant[field]!);
                example["notesZhCn"] = Copy(pattern["notesZhCn"]!);
                examples.Add(example);
            }
        return examples;
    }

    private static IEnumerable<string> Releases(JsonNode variant) => Text(variant["id"]) == "lad" ? new[] { "21" } : Keys;
    private static string Cell(string pattern, string variant, string release, string family) => string.Join("/", pattern, variant, release, family);
    private static bool OfficialUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && (uri.Host == "docs.tia.siemens.cloud" || uri.Host == "support.industry.siemens.com");
    private static string Nonempty(JsonNode node, string field)
    {
        string value = Text(node[field]); Require(!string.IsNullOrWhiteSpace(value), "Missing pattern field: " + field); return value;
    }
    private static string[] StringList(JsonNode? node, bool nonempty = false)
    {
        Require(node is JsonArray, "Pattern field must be an array");
        string[] values = Strings(node);
        Require((!nonempty || values.Length > 0) && values.All(v => !string.IsNullOrWhiteSpace(v))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "Empty or duplicate pattern list item");
        return values;
    }
    private static void Shape(JsonNode node, string[] required, string[]? optional = null)
    {
        Require(node is JsonObject, "Pattern record must be an object");
        var obj = node.AsObject(); var allowed = Set(required.Concat(optional ?? []));
        Require(required.All(k => obj[k] != null) && obj.All(p => allowed.Contains(p.Key)), "Missing or unknown pattern key: " + string
                .Join(", ", obj.Select(p => p.Key)));
    }
}
