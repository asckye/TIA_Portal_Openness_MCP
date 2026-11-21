using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Contracts
{
    internal static class PlcOpcUaImportDocument
    {
        internal static string InterfaceName(FileInfo file)
        {
            XNamespace ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
            using var stream = file.OpenRead();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root;
            if (root?.Name == ua + "UANodeSet")
            {
                var models = root.Elements(ua + "UAObject").Where(node => node.Elements(ua + "References").Elements(ua + "Reference")
                    .Any(link => (string?)link.Attribute("ReferenceType") == "Organizes" && (string?)link.Attribute("IsForward") == "false"
                        && link.Value == "ns=1;s=UserModelledInterfaces")).ToArray();
                if (models.Length > 0)
                {
                    var name = (string?)models[0].Element(ua + "DisplayName");
                    if (models.Length != 1 || string.IsNullOrWhiteSpace(name) || (string?)models[0].Attribute("NodeId") != "ns=2;s=" + name
                        || (string?)models[0].Attribute("BrowseName") != "2:" + name)
                        throw new AdapterPreconditionException("Generated XML must identify one modelled server interface.", "importPath");
                    return name!;
                }
            }
            return Path.GetFileNameWithoutExtension(file.Name);
        }
    }
}
