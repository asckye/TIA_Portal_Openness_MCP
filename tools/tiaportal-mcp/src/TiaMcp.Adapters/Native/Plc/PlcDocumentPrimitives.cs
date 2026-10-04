#nullable disable
#if TIA_ENGINE_LOCAL_PRIMITIVES
#define PLC_DOCUMENT_EXPORT
#define PLC_SOURCE_RESULTS
namespace TiaMcpServer.Siemens.LocalDocuments
#else
namespace TiaMcp.Adapters.Native.Plc
#endif
{
    using System.Collections.Generic;
    using System.IO;
    using global::Siemens.Engineering;
    using global::Siemens.Engineering.HW;
    using global::Siemens.Engineering.HW.Features;
    using global::Siemens.Engineering.SW;
    using global::Siemens.Engineering.SW.Blocks;
    using global::Siemens.Engineering.SW.ExternalSources;
    using global::Siemens.Engineering.SW.Types;
#if PLC_DOCUMENT_EXPORT
    using global::Siemens.Engineering.Library.MasterCopies;
    using global::Siemens.Engineering.SW.Units;
#endif

    // Raw document and external-source operations. Hosts retain validation, options,
    // result handling, traversal, retries and the ownership of native handles/threads.
    public static class PlcDocumentPrimitives
    {
        public static PlcBlockSystemGroup BlockGroup(PlcSoftware software) => software.BlockGroup;
        public static PlcTypeSystemGroup TypeGroup(PlcSoftware software) => software.TypeGroup;
        public static PlcBlockComposition Blocks(PlcBlockGroup group) => group.Blocks;
        public static string Name(PlcBlock block) => block.Name;
        public static string Name(PlcType type) => type.Name;
        public static bool IsConsistent(PlcBlock block) => block.IsConsistent;
        public static ProgrammingLanguage Language(PlcBlock block) => block.ProgrammingLanguage;

#if PLC_DOCUMENT_EXPORT
        public static Software Software(SoftwareContainer container) => container?.Software;
        public static PlcBlockUserGroupComposition BlockGroups(PlcBlockGroup group) => group.Groups;
        public static PlcBlock FindBlock(this PlcBlockComposition blocks, string name) => blocks.Find(name);
        public static string Name(PlcBlockUserGroup group) => group?.Name;
        public static string Name(PlcTypeUserGroup group) => group?.Name;
        public static int Number(PlcBlock block) => block.Number;
        public static void SetNumber(PlcBlock block, int number) => block.Number = number;
        public static bool AutoNumber(PlcBlock block) => block.AutoNumber;
        public static void SetAutoNumber(PlcBlock block, bool autoNumber) => block.AutoNumber = autoNumber;
        public static IEngineeringObject Parent(PlcBlock block) => block.Parent;
        public static int Count(PlcBlockComposition blocks) => blocks.Count;
        public static int Count(PlcTypeComposition types) => types.Count;
        public static object Attribute(IEngineeringObject value, string name) => value.GetAttribute(name);
#endif

        public static PlcExternalSourceSystemGroup ExternalSourceGroup(PlcSoftware software) => software.ExternalSourceGroup;
        public static PlcExternalSourceComposition Sources(PlcExternalSourceGroup group) => group.ExternalSources;
        public static string Name(PlcExternalSource source) => source.Name;
        public static PlcExternalSource Find(PlcExternalSourceComposition sources, string name) => sources.Find(name);
        public static PlcExternalSource CreateFromFile(PlcExternalSourceComposition sources, string name, string path) => sources.CreateFromFile(name, path);
        public static void Delete(PlcExternalSource source) => source.Delete();
#if !PLC_SOURCE_RESULTS || PLC_DOCUMENT_EXPORT
        public static void Generate(PlcExternalSource source) => source.GenerateBlocksFromSource();
#endif
#if PLC_SOURCE_RESULTS
        public static IList<IEngineeringObject> Generate(PlcExternalSource source, GenerateBlockOption option) => source.GenerateBlocksFromSource(option);
#endif

#if PLC_DOCUMENT_EXPORT
        public static DocumentExportResult ExportOptional(PlcBlockGroup group, string directory, string name) => group?.Blocks.FindBlock(name)?.ExportAsDocuments(new DirectoryInfo(directory), name);
        public static DocumentExportResult Export(PlcBlock block, DirectoryInfo directory, string name) => block.ExportAsDocuments(directory, name);
        public static DocumentImportResultForBlocks Import(PlcBlockComposition blocks, DirectoryInfo directory, string name, ImportDocumentOptions option) => blocks.ImportFromDocuments(directory, name, option);
        public static DocumentResultState State(DocumentExportResult result) => result.State;
        public static DocumentResultState State(DocumentImportResult result) => result.State;
        public static DocumentResultState? OptionalState(DocumentImportResult result) => result?.State;
        public static PlcBlockAssociation ImportedBlocks(DocumentImportResultForBlocks result) => result.ImportedPlcBlocks;
        public static PlcBlockAssociation OptionalImportedBlocks(DocumentImportResultForBlocks result) => result?.ImportedPlcBlocks;
        public static DocumentResultMessageComposition Messages(DocumentImportResult result) => result?.Messages;
        public static PlcBlockSystemGroup BlockGroup(PlcUnitBase unit) => unit.BlockGroup;
        public static PlcTypeSystemGroup TypeGroup(PlcUnitBase unit) => unit.TypeGroup;
        public static string Name(PlcUnitBase unit) => unit?.Name;
        public static PlcExternalSourceSystemGroup ExternalSourceGroup(PlcUnitBase unit) => unit.ExternalSourceGroup;
        public static PlcExternalSourceUserGroupComposition SourceGroups(PlcExternalSourceGroup group) => group.Groups;
        public static string Name(PlcExternalSourceGroup group) => group.Name;
        public static string Name(PlcExternalSourceUserGroup group) => group.Name;
        public static PlcExternalSourceUserGroup Find(PlcExternalSourceUserGroupComposition groups, string name) => groups.Find(name);
        public static PlcExternalSourceUserGroup Create(PlcExternalSourceUserGroupComposition groups, string name) => groups.Create(name);
        public static void Delete(PlcExternalSourceUserGroup group) => group.Delete();
#if PLC_EXTERNAL_SOURCE_GROUP_RENAME
        public static void SetName(PlcExternalSourceUserGroup group, string name) => group.Name = name;
#endif
        public static PlcExternalSource CreateFrom(PlcExternalSourceComposition sources, MasterCopy copy) => sources.CreateFrom(copy);
        public static PlcExternalSource CreateFrom(PlcExternalSourceComposition sources, MasterCopy copy, MasterCopyMode mode) => sources.CreateFrom(copy, mode);
        public static IList<IEngineeringObject> Generate(PlcExternalSource source, PlcBlockUserGroup target, GenerateBlockOption option) => source.GenerateBlocksFromSource(target, option);
        public static IList<IEngineeringObject> Generate(PlcExternalSource source, PlcTypeUserGroup target, GenerateBlockOption option) => source.GenerateBlocksFromSource(target, option);
        public static PlcBlockComposition Blocks(PlcSystemBlockGroup group) => group.Blocks;
        public static PlcSystemBlockGroupComposition SystemBlockGroups(PlcBlockSystemGroup group) => group.SystemBlockGroups;
        public static PlcSystemBlockGroupComposition SystemBlockGroups(PlcSystemBlockGroup group) => group.Groups;
        public static PlcSystemTypeGroupComposition SystemTypeGroups(PlcTypeSystemGroup group) => group.SystemTypeGroups;
        public static PlcTypeComposition Types(PlcSystemTypeGroup group) => group.Types;
        public static string Name(PlcSystemBlockGroup group) => group.Name;
        public static string Name(PlcSystemTypeGroup group) => group.Name;
        public static int Count(PlcSystemBlockGroupComposition groups) => groups.Count;

        public static ImportedBlockSequence Enumerate(PlcBlockAssociation source) => new ImportedBlockSequence(source);
        public readonly struct ImportedBlockSequence
        {
            private readonly PlcBlockAssociation source;
            internal ImportedBlockSequence(PlcBlockAssociation source) { this.source = source; }
            public IEnumerator<PlcBlock> GetEnumerator() => source.GetEnumerator();
        }
        // Preserve typed composition enumeration without moving materialization into this layer.
        public static BlockGroupSequence Enumerate(PlcBlockUserGroupComposition source) => new BlockGroupSequence(source);
        public readonly struct BlockGroupSequence
        {
            private readonly PlcBlockUserGroupComposition source;
            internal BlockGroupSequence(PlcBlockUserGroupComposition source) { this.source = source; }
            public IEnumerator<PlcBlockUserGroup> GetEnumerator() => source.GetEnumerator();
        }
#endif
    }
}
