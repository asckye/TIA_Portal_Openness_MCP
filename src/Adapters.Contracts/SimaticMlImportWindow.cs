using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // The documented SimaticML import window ("Version-specific SimaticML import", TIA Portal Openness V20/V21; API audit
    // 2026-10-19 F6). Up to V18 the API version must be at least the file's Engineering version; from V19 the file must be
    // within Vxx-3..Vxx (V20 accepts V17-V20, V21 accepts V18-V21). A caller's document is never relabelled: a header outside
    // the window is refused before any native call, an in-window header passes through unchanged.
    public static class SimaticMlImportWindow
    {
        private static readonly Regex Header = new Regex(@"^V(?<major>\d{1,2})(?:\.(?<minor>\d{1,2}))?(?:\s+SP(?<sp>\d{1,2}))?$", RegexOptions.CultureInvariant);

        // Parses the Engineering version spellings TIA writes: "V13 SP1", "V14", "V14 SP1", "V15", "V15.1", "V16" ... "V21".
        // A service pack and a dotted minor are the same ordering step ("V14 SP1" < "V15" < "V15.1" < "V16").
        public static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0);
            var match = Header.Match(text ?? "");
            if (!match.Success || match.Groups["minor"].Success && match.Groups["sp"].Success) return false;
            int major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
            int minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture)
                : match.Groups["sp"].Success ? int.Parse(match.Groups["sp"].Value, CultureInfo.InvariantCulture) : 0;
            if (major < 10) return false;
            version = new Version(major, minor);
            return true;
        }

        public static Version ApiVersion(string release) => release switch
        {
            "14sp1" => new Version(14, 1),
            "15.1" => new Version(15, 1),
            "16" or "17" or "18" or "19" or "20" or "21" => new Version(int.Parse(release, CultureInfo.InvariantCulture), 0),
            _ => throw new ArgumentException("Unsupported TIA release: " + release, nameof(release))
        };

        // Inclusive lower bound; null up to V18 (only "API >= file" applies).
        public static Version? Lowest(string release)
        {
            var api = ApiVersion(release);
            return api.Major >= 19 ? new Version(api.Major - 3, 0) : null;
        }

        public static string Window(string release)
        {
            var api = ApiVersion(release); var lowest = Lowest(release);
            string name = PlcDeclarationText(release);
            return lowest == null ? "engineering versions up to " + name
                : "engineering versions V" + lowest.Major.ToString(CultureInfo.InvariantCulture) + " to " + name;
        }

        private static string PlcDeclarationText(string release) => release == "14sp1" ? "V14 SP1" : "V" + release;

        // Null when the header is inside the window; otherwise the refusal text naming the window and the file's version.
        public static string? Refusal(string release, string? fileVersion)
        {
            var api = ApiVersion(release);
            string window = "TIA Portal " + PlcDeclarationText(release) + " imports SimaticML " + Window(release) + " (Version-specific SimaticML import).";
            if (fileVersion == null) return "The document has no single Engineering version header; " + window + " The MCP never adds or rewrites the header.";
            if (!TryParse(fileVersion, out var version)) return "The document's Engineering version '" + fileVersion + "' is not a TIA Portal version; " + window;
            var lowest = Lowest(release);
            if (version > api || lowest != null && version < lowest)
                return "The document's Engineering version " + fileVersion + " is outside the import window: " + window
                    + " The MCP never rewrites the header; export the object from a TIA Portal release inside the window.";
            return null;
        }

        // Reads the single top-level Engineering version of a SimaticML document; null when absent or ambiguous.
        public static string? ReadVersion(XDocument document)
        {
            var headers = document.Root?.Elements().Where(e => e.Name.LocalName == "Engineering").ToArray() ?? Array.Empty<XElement>();
            if (headers.Length != 1) return null;
            var attributes = headers[0].Attributes().Where(a => a.Name.LocalName == "version").ToArray();
            return attributes.Length == 1 ? attributes[0].Value : null;
        }

        // The first SW.Blocks.*, SW.Types.* or SW.Tags.* object (kind = local name after the last dot) and its AttributeList/Name.
        public static (string Kind, string Name)? ReadObject(XDocument document)
        {
            var native = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)
                || e.Name.LocalName.StartsWith("SW.Types.", StringComparison.Ordinal) || e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.Ordinal));
            var name = native?.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            if (native == null || string.IsNullOrEmpty(name)) return null;
            return (native.Name.LocalName.Substring(native.Name.LocalName.LastIndexOf('.') + 1), name!);
        }

        public static XDocument Load(byte[] bytes)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
            using var input = new MemoryStream(bytes, false); using var xml = XmlReader.Create(input, settings);
            return XDocument.Load(xml);
        }

        // Throws the typed refusal before any native call; parameter names the caller's file argument.
        public static void Require(string release, byte[] bytes, string parameter)
        {
            XDocument document;
            try { document = Load(bytes); }
            catch (XmlException error) { throw new AdapterPreconditionException("The import file is not well-formed SimaticML XML: " + error.Message, parameter, true, error); }
            var refusal = Refusal(release, ReadVersion(document));
            if (refusal != null) throw new AdapterPreconditionException(refusal, parameter, false);
        }
    }
}
