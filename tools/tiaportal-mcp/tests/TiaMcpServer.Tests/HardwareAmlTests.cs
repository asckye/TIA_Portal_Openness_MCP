using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// Offline AML generation: spec parsing, CAEX structure (roles last, attributes, subnet links) and reference-file mode.
    /// </summary>
    internal static class HardwareAmlTests
    {
        private const string Spec = @"{""projectName"":""P1"",""devices"":[{""name"":""PLC_1"",""typeIdentifier"":""System:Device.S71500"",""deviceItems"":[
  {""name"":""Rack_0"",""typeIdentifier"":""OrderNumber:6ES7 590-1AE80-0AA0"",""deviceItems"":[
    {""name"":""PLC_1"",""typeIdentifier"":""OrderNumber:6ES7 516-3AN02-0AB0/V2.9"",""positionNumber"":1,""firmwareVersion"":""V2.9"",""deviceItems"":[
      {""name"":""PROFINET interface_1"",""role"":""CommunicationInterface"",""builtIn"":true,""nodes"":[{""name"":""E1"",""networkAddress"":""192.168.0.1"",""subnetMask"":""255.255.255.0"",""subnetName"":""PN/IE_1""}]}]},
    {""name"":""DI 32x24VDC HF_1"",""typeIdentifier"":""OrderNumber:6ES7 521-1BL00-0AB0/V2.0"",""positionNumber"":2,""attributes"":{""Comment"":""inputs""}}]}]}]}";

        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var dir = Path.Combine(Path.GetTempPath(), "tia-aml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var spec = HardwareAmlLogic.ParseSpec(Spec);
                check(spec.Devices.Count == 1 && spec.Devices[0].Items.Count == 1 && spec.Devices[0].Items[0].Role == "Rack" && spec.Devices[0].Items[0].PositionNumber == 0, "rack inferred at depth 0 with position 0");
                check(spec.Subnets.Count == 1 && spec.Subnets[0].Name == "PN/IE_1", "subnet referenced by a node is created implicitly");
                check(HardwareAmlLogic.AllItems(spec.Devices[0]).Count() == 4, "all device items enumerated");
                check(Fails(() => HardwareAmlLogic.ParseSpec("{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"name\":\"M\"}]}]}")), "[sentinel] module without typeIdentifier refused");
                check(Fails(() => HardwareAmlLogic.ParseSpec("{\"devices\":[]}")), "[sentinel] no devices refused");
                check(Fails(() => HardwareAmlLogic.ParseSpec("{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"name\":\"M\",\"role\":\"Module\",\"typeIdentifier\":\"y\"}]}]}")), "[sentinel] unknown role refused");
                check(Fails(() => HardwareAmlLogic.ParseSpec("{\"devices\":[{\"name\":\"D\",\"typeIdentifier\":\"x\",\"deviceItems\":[{\"name\":\"I\",\"role\":\"CommunicationInterface\",\"nodes\":[{\"networkAddress\":\"300.1.1.1\"}]}]}]}")), "[sentinel] invalid IP refused");

                var built = HardwareAmlLogic.Build(spec, null, "test.aml", "2.7.19.0");
                check(built.Devices == 1 && built.DeviceItems == 4 && built.Nodes == 1 && built.Subnets == 1 && built.Links == 1, "counts: " + built.DeviceItems + " items, " + built.Links + " links");
                check(built.LibraryOrigin.StartsWith("builtin", StringComparison.Ordinal) && built.Warnings.Count == 1, "builtin mode is flagged");
                var text = HardwareAmlLogic.Serialize(built.Document);
                check(text.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", StringComparison.Ordinal), "UTF-8 declaration");
                var doc = XDocument.Parse(text);
                var root = doc.Root!;
                check(root.Name.LocalName == "CAEXFile" && (string?)root.Attribute("SchemaVersion") == "2.15" && (string?)root.Attribute("FileName") == "test.aml", "CAEX 2.15 root");
                var order = root.Elements().Select(e => e.Name.LocalName).ToList();
                check(order.IndexOf("InstanceHierarchy") > order.LastIndexOf("AdditionalInformation") && order.IndexOf("InstanceHierarchy") < order.IndexOf("InterfaceClassLib") && order.IndexOf("InterfaceClassLib") < order.IndexOf("RoleClassLib"), "CAEX top-level order: " + string.Join(",", order));
                var project = root.Element("InstanceHierarchy")!.Element("InternalElement")!;
                check((string?)project.Attribute("Name") == "P1" && project.Elements().Last().Name.LocalName == "SupportedRoleClass" && (string?)project.Elements().Last().Attribute("RefRoleClassPath") == HardwareAmlLogic.RoleLib + "/AutomationProject", "project role is the last child");
                var device = project.Elements("InternalElement").First(e => (string?)e.Attribute("Name") == "PLC_1");
                check(device.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "TypeIdentifier" && a.Element("Value")?.Value == "System:Device.S71500"), "device TypeIdentifier attribute");
                var rack = device.Element("InternalElement")!;
                check(rack.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "PositionNumber" && a.Element("Value")?.Value == "0" && (string?)a.Attribute("AttributeDataType") == "xs:int"), "rack PositionNumber xs:int 0");
                check(rack.Elements().Last().Name.LocalName == "SupportedRoleClass" && ((string?)rack.Elements().Last().Attribute("RefRoleClassPath"))!.EndsWith("/Rack"), "rack role last");
                var cpu = rack.Elements("InternalElement").First();
                check(cpu.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "FirmwareVersion" && a.Element("Value")?.Value == "V2.9"), "firmware attribute");
                var iface = cpu.Element("InternalElement")!;
                check(iface.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "BuiltIn" && a.Element("Value")?.Value == "true") && ((string?)iface.Elements().Last().Attribute("RefRoleClassPath"))!.EndsWith("/CommunicationInterface"), "interface builtIn + role");
                var node = iface.Element("InternalElement")!;
                check(node.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "NetworkAddress" && a.Element("Value")?.Value == "192.168.0.1") && node.Element("ExternalInterface") != null, "node address + logical end point");
                var subnet = project.Elements("InternalElement").First(e => (string?)e.Attribute("Name") == "PN/IE_1");
                var link = project.Element("InternalLink")!;
                check((string?)link.Attribute("RefPartnerSideA") == (string?)node.Element("ExternalInterface")!.Attribute("ID") && (string?)link.Attribute("RefPartnerSideB") == (string?)subnet.Element("ExternalInterface")!.Attribute("ID"), "internal link joins node and subnet interfaces");
                var di = rack.Elements("InternalElement").Last();
                check(di.Elements("Attribute").Any(a => (string?)a.Attribute("Name") == "Comment" && a.Element("Value")?.Value == "inputs"), "free attributes emitted");
                check(root.Elements("RoleClassLib").Any(l => (string?)l.Attribute("Name") == HardwareAmlLogic.RoleLib && l.Elements("RoleClass").Count() == 8), "builtin role class library declares the 8 roles");
                var ids = doc.Descendants().Attributes("ID").Select(a => a.Value).ToList();
                check(ids.Count == ids.Distinct().Count() && ids.All(id => Guid.TryParse(id, out _)), "all IDs are unique GUIDs");

                // ---- reference mode: header + libraries copied, old instance hierarchy replaced
                var reference = Path.Combine(dir, "ref.aml");
                File.WriteAllText(reference, "<?xml version=\"1.0\" encoding=\"utf-8\"?><CAEXFile FileName=\"ref.aml\" SchemaVersion=\"2.15\"><AdditionalInformation AutomationMLVersion=\"2.0\" /><AdditionalInformation><WriterHeader><WriterName>TIA</WriterName><WriterVersion>Engineering V21</WriterVersion></WriterHeader></AdditionalInformation><InstanceHierarchy Name=\"Old\"><InternalElement Name=\"OldProject\" ID=\"1\" /></InstanceHierarchy><RoleClassLib Name=\"" + HardwareAmlLogic.RoleLib + "\"><Version>2.0.0</Version><RoleClass Name=\"Device\" /></RoleClassLib></CAEXFile>", new UTF8Encoding(false));
                var fromRef = HardwareAmlLogic.Build(spec, reference, "out.aml", "2.7.19.0");
                var refDoc = fromRef.Document.Root!;
                check(fromRef.LibraryOrigin.StartsWith("reference:", StringComparison.Ordinal) && fromRef.Warnings.Count == 0, "reference mode without warnings");
                check(refDoc.Descendants("WriterVersion").First().Value == "Engineering V21", "writer header copied from reference");
                check(refDoc.Elements("InstanceHierarchy").Count() == 1 && (string?)refDoc.Element("InstanceHierarchy")!.Attribute("Name") == "Old" && refDoc.Descendants("InternalElement").All(e => (string?)e.Attribute("Name") != "OldProject"), "old instance hierarchy replaced, its name kept");
                check(refDoc.Element("RoleClassLib")!.Element("Version")!.Value == "2.0.0" && (string?)refDoc.Attribute("FileName") == "out.aml", "reference libraries kept, file name updated");
                check(Fails(() => HardwareAmlLogic.Build(spec, Path.Combine(dir, "missing.aml"), "x.aml", "1")), "[sentinel] missing reference refused");
                File.WriteAllText(Path.Combine(dir, "notaml.xml"), "<Document/>", new UTF8Encoding(false));
                check(Fails(() => HardwareAmlLogic.Build(spec, Path.Combine(dir, "notaml.xml"), "x.aml", "1")), "[sentinel] non-CAEX reference refused");
                check(HardwareAmlLogic.Summary(built)["importVerified"]!.GetValue<bool>() == false, "summary always says importVerified=false");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
