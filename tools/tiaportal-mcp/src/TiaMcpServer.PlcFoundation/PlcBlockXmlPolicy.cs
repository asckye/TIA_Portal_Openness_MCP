using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcBlockXmlCapability
    {
        internal string Content="native-xml";
        internal bool? FullProgramRestoreSupported;
        internal bool ImportAllowed=true;
        internal string[] Warnings=new string[0];
        internal void Apply(PlcMutationResult result)
        { result.XmlContent=Content; result.FullProgramRestoreSupported=FullProgramRestoreSupported; result.Warnings=Warnings; }
        internal void RequireImport()
        { if(!ImportAllowed) throw new NotSupportedException("Interface-only or unverified SCL XML cannot be imported as a complete program; no source code is reconstructed."); }
    }
    internal static class PlcBlockXmlPolicy
    {
        private static PlcBlockXmlCapability InterfaceOnly() => new PlcBlockXmlCapability {
            Content="interface-only",FullProgramRestoreSupported=false,ImportAllowed=false,
            Warnings=new[]{"V14 SP1 SCL XML contains the block interface only, not the SCL implementation. It is not a complete program backup and cannot restore or replace the complete program."} };
        internal static PlcBlockXmlCapability Export(string release,string language) =>
            release=="14sp1" && string.Equals(language,"SCL",StringComparison.OrdinalIgnoreCase) ? InterfaceOnly() : new PlcBlockXmlCapability();
        internal static PlcBlockXmlCapability Import(string release,string file)
        {
            var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024 };
            XDocument document;
            using(var reader=XmlReader.Create(file,settings)) document=XDocument.Load(reader);
            var languages=document.Descendants().Where(x=>x.Name.LocalName=="ProgrammingLanguage").Select(x=>x.Value.Trim()).ToArray();
            bool scl=languages.Any(x=>string.Equals(x,"SCL",StringComparison.OrdinalIgnoreCase));
            var versions=document.Descendants().Where(x=>x.Name.LocalName=="Engineering").Attributes().Where(x=>string.Equals(x.Name.LocalName,"version",StringComparison.OrdinalIgnoreCase)).Select(x=>x.Value.Trim()).ToArray();
            // Treat any V14 producer conservatively: original V14 is not substituted
            // for SP1 and no XML version marker is rewritten to make it importable.
            bool v14=versions.Any(x=>x.StartsWith("V14",StringComparison.OrdinalIgnoreCase));
            if(scl && (release=="14sp1" || v14)) return InterfaceOnly();
            bool knownProducer=versions.Length>0 && versions.All(v=>new[]{"V15.1","V16","V17","V18","V19","V20","V21"}.Any(k=>string.Equals(v,k,StringComparison.OrdinalIgnoreCase) || v.StartsWith(k+".",StringComparison.OrdinalIgnoreCase)));
            if((scl && !knownProducer) || (release=="14sp1" && languages.Length==0))
                return new PlcBlockXmlCapability { Content="unverified",FullProgramRestoreSupported=false,ImportAllowed=false,Warnings=new[]{"SCL implementation completeness cannot be established from this XML; full program import is blocked."} };
            return new PlcBlockXmlCapability();
        }
    }
}
