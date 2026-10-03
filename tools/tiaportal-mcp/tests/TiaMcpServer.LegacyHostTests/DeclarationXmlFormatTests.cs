using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TiaMcp.LegacyHost;
using TiaMcpServer.ModelContextProtocol;

internal static class DeclarationXmlFormatTests
{
    internal static void Run(Action<bool, string> check)
    {
        var sdkRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT");
        var releases = new[]
        {
            (Key: "14sp1", Folder: "TIA_V14SP1_PublicAPI/V14 SP1", Header: "V14 SP1", Interface: "v2", ObjectNamespace: false),
            (Key: "15.1", Folder: "TIA_V15.1_PublicAPI/V15.1", Header: "V15.1", Interface: "v3", ObjectNamespace: false),
            (Key: "16", Folder: "TIA_V16_PublicAPI/V16", Header: "V16", Interface: "v4", ObjectNamespace: false),
            (Key: "17", Folder: "TIA_V17_PublicAPI/V17", Header: "V17", Interface: "v4", ObjectNamespace: false),
            (Key: "18", Folder: "TIA_V18_PublicAPI/V18", Header: "V18", Interface: "v5", ObjectNamespace: true),
            (Key: "19", Folder: "TIA_V19_PublicAPI/V19", Header: "V19", Interface: "v5", ObjectNamespace: true),
            (Key: "20", Folder: "TIA_V20_PublicAPI/V20", Header: "V20", Interface: "v5", ObjectNamespace: true),
            (Key: "21", Folder: "TIA_V21_PublicAPI/V21/net48", Header: "V21", Interface: "v5", ObjectNamespace: true)
        };
        const string udtJson = """{"name":"UDT_Status","members":[{"name":"Ready","datatype":"Bool","externalWritable":true,"comment":"就绪 <状态> & 输出"},{"name":"Counter","datatype":"Int"},{"name":"Speed","datatype":"Real"},{"name":"Caption","datatype":"String[20]"}]}""";
        const string dbJson = """{"dbName":"DB_Status","dbNumber":42,"staticMembers":[{"name":"Ready","datatype":"Bool","externalWritable":false,"comment":"就绪 <状态> & 输出","startValue":"TRUE"},{"name":"Counter","datatype":"Int","startValue":"12"},{"name":"Speed","datatype":"Real","startValue":"1.25"},{"name":"Caption","datatype":"String[20]","startValue":"'Ready'"}]}""";

        foreach (var release in releases)
        foreach (var kind in new[] { "UDT", "GlobalDB" })
        {
            var result = kind == "UDT"
                ? OfflineXmlBuilders.Build("BuildPlcUdtXml", release.Key, udtJson)
                : OfflineCompositionBuilders.Build("BuildPlcGlobalDbXml", release.Key, dbJson);
            var xml = result["Xml"]!.GetValue<string>();
            var document = XDocument.Parse(xml);
            var label = release.Key + " " + kind;
            var objectName = kind == "UDT" ? "SW.Types.PlcStruct" : "SW.Blocks.GlobalDB";
            var attrs = document.Root!.Element(objectName)!.Element("AttributeList")!;
            var ns = "http://www.siemens.com/automation/Openness/SW/Interface/" + release.Interface;
            var sections = attrs.Element("Interface")!.Elements().Single();
            check((string?)document.Root.Element("Engineering")!.Attribute("version") == release.Header, label + " exact engineering format");
            check(sections.Name == XName.Get("Sections", ns) && sections.DescendantsAndSelf().All(element => element.Name.NamespaceName == ns), label + " target schema applied to every interface element");
            check((attrs.Element("Namespace") != null) == release.ObjectNamespace, label + " target object attributes");
            check(sections.Descendants(XName.Get("Member", ns)).Select(element => (string?)element.Attribute("Name")).SequenceEqual(new[] { "Ready", "Counter", "Speed", "Caption" }), label + " members retained in order");
            check(sections.Descendants(XName.Get("MultiLanguageText", ns)).Single().Value == "就绪 <状态> & 输出", label + " member text retained and escaped");
            check(result["Data"]!["outputReleaseKey"]!.GetValue<string>() == release.Key && result["Data"]!["interfaceNamespace"]!.GetValue<string>() == ns, label + " response describes actual output");
            check(!result["Data"]!["schemaValidated"]!.GetValue<bool>() && !result["Data"]!["importValidated"]!.GetValue<bool>(), label + " runtime does not claim per-call XSD or native validation");
            if (kind == "GlobalDB")
            {
                check(attrs.Element("Number")!.Value == "42" && attrs.Element("MemoryLayout")!.Value == "Standard", label + " DB identity and layout retained");
                check(sections.Descendants(XName.Get("StartValue", ns)).Select(element => element.Value).SequenceEqual(new[] { "TRUE", "12", "1.25", "'Ready'" }), label + " typed initializers retained");
                check(sections.Descendants(XName.Get("Member", ns)).First().Elements().Select(element => element.Name.LocalName).SequenceEqual(new[] { "AttributeList", "StartValue", "Comment" }), label + " initializer and comment child order");
            }

            if (string.IsNullOrEmpty(sdkRoot)) continue;
            var schemaDirectory = Path.Combine(sdkRoot, release.Folder.Replace('/', Path.DirectorySeparatorChar), "Schemas");
            // V21 installs fragment XSDs beside the framework directories.
            if (release.Key == "21" && !Directory.Exists(schemaDirectory))
                schemaDirectory = Path.Combine(sdkRoot, "TIA_V21_PublicAPI", "V21", "Schemas");
            var schemaPath = Path.Combine(schemaDirectory, "SW.InterfaceSections_" + release.Interface + ".xsd");
            var issues = new List<string>();
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            schemas.ValidationEventHandler += (_, args) => issues.Add(args.Message);
            schemas.Add(ns, schemaPath);
            schemas.Compile();
            new XDocument(new XElement(sections)).Validate(schemas, (_, args) => issues.Add(args.Message));
            check(issues.Count == 0, label + " official interface XSD: " + string.Join("; ", issues));
            Console.WriteLine("[XSD] " + label + " " + Path.GetFileName(schemaPath) + " sha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant());
        }
        if (string.IsNullOrEmpty(sdkRoot))
            Console.WriteLine("[XSD NOT RUN] Set TIA_MCP_TEST_PUBLIC_API_ROOT to the eight official PublicAPI directories to validate generated declaration interfaces. Structure tests still ran; native import is separate.");
    }
}
