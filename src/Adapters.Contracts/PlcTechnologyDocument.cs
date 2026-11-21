using System.IO;
using System.Xml;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcTechnologyDocument
    {
        public const string ObjectType = "SW.TechnologicalObjects.TechnologicalInstanceDB";

        public static XmlReader OpenReader(Stream input) =>
            XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        internal static string ReadName(Stream stream, string parameter)
        {
            try
            {
                using var reader = OpenReader(stream);
                while (reader.Read())
                    if (reader.Depth == 1 && reader.IsStartElement(ObjectType, "")) return ReadName(reader, parameter);
            }
            catch (XmlException error) { throw new AdapterPreconditionException("Invalid technology object XML.", parameter, true, error); }
            throw MissingName(parameter);
        }

        public static string ReadName(XmlReader technologyObject, string parameter)
        {
            using var reader = technologyObject.ReadSubtree();
            string? name = null;
            bool attributes = false;
            while (reader.Read())
            {
                if (reader.Depth == 1) attributes = reader.IsStartElement("AttributeList", "");
                if (attributes && reader.Depth == 2 && reader.IsStartElement("Name", ""))
                {
                    if (name != null) throw MissingName(parameter);
                    name = reader.ReadString();
                }
            }
            return string.IsNullOrWhiteSpace(name) ? throw MissingName(parameter) : name!;
        }

        private static AdapterPreconditionException MissingName(string parameter)
            => new AdapterPreconditionException("The technology object must contain one nonempty name.", parameter);
    }
}
