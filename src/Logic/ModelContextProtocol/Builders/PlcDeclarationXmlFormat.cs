using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>Format choices for newly generated flat UDT/GlobalDB declarations, not an XML converter.</summary>
    public sealed class PlcDeclarationXmlFormat
    {
        public static IReadOnlyList<string> ReleaseKeys { get; } = Array.AsReadOnly(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" });
        public const string Warning = "Candidate declaration XML. Runtime XSD validation and native TIA import have NOT RUN. Not import-ready; the supplied PublicAPI XSD covers interface fragments, not the whole document or CPU/type semantics.";

        public string ReleaseKey { get; }
        public string EngineeringVersion { get; }
        public XNamespace InterfaceNamespace { get; }
        public string InterfaceSchemaFile { get; }
        public bool HasObjectNamespace { get; }

        private PlcDeclarationXmlFormat(string releaseKey, string engineeringVersion, int interfaceVersion, bool hasObjectNamespace)
        {
            ReleaseKey = releaseKey;
            EngineeringVersion = engineeringVersion;
            InterfaceNamespace = "http://www.siemens.com/automation/Openness/SW/Interface/v" + interfaceVersion;
            InterfaceSchemaFile = "SW.InterfaceSections_v" + interfaceVersion + ".xsd";
            HasObjectNamespace = hasObjectNamespace;
        }

        public static PlcDeclarationXmlFormat ForRelease(string releaseKey)
        {
            // These are the interface schemas shipped by each exact PublicAPI release.
            // V17 uses its v4 format subset. Siemens' "Major changes for long-term stability
            // in TIA Portal Openness V18" makes object Namespace mandatory from V18 onward.
            switch (releaseKey)
            {
                case "14sp1": return new PlcDeclarationXmlFormat(releaseKey, "V14 SP1", 2, false);
                case "15.1": return new PlcDeclarationXmlFormat(releaseKey, "V15.1", 3, false);
                case "16": case "17": return new PlcDeclarationXmlFormat(releaseKey, "V" + releaseKey, 4, false);
                case "18": case "19": case "20": case "21": return new PlcDeclarationXmlFormat(releaseKey, "V" + releaseKey, 5, true);
                default: throw new ArgumentException("Unsupported declaration output release: " + releaseKey, nameof(releaseKey));
            }
        }
    }
}
