using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class SafeXml
    {
        private const long MaximumCharacters = 64L * 1024 * 1024;

        public static XmlReader Open(Stream input, long maximumCharacters = MaximumCharacters)
        {
            if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters), "A positive XML size limit is required.");
            return XmlReader.Create(input, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumCharacters });
        }

        public static XDocument Load(string path)
        {
            using var input = File.OpenRead(path);
            using var reader = Open(input);
            return XDocument.Load(reader);
        }
    }
}
