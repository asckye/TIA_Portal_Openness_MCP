using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    // Offline generator of an AutomationML (CAEX 2.15) hardware description for CaxProvider.Import
    // (ImportDeviceAml). The instance hierarchy follows the "Automation Project Configuration" application
    // recommendation that TIA Portal exports use: Project > Device > Rack/DeviceItem tree with
    // TypeIdentifier ("OrderNumber:6ES7 ...", "System:Device.S71500") and PositionNumber attributes,
    // CommunicationInterface / Node elements with NetworkAddress, and Subnet elements linked to nodes.
    //
    // Two modes for the parts that must match the importing TIA version exactly (file header, role class
    // libraries, interface class library):
    //   - referenceAml given: everything except the InstanceHierarchy is copied verbatim from a file that
    //     ExportDeviceAml produced on the same TIA version. This is the recommended path.
    //   - no reference: a built-in skeleton with the standard library declarations is emitted. It has not
    //     been validated against a TIA import on this project; the result carries importVerified=false.
    public static class HardwareAmlLogic
    {
        public const string RoleLib = "AutomationProjectConfigurationRoleClassLib";
        public const string BaseRoleLib = "AutomationMLBaseRoleClassLib";
        public const string InterfaceLib = "CommunicationInterfaceClassLib";
        public const int MaxElements = 5000;

        public sealed class Spec
        {
            public string ProjectName = "Project";
            public List<DeviceSpec> Devices = new List<DeviceSpec>();
            public List<SubnetSpec> Subnets = new List<SubnetSpec>();
        }

        public sealed class DeviceSpec
        {
            public string Name = "";
            public string TypeIdentifier = "";
            public List<ItemSpec> Items = new List<ItemSpec>();
        }

        public sealed class ItemSpec
        {
            public string Name = "";
            public string TypeIdentifier = "";
            public int? PositionNumber;
            public bool BuiltIn;
            public string FirmwareVersion = "";
            public string Role = "DeviceItem";   // Rack | DeviceItem | CommunicationInterface | CommunicationPort
            public string Comment = "";
            public string Label = "";
            public List<ItemSpec> Items = new List<ItemSpec>();
            public List<NodeSpec> Nodes = new List<NodeSpec>();
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public sealed class NodeSpec
        {
            public string Name = "";
            public string NetworkAddress = "";
            public string SubnetMask = "";
            public string RouterAddress = "";
            public string SubnetName = "";
            public string PnDeviceName = "";
        }

        public sealed class SubnetSpec
        {
            public string Name = "";
            public string Type = "Ethernet";   // Ethernet | Profibus | ...
        }

        public sealed class Result
        {
            public XDocument Document = new XDocument();
            public int Devices;
            public int DeviceItems;
            public int Nodes;
            public int Subnets;
            public int Links;
            public string LibraryOrigin = "";
            public List<string> Warnings = new List<string>();
        }

        // ------------------------------------------------------------------ spec parsing

        public static Spec ParseSpec(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("specJson is empty.");
            JsonNode? node;
            try { node = JsonNode.Parse(json!); }
            catch (JsonException ex) { throw new ArgumentException("specJson is not valid JSON: " + ex.Message); }
            var obj = node as JsonObject ?? throw new ArgumentException("specJson must be a JSON object.");
            var spec = new Spec { ProjectName = Str(obj, "projectName", "Project") };
            var devices = obj["devices"] as JsonArray ?? throw new ArgumentException("specJson.devices must be an array with at least one device.");
            if (devices.Count == 0) throw new ArgumentException("specJson.devices is empty.");
            var count = 0;
            foreach (var d in devices)
            {
                var o = d as JsonObject ?? throw new ArgumentException("devices[] entries must be objects.");
                var dev = new DeviceSpec { Name = Str(o, "name", ""), TypeIdentifier = Str(o, "typeIdentifier", "") };
                if (dev.Name.Length == 0) throw new ArgumentException("Every device needs a name.");
                if (dev.TypeIdentifier.Length == 0) throw new ArgumentException("Device '" + dev.Name + "' needs a typeIdentifier such as 'System:Device.S71500'.");
                var items = o["deviceItems"] as JsonArray ?? throw new ArgumentException("Device '" + dev.Name + "' needs deviceItems (the rack with its modules).");
                foreach (var it in items) dev.Items.Add(ParseItem(it, dev.Name, ref count, 0));
                spec.Devices.Add(dev);
            }
            if (obj["subnets"] is JsonArray subnets)
                foreach (var s in subnets)
                {
                    var o = s as JsonObject ?? throw new ArgumentException("subnets[] entries must be objects.");
                    var sub = new SubnetSpec { Name = Str(o, "name", ""), Type = Str(o, "type", "Ethernet") };
                    if (sub.Name.Length == 0) throw new ArgumentException("Every subnet needs a name.");
                    spec.Subnets.Add(sub);
                }
            var referenced = spec.Devices.SelectMany(AllItems).SelectMany(i => i.Nodes).Select(n => n.SubnetName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            foreach (var r in referenced) if (!spec.Subnets.Any(s => s.Name == r)) spec.Subnets.Add(new SubnetSpec { Name = r });
            return spec;
        }

        private static ItemSpec ParseItem(JsonNode? node, string owner, ref int count, int depth)
        {
            if (++count > MaxElements) throw new ArgumentException("More than " + MaxElements + " device items.");
            if (depth > 8) throw new ArgumentException("deviceItems nesting deeper than 8 levels.");
            var o = node as JsonObject ?? throw new ArgumentException("deviceItems[] entries of '" + owner + "' must be objects.");
            var item = new ItemSpec
            {
                Name = Str(o, "name", ""), TypeIdentifier = Str(o, "typeIdentifier", ""), FirmwareVersion = Str(o, "firmwareVersion", ""),
                Comment = Str(o, "comment", ""), Label = Str(o, "label", ""), Role = Str(o, "role", depth == 0 ? "Rack" : "DeviceItem")
            };
            if (item.Name.Length == 0) throw new ArgumentException("Every device item of '" + owner + "' needs a name.");
            if (o["positionNumber"] != null) item.PositionNumber = o["positionNumber"]!.GetValue<int>();
            if (o["builtIn"] != null) item.BuiltIn = o["builtIn"]!.GetValue<bool>();
            if (!new[] { "Rack", "DeviceItem", "CommunicationInterface", "CommunicationPort" }.Contains(item.Role)) throw new ArgumentException("Item '" + item.Name + "': role must be Rack, DeviceItem, CommunicationInterface or CommunicationPort.");
            if (item.Role == "Rack" && item.PositionNumber == null) item.PositionNumber = 0;
            if ((item.Role == "DeviceItem" || item.Role == "Rack") && !item.BuiltIn && item.TypeIdentifier.Length == 0) throw new ArgumentException("Item '" + item.Name + "' needs a typeIdentifier (e.g. 'OrderNumber:6ES7 521-1BL00-0AB0/V2.0') unless builtIn=true.");
            if (o["attributes"] is JsonObject attrs) foreach (var kv in attrs) item.Attributes[kv.Key] = kv.Value?.ToString() ?? "";
            if (o["nodes"] is JsonArray nodes)
                foreach (var n in nodes)
                {
                    var no = n as JsonObject ?? throw new ArgumentException("nodes[] entries of '" + item.Name + "' must be objects.");
                    var ns = new NodeSpec { Name = Str(no, "name", ""), NetworkAddress = Str(no, "networkAddress", ""), SubnetMask = Str(no, "subnetMask", ""), RouterAddress = Str(no, "routerAddress", ""), SubnetName = Str(no, "subnetName", ""), PnDeviceName = Str(no, "pnDeviceName", "") };
                    if (ns.Name.Length == 0) ns.Name = "E" + (item.Nodes.Count + 1);
                    if (ns.NetworkAddress.Length > 0 && !System.Net.IPAddress.TryParse(ns.NetworkAddress, out _) && !ns.NetworkAddress.All(char.IsDigit)) throw new ArgumentException("Node '" + ns.Name + "' has an invalid networkAddress '" + ns.NetworkAddress + "'.");
                    item.Nodes.Add(ns);
                }
            if (o["deviceItems"] is JsonArray children) foreach (var c in children) item.Items.Add(ParseItem(c, item.Name, ref count, depth + 1));
            return item;
        }

        private static string Str(JsonObject o, string key, string fallback) => o[key] == null ? fallback : o[key]!.ToString().Trim();

        public static IEnumerable<ItemSpec> AllItems(DeviceSpec d) => d.Items.SelectMany(AllItems);
        private static IEnumerable<ItemSpec> AllItems(ItemSpec i) => new[] { i }.Concat(i.Items.SelectMany(AllItems));

        // ------------------------------------------------------------------ building

        public static Result Build(Spec spec, string? referenceAmlPath, string fileName, string writerVersion)
        {
            var result = new Result();
            XElement caex;
            XElement? referenceInstanceHierarchy = null;
            if (!string.IsNullOrWhiteSpace(referenceAmlPath))
            {
                if (!Path.IsPathRooted(referenceAmlPath)) throw new ArgumentException("referenceAmlPath must be absolute.");
                if (!File.Exists(referenceAmlPath)) throw new FileNotFoundException("referenceAmlPath not found: " + referenceAmlPath, referenceAmlPath);
                var reference = XDocument.Load(referenceAmlPath, LoadOptions.None);
                var refRoot = reference.Root ?? throw new InvalidDataException("Reference AML is empty.");
                if (refRoot.Name.LocalName != "CAEXFile") throw new InvalidDataException("Reference AML root must be CAEXFile, found " + refRoot.Name.LocalName + ".");
                caex = new XElement(refRoot);
                referenceInstanceHierarchy = caex.Elements().FirstOrDefault(e => e.Name.LocalName == "InstanceHierarchy");
                foreach (var old in caex.Elements().Where(e => e.Name.LocalName == "InstanceHierarchy").ToList()) old.Remove();
                caex.SetAttributeValue("FileName", fileName);
                if (!caex.Elements().Any(e => e.Name.LocalName == "RoleClassLib" && (string?)e.Attribute("Name") == RoleLib))
                    result.Warnings.Add("Reference AML has no " + RoleLib + " declaration; TIA export files normally carry it.");
                result.LibraryOrigin = "reference: " + referenceAmlPath;
            }
            else
            {
                caex = BuiltinSkeleton(fileName, writerVersion);
                result.LibraryOrigin = "builtin (not validated against a TIA import)";
                result.Warnings.Add("No referenceAmlPath: header and role class libraries come from the built-in skeleton. Export any project once with ExportDeviceAml and pass the file as referenceAmlPath for a version-exact header.");
            }

            var ns = caex.Name.Namespace;
            var ih = new XElement(ns + "InstanceHierarchy", new XAttribute("Name", spec.ProjectName + " Instance Hierarchy"));
            if (referenceInstanceHierarchy != null && (string?)referenceInstanceHierarchy.Attribute("Name") is string ihName && ihName.Length > 0) ih.SetAttributeValue("Name", ihName);
            var project = Element(ns, spec.ProjectName);
            ih.Add(project);
            var subnetInterfaceIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sub in spec.Subnets)
            {
                var subnet = Element(ns, sub.Name);
                subnet.Add(Attr(ns, "Type", "xs:string", sub.Type));
                var ifId = NewId();
                subnet.Add(new XElement(ns + "ExternalInterface", new XAttribute("Name", "LogicalEndPoint_Subnet"), new XAttribute("ID", ifId), new XAttribute("RefBaseClassPath", InterfaceLib + "/LogicalEndPoint")));
                subnetInterfaceIds[sub.Name] = ifId;
                project.Add(WithRole(ns, subnet, "Subnet"));
                result.Subnets++;
            }
            var links = new List<XElement>();
            foreach (var dev in spec.Devices)
            {
                var device = Element(ns, dev.Name);
                device.Add(Attr(ns, "TypeIdentifier", "xs:string", dev.TypeIdentifier));
                foreach (var item in dev.Items) device.Add(BuildItem(ns, item, subnetInterfaceIds, links, result));
                project.Add(WithRole(ns, device, "Device"));
                result.Devices++;
            }
            foreach (var link in links) project.Add(link);
            WithRole(ns, project, "AutomationProject");
            result.Links = links.Count;
            // CAEX order: AdditionalInformation*, ExternalReference*, InstanceHierarchy*, InterfaceClassLib*, RoleClassLib*, SystemUnitClassLib*
            var anchor = caex.Elements().FirstOrDefault(e => e.Name.LocalName == "InterfaceClassLib" || e.Name.LocalName == "RoleClassLib" || e.Name.LocalName == "SystemUnitClassLib");
            if (anchor != null) anchor.AddBeforeSelf(ih); else caex.Add(ih);
            result.Document = new XDocument(new XDeclaration("1.0", "utf-8", null), caex);
            return result;
        }

        private static XElement BuildItem(XNamespace ns, ItemSpec item, Dictionary<string, string> subnetInterfaceIds, List<XElement> links, Result result)
        {
            var e = Element(ns, item.Name);
            if (item.TypeIdentifier.Length > 0) e.Add(Attr(ns, "TypeIdentifier", "xs:string", item.TypeIdentifier));
            if (item.PositionNumber != null) e.Add(Attr(ns, "PositionNumber", "xs:int", item.PositionNumber.Value.ToString(CultureInfo.InvariantCulture)));
            e.Add(Attr(ns, "BuiltIn", "xs:boolean", item.BuiltIn ? "true" : "false"));
            if (item.FirmwareVersion.Length > 0) e.Add(Attr(ns, "FirmwareVersion", "xs:string", item.FirmwareVersion));
            if (item.Label.Length > 0) e.Add(Attr(ns, "Label", "xs:string", item.Label));
            if (item.Comment.Length > 0) e.Add(Attr(ns, "Comment", "xs:string", item.Comment));
            foreach (var kv in item.Attributes) e.Add(Attr(ns, kv.Key, "xs:string", kv.Value));
            result.DeviceItems++;
            foreach (var node in item.Nodes)
            {
                var n = Element(ns, node.Name);
                if (node.NetworkAddress.Length > 0) n.Add(Attr(ns, "NetworkAddress", "xs:string", node.NetworkAddress));
                if (node.SubnetMask.Length > 0) n.Add(Attr(ns, "SubnetMask", "xs:string", node.SubnetMask));
                if (node.RouterAddress.Length > 0) n.Add(Attr(ns, "RouterAddress", "xs:string", node.RouterAddress));
                if (node.PnDeviceName.Length > 0) n.Add(Attr(ns, "PnDeviceName", "xs:string", node.PnDeviceName));
                if (node.SubnetName.Length > 0 && subnetInterfaceIds.TryGetValue(node.SubnetName, out var subnetIf))
                {
                    var nodeIf = NewId();
                    n.Add(new XElement(ns + "ExternalInterface", new XAttribute("Name", "LogicalEndPoint_Node"), new XAttribute("ID", nodeIf), new XAttribute("RefBaseClassPath", InterfaceLib + "/LogicalEndPoint")));
                    links.Add(new XElement(ns + "InternalLink", new XAttribute("Name", "Link To " + node.SubnetName + "_" + item.Name + "_" + node.Name), new XAttribute("RefPartnerSideA", nodeIf), new XAttribute("RefPartnerSideB", subnetIf)));
                }
                e.Add(WithRole(ns, n, "Node"));
                result.Nodes++;
            }
            foreach (var child in item.Items) e.Add(BuildItem(ns, child, subnetInterfaceIds, links, result));
            return WithRole(ns, e, item.Role);
        }

        private static XElement Element(XNamespace ns, string name)
            => new XElement(ns + "InternalElement", new XAttribute("Name", name), new XAttribute("ID", NewId()));

        // SupportedRoleClass is the last child of an InternalElement in CAEX 2.15.
        private static XElement WithRole(XNamespace ns, XElement e, string role)
        {
            e.Add(new XElement(ns + "SupportedRoleClass", new XAttribute("RefRoleClassPath", RoleLib + "/" + role)));
            return e;
        }

        private static XElement Attr(XNamespace ns, string name, string type, string value)
            => new XElement(ns + "Attribute", new XAttribute("Name", name), new XAttribute("AttributeDataType", type), new XElement(ns + "Value", value));

        private static string NewId() => Guid.NewGuid().ToString("D");

        public static XElement BuiltinSkeleton(string fileName, string writerVersion)
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            var caex = new XElement("CAEXFile",
                new XAttribute("FileName", fileName),
                new XAttribute("SchemaVersion", "2.15"),
                new XAttribute(xsi + "noNamespaceSchemaLocation", "CAEX_ClassModel_V2.15.xsd"),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                new XElement("AdditionalInformation", new XAttribute("AutomationMLVersion", "2.0")),
                new XElement("AdditionalInformation",
                    new XElement("WriterHeader",
                        new XElement("WriterName", "TiaMcpServer BuildDeviceAmlDocument"),
                        new XElement("WriterID", "TiaMcpServer:" + writerVersion),
                        new XElement("WriterVendor", "asckye/TIA_Portal_Openness_MCP"),
                        new XElement("WriterVendorURL", "https://github.com/asckye/TIA_Portal_Openness_MCP"),
                        new XElement("WriterVersion", writerVersion),
                        new XElement("WriterRelease", writerVersion),
                        new XElement("LastWritingDateTime", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement("WriterProjectTitle", Path.GetFileNameWithoutExtension(fileName)),
                        new XElement("WriterProjectID", NewId()))),
                new XElement("InterfaceClassLib", new XAttribute("Name", InterfaceLib),
                    new XElement("Description", "Communication interface class library"),
                    new XElement("Version", "1.0.0"),
                    new XElement("InterfaceClass", new XAttribute("Name", "LogicalEndPoint"), new XAttribute("RefBaseClassPath", "AutomationMLInterfaceClassLib/AutomationMLBaseInterface"))),
                new XElement("RoleClassLib", new XAttribute("Name", BaseRoleLib),
                    new XElement("Description", "Automation Markup Language base role class library"),
                    new XElement("Version", "2.2.2"),
                    new XElement("RoleClass", new XAttribute("Name", "AutomationMLBaseRole"))),
                new XElement("RoleClassLib", new XAttribute("Name", RoleLib),
                    new XElement("Description", "Automation Project Configuration Role Class Library"),
                    new XElement("Version", "1.0.0"),
                    new XElement("RoleClass", new XAttribute("Name", "AutomationProject"), new XAttribute("RefBaseClassPath", BaseRoleLib + "/AutomationMLBaseRole")),
                    new XElement("RoleClass", new XAttribute("Name", "Device"), new XAttribute("RefBaseClassPath", BaseRoleLib + "/AutomationMLBaseRole")),
                    new XElement("RoleClass", new XAttribute("Name", "DeviceItem"), new XAttribute("RefBaseClassPath", BaseRoleLib + "/AutomationMLBaseRole")),
                    new XElement("RoleClass", new XAttribute("Name", "Rack"), new XAttribute("RefBaseClassPath", RoleLib + "/DeviceItem")),
                    new XElement("RoleClass", new XAttribute("Name", "CommunicationInterface"), new XAttribute("RefBaseClassPath", RoleLib + "/DeviceItem")),
                    new XElement("RoleClass", new XAttribute("Name", "CommunicationPort"), new XAttribute("RefBaseClassPath", RoleLib + "/DeviceItem")),
                    new XElement("RoleClass", new XAttribute("Name", "Node"), new XAttribute("RefBaseClassPath", BaseRoleLib + "/AutomationMLBaseRole")),
                    new XElement("RoleClass", new XAttribute("Name", "Subnet"), new XAttribute("RefBaseClassPath", BaseRoleLib + "/AutomationMLBaseRole"))));
            return caex;
        }

        public static string Serialize(XDocument doc)
        {
            var settings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
            var sb = new StringBuilder();
            using (var writer = System.Xml.XmlWriter.Create(sb, settings)) doc.Save(writer);
            // StringBuilder output declares utf-16; fix the declaration for the UTF-8 file written afterwards.
            return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        }

        public static JsonObject Summary(Result r) => new JsonObject
        {
            ["devices"] = r.Devices, ["deviceItems"] = r.DeviceItems, ["nodes"] = r.Nodes, ["subnets"] = r.Subnets, ["subnetLinks"] = r.Links,
            ["libraryOrigin"] = r.LibraryOrigin, ["importVerified"] = false,
            ["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode)w).ToArray())
        };
    }
}
