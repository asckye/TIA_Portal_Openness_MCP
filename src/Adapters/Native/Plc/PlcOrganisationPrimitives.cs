using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
#if PLC_SOFTWARE_PROTECTION
using Siemens.Engineering.SW.WatchAndForceTables;
#endif
#if PLC_SOFTWARE_CROSS_REFERENCES
using Siemens.Engineering.CrossReference;
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcOrganisationPrimitives
    {
        internal static Software? SoftwareOrNull(SoftwareContainer? value) => PlcBlockPrimitives.SoftwareOrNull(value);
        internal static string Name(PlcBlock value) => PlcBlockPrimitives.Name(value);
        internal static string Name(PlcSoftware value) => PlcBlockPrimitives.Name(value);
        internal static string Name(PlcType value) => PlcBlockPrimitives.Name(value);
        #if STUDIO_VCI_MODERN
        internal static string Name(PlcTypeGroup value)  => PlcBlockPrimitives.Name(value);
#else
        internal static string Name(PlcTypeGroup value) => value.Name;
#endif
        #if STUDIO_VCI_MODERN
        internal static string Name(PlcBlockUserGroup value)  => PlcBlockPrimitives.Name(value);
#else
        internal static string Name(PlcBlockUserGroup value) => value.Name;
#endif
        #if STUDIO_VCI_MODERN
        internal static string Name(PlcTagTableUserGroup value)  => PlcBlockPrimitives.Name(value);
#else
        internal static string Name(PlcTagTableUserGroup value) => value.Name;
#endif
        #if STUDIO_VCI_MODERN
        internal static string Name(PlcExternalSourceUserGroup value)  => PlcBlockPrimitives.Name(value);
#else
        internal static string Name(PlcExternalSourceUserGroup value) => value.Name;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Number(PlcBlock value)  => PlcBlockPrimitives.Number(value);
#else
        internal static int Number(PlcBlock value) => value.Number;
#endif
        #if STUDIO_VCI_MODERN
        internal static bool AutoNumber(PlcBlock value)  => PlcBlockPrimitives.AutoNumber(value);
#else
        internal static bool AutoNumber(PlcBlock value) => value.AutoNumber;
#endif
        internal static ProgrammingLanguage Language(PlcBlock value) => PlcBlockPrimitives.Language(value);
        #if STUDIO_VCI_MODERN
        internal static IEngineeringObject Parent(PlcBlock value)  => PlcBlockPrimitives.Parent(value);
#else
        internal static IEngineeringObject Parent(PlcBlock value) => value.Parent;
#endif
        internal static PlcBlockSystemGroup BlockGroup(PlcSoftware value) => PlcBlockPrimitives.BlockGroup(value);
        internal static PlcTypeSystemGroup TypeGroup(PlcSoftware value) => PlcBlockPrimitives.TypeGroup(value);
        internal static PlcBlockComposition Blocks(PlcBlockGroup value) => PlcBlockPrimitives.Blocks(value);
        #if STUDIO_VCI_MODERN
        internal static PlcBlockComposition? BlocksOrNull(PlcBlockGroup? value)  => PlcBlockPrimitives.BlocksOrNull(value);
#else
        internal static PlcBlockComposition? BlocksOrNull(PlcBlockGroup? value) => value?.Blocks;
#endif
        internal static PlcBlockUserGroupComposition Groups(PlcBlockGroup value) => PlcBlockPrimitives.Groups(value);
        internal static PlcTypeUserGroupComposition Groups(PlcTypeGroup value) => PlcBlockPrimitives.Groups(value);
        #if STUDIO_VCI_MODERN
        internal static PlcTagTableUserGroupComposition Groups(PlcTagTableGroup value)  => PlcBlockPrimitives.Groups(value);
#else
        internal static PlcTagTableUserGroupComposition Groups(PlcTagTableGroup value) => value.Groups;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcExternalSourceUserGroupComposition Groups(PlcExternalSourceGroup value)  => PlcBlockPrimitives.Groups(value);
#else
        internal static PlcExternalSourceUserGroupComposition Groups(PlcExternalSourceGroup value) => value.Groups;
#endif
        internal static PlcTypeComposition Types(PlcTypeGroup value) => PlcBlockPrimitives.Types(value);
        internal static PlcTagTableComposition TagTables(PlcTagTableGroup value) => PlcBlockPrimitives.TagTables(value);
        internal static PlcTagTableSystemGroup TagTableGroup(object unit)
        {
#if STUDIO_VCI_MODERN
            return PlcDocumentPrimitives.TagTableGroup((Siemens.Engineering.SW.Units.PlcUnitBase)unit);
#else
            return (PlcTagTableSystemGroup)PlcGroupOperations.Get(unit, "TagTableGroup");
#endif
        }
        #if STUDIO_VCI_MODERN
        internal static PlcExternalSourceComposition ExternalSources(PlcExternalSourceGroup value)  => PlcBlockPrimitives.ExternalSources(value);
#else
        internal static PlcExternalSourceComposition ExternalSources(PlcExternalSourceGroup value) => value.ExternalSources;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcTagComposition Tags(PlcTagTable value)  => PlcBlockPrimitives.Tags(value);
#else
        internal static PlcTagComposition Tags(PlcTagTable value) => value.Tags;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcSystemConstantComposition SystemConstants(PlcTagTable value)  => PlcBlockPrimitives.SystemConstants(value);
#else
        internal static PlcSystemConstantComposition SystemConstants(PlcTagTable value) => value.SystemConstants;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcBlockComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcBlockComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcBlockUserGroupComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcBlockUserGroupComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcTypeComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcTypeComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcTypeUserGroupComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcTypeUserGroupComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcTagTableComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcTagTableComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcTagTableUserGroupComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcTagTableUserGroupComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcExternalSourceComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcExternalSourceComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcExternalSourceUserGroupComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcExternalSourceUserGroupComposition value) => value.Count;
#endif
#if STUDIO_VCI_MODERN
        internal static PlcBlockUserGroup Create(PlcBlockUserGroupComposition value, string name) => PlcBlockPrimitives.Create(value, name);
#else
        internal static PlcBlockUserGroup Create(PlcBlockUserGroupComposition value, string name) => value.Create(name);
#endif
#if STUDIO_VCI_MODERN
        internal static PlcTypeUserGroup Create(PlcTypeUserGroupComposition value, string name) => PlcBlockPrimitives.Create(value, name);
#else
        internal static PlcTypeUserGroup Create(PlcTypeUserGroupComposition value, string name) => value.Create(name);
#endif
        #if STUDIO_VCI_MODERN
        internal static void Delete(PlcBlock value)  => PlcBlockPrimitives.Delete(value);
#else
        internal static void Delete(PlcBlock value) => value.Delete();
#endif
        #if STUDIO_VCI_MODERN
        internal static void Delete(PlcType value)  => PlcBlockPrimitives.Delete(value);
#else
        internal static void Delete(PlcType value) => value.Delete();
#endif
        #if STUDIO_VCI_MODERN
        internal static void Delete(PlcBlockUserGroup value)  => PlcBlockPrimitives.Delete(value);
#else
        internal static void Delete(PlcBlockUserGroup value) => value.Delete();
#endif
        #if STUDIO_VCI_MODERN
        internal static OnlineState? StateOrNull(OnlineProvider? value)  => PlcBlockPrimitives.StateOrNull(value);
#else
        internal static OnlineState? StateOrNull(OnlineProvider? value) => value?.State;
#endif
        internal static void Export(PlcBlock value, FileInfo file, ExportOptions options) => PlcBlockPrimitives.Export(value, file, options);
        internal static IList<PlcBlock> Import(PlcBlockComposition value, FileInfo file, ImportOptions options) => PlcBlockPrimitives.Import(value, file, options);
#if PLC_DOCUMENT_EXPORT
        internal static DocumentExportResult ExportDocuments(PlcBlock value, DirectoryInfo directory, string name) => PlcDocumentPrimitives.Export(value, directory, name);
        internal static DocumentImportResultForBlocks ImportDocuments(PlcBlockComposition value, DirectoryInfo directory, string name, ImportDocumentOptions options) => PlcDocumentPrimitives.Import(value, directory, name, options);
        internal static DocumentResultState State(DocumentExportResult value) => PlcDocumentPrimitives.State(value);
        internal static DocumentResultState State(DocumentImportResult value) => PlcDocumentPrimitives.State(value);
#endif
#if PLC_SOFTWARE_PROTECTION
        #if STUDIO_VCI_MODERN
        internal static PlcBlockProtectionProvider? Protection(PlcBlock value)  => PlcBlockPrimitives.Protection(value);
#else
        internal static PlcBlockProtectionProvider? Protection(PlcBlock value) => value.GetService<PlcBlockProtectionProvider>();
#endif
        #if STUDIO_VCI_MODERN
        internal static bool IsKnowHowProtected(PlcBlock value)  => PlcBlockPrimitives.IsKnowHowProtected(value);
#else
        internal static bool IsKnowHowProtected(PlcBlock value) => value.IsKnowHowProtected;
#endif
        #if STUDIO_VCI_MODERN
        internal static IEnumerable<char> InvalidPasswordCharacters(PlcBlockProtectionProvider value)  => PlcBlockPrimitives.InvalidPasswordCharacters(value);
#else
        internal static IEnumerable<char> InvalidPasswordCharacters(PlcBlockProtectionProvider value) => value.GetInvalidPasswordCharacters();
#endif
#if STUDIO_VCI_MODERN
        internal static void Protect(PlcBlockProtectionProvider value, System.Security.SecureString password) => PlcBlockPrimitives.Protect(value, password);
#else
        internal static void Protect(PlcBlockProtectionProvider value, System.Security.SecureString password) => value.Protect(password);
#endif
#if STUDIO_VCI_MODERN
        internal static void Unprotect(PlcBlockProtectionProvider value, System.Security.SecureString password) => PlcBlockPrimitives.Unprotect(value, password);
#else
        internal static void Unprotect(PlcBlockProtectionProvider value, System.Security.SecureString password) => value.Unprotect(password);
#endif
        #if STUDIO_VCI_MODERN
        internal static string Name(PlcWatchAndForceTableUserGroup value)  => PlcBlockPrimitives.Name(value);
#else
        internal static string Name(PlcWatchAndForceTableUserGroup value) => value.Name;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcWatchAndForceTableUserGroupComposition Groups(PlcWatchAndForceTableGroup value)  => PlcBlockPrimitives.Groups(value);
#else
        internal static PlcWatchAndForceTableUserGroupComposition Groups(PlcWatchAndForceTableGroup value) => value.Groups;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcWatchTableComposition WatchTables(PlcWatchAndForceTableGroup value)  => PlcBlockPrimitives.WatchTables(value);
#else
        internal static PlcWatchTableComposition WatchTables(PlcWatchAndForceTableGroup value) => value.WatchTables;
#endif
        #if STUDIO_VCI_MODERN
        internal static PlcForceTableComposition ForceTables(PlcWatchAndForceTableGroup value)  => PlcBlockPrimitives.ForceTables(value);
#else
        internal static PlcForceTableComposition ForceTables(PlcWatchAndForceTableGroup value) => value.ForceTables;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcWatchTableComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcWatchTableComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcForceTableComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcForceTableComposition value) => value.Count;
#endif
        #if STUDIO_VCI_MODERN
        internal static int Count(PlcWatchAndForceTableUserGroupComposition value)  => PlcBlockPrimitives.Count(value);
#else
        internal static int Count(PlcWatchAndForceTableUserGroupComposition value) => value.Count;
#endif
#endif
#if PLC_SOFTWARE_CROSS_REFERENCES
        #if STUDIO_VCI_MODERN
        internal static CrossReferenceService? CrossReferences(IEngineeringServiceProvider value)  => PlcBlockPrimitives.CrossReferences(value);
#else
        internal static CrossReferenceService? CrossReferences(IEngineeringServiceProvider value) => value.GetService<CrossReferenceService>();
#endif
#if STUDIO_VCI_MODERN
        internal static CrossReferenceResult CrossReferences(CrossReferenceService value, CrossReferenceFilter filter) => PlcBlockPrimitives.CrossReferences(value, filter);
#else
        internal static CrossReferenceResult CrossReferences(CrossReferenceService value, CrossReferenceFilter filter) => value.GetCrossReferences(filter);
#endif
#endif
    }
}
