using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Adapters
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
        {
            if(!ImportAllowed)
                throw new NotSupportedException("Interface-only or unverified SCL XML cannot be imported as a complete program; no source code is " +
                "reconstructed.");
        }
    }
    internal static class PlcBlockXmlPolicy
    {
        private static PlcBlockXmlCapability InterfaceOnly() => new PlcBlockXmlCapability {
            Content="interface-only",FullProgramRestoreSupported=false,ImportAllowed=false,
            Warnings=new[]{
                "V14 SP1 SCL XML contains the block interface only, not the SCL implementation. It is not a complete program backup and cannot restore or " +
                    "replace the complete program."
            } };
        internal static PlcBlockXmlCapability Export(string release,string language) =>
            release=="14sp1" && string.Equals(language,"SCL",StringComparison.OrdinalIgnoreCase) ? InterfaceOnly() : new PlcBlockXmlCapability();
        // API audit 2026-10-19 F5: a know-how-protected block exports only its public interface ("Exporting blocks with know-how
        // protection") and cannot be imported again ("Basic principles of importing/exporting").
        internal static PlcBlockXmlCapability Export(string release,string language,bool knowHowProtected) =>
            knowHowProtected ? new PlcBlockXmlCapability {
                Content="interface-only",FullProgramRestoreSupported=false,ImportAllowed=false,
                Warnings=new[]{
                    "The block is know-how protected: its XML holds only the public interface, not the protected code. It is not a backup and cannot be " +
                        "imported again."
                } }
            : Export(release,language);
        internal static PlcBlockXmlCapability Import(string release,string file)
        {
            var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024 };
            XDocument document;
            using(var reader=XmlReader.Create(file,settings)) document=XDocument.Load(reader);
            var content = Contracts.SimaticMlImportWindow.BlockContent(release, document);
            if (content == "interface-only") return InterfaceOnly();
            if (content == "unverified")
                return new PlcBlockXmlCapability {
                    Content="unverified",FullProgramRestoreSupported=false,ImportAllowed=false,Warnings=new[]{
                        "SCL implementation completeness cannot be established from this XML; full program import is blocked."}
                };
            return new PlcBlockXmlCapability();
        }
    }
}
