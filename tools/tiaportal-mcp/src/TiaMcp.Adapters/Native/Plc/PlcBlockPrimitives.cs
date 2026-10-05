#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if STUDIO_VCI_MODERN || TIA_ENGINE_LOCAL_PRIMITIVES
using System.Security;
using Siemens.Engineering.Connection;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW.Blocks.Interface;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.WatchAndForceTables;
#endif

#if TIA_ENGINE_LOCAL_PRIMITIVES
namespace TiaMcpServer.Siemens.LocalPlcBlocks
#else
namespace TiaMcp.Adapters.Native.Plc
#endif
{
    // Raw native operations; callers retain selection, options, guards and thread ownership.
    public static class PlcBlockPrimitives
    {
        public static IEngineeringObject Parent(IEngineeringInstance value) => value.Parent;
        public static string Name(PlcBlock value) => value.Name;
        public static bool IsConsistent(PlcBlock value) => value.IsConsistent;
        public static void Export(PlcBlock value, FileInfo file, ExportOptions options) => value.Export(file, options);
        public static string Name(PlcSoftware value) => value.Name;
        public static string Name(PlcType value) => value.Name;
        public static ProgrammingLanguage Language(PlcBlock value) => value.ProgrammingLanguage;
        public static PlcBlockSystemGroup BlockGroup(PlcSoftware value) => value.BlockGroup;
        public static PlcTypeSystemGroup TypeGroup(PlcSoftware value) => value.TypeGroup;
        public static PlcBlockComposition Blocks(PlcBlockGroup value) => value.Blocks;
        public static PlcBlockUserGroupComposition Groups(PlcBlockGroup value) => value.Groups;
        public static PlcTypeComposition Types(PlcTypeGroup value) => value.Types;
        public static PlcTypeUserGroupComposition Groups(PlcTypeGroup value) => value.Groups;
        public static string Name(PlcBlockUserGroup value) => value.Name;
        public static string Name(PlcTypeUserGroup value) => value.Name;
        public static IList<PlcBlock> Import(PlcBlockComposition value, FileInfo file, ImportOptions options) => value.Import(file, options);
        public static PlcTagTableComposition TagTables(PlcTagTableGroup value) => value.TagTables;
        public static CompilerResult Compile(ICompilable value) => value.Compile();
        public static CompilerResultState State(CompilerResult value) => value.State;
        public static int ErrorCount(CompilerResult value) => value.ErrorCount;
        public static int WarningCount(CompilerResult value) => value.WarningCount;
        public static CompilerResultMessageComposition Messages(CompilerResult value) => value.Messages;
        public static CompilerResultState State(CompilerResultMessage value) => value.State;
        public static CompilerResultMessageComposition Messages(CompilerResultMessage value) => value.Messages;
        public static string Path(CompilerResultMessage value) => value.Path;
        public static string Description(CompilerResultMessage value) => value.Description;

#if STUDIO_VCI_MODERN || TIA_ENGINE_LOCAL_PRIMITIVES
        public static PlcBlockComposition BlocksOrNull(PlcBlockGroup value) => value?.Blocks;
        public static IEngineeringObject Parent(PlcBlock value) => value.Parent;
        public static string Name(PlcTypeGroup value) => value.Name;
        public static Software SoftwareOrNull(SoftwareContainer value) => value?.Software;
        public static IEngineeringObject ParentOrNull(HardwareFeature value) => value?.Parent;
        public static IEngineeringObject ParentOrNull(IEngineeringInstance value) => value == null ? null : Parent(value);
        public static SafetyAdministration SafetyOrNull(DeviceItem value) => value?.GetService<SafetyAdministration>();
        public static bool IsLoggedOn(SafetyAdministration value) => value.IsLoggedOnToSafetyOfflineProgram;
        public static void Login(SafetyAdministration value, SecureString password) => value.LoginToSafetyOfflineProgram(password);
        public static ICompilable Compiler(IEngineeringServiceProvider value) => value.GetService<ICompilable>();
        public static ICompilable Compiler(PlcBlock value) => value.GetService<ICompilable>();
        public static int ErrorCount(CompilerResultMessage value) => value.ErrorCount;
        public static int WarningCount(CompilerResultMessage value) => value.WarningCount;
        public static DateTime DateTime(CompilerResultMessage value) => value.DateTime;
        public static bool AutoNumber(PlcBlock value) => value.AutoNumber;
        public static int Number(PlcBlock value) => value.Number;
        public static void Delete(PlcBlock value) => value.Delete();
        public static void Delete(PlcType value) => value.Delete();
        public static void Delete(PlcBlockUserGroup value) => value.Delete();
        public static PlcTagComposition Tags(PlcTagTable value) => value.Tags;
        public static PlcSystemConstantComposition SystemConstants(PlcTagTable value) => value.SystemConstants;
        public static int Count(PlcBlockComposition value) => value.Count;
        public static int Count(PlcBlockUserGroupComposition value) => value.Count;
        public static int Count(PlcTypeComposition value) => value.Count;
        public static int Count(PlcTypeUserGroupComposition value) => value.Count;
        public static int Count(PlcTagTableComposition value) => value.Count;
        public static int Count(PlcTagTableUserGroupComposition value) => value.Count;
        public static string Name(PlcTagTableUserGroup value) => value.Name;
        public static PlcTagTableUserGroupComposition Groups(PlcTagTableGroup value) => value.Groups;
        public static int Count(PlcWatchTableComposition value) => value.Count;
        public static int Count(PlcForceTableComposition value) => value.Count;
        public static int Count(PlcWatchAndForceTableUserGroupComposition value) => value.Count;
        public static string Name(PlcWatchAndForceTableUserGroup value) => value.Name;
        public static PlcWatchAndForceTableUserGroupComposition Groups(PlcWatchAndForceTableGroup value) => value.Groups;
        public static int Count(PlcExternalSourceComposition value) => value.Count;
        public static int Count(PlcExternalSourceUserGroupComposition value) => value.Count;
        public static string Name(PlcExternalSourceUserGroup value) => value.Name;
        public static PlcExternalSourceUserGroupComposition Groups(PlcExternalSourceGroup value) => value.Groups;
        public static PlcWatchTableComposition WatchTables(PlcWatchAndForceTableGroup value) => value.WatchTables;
        public static PlcForceTableComposition ForceTables(PlcWatchAndForceTableGroup value) => value.ForceTables;
        public static PlcExternalSourceComposition ExternalSources(PlcExternalSourceGroup value) => value.ExternalSources;
        public static PlcBlockUserGroup Create(PlcBlockUserGroupComposition value, string name) => value.Create(name);
        public static PlcTypeUserGroup Create(PlcTypeUserGroupComposition value, string name) => value.Create(name);
        public static DocumentExportResult ExportDocuments(PlcBlock value, DirectoryInfo directory, string name) => value.ExportAsDocuments(directory, name);
        public static DocumentResultState State(DocumentExportResult value) => value.State;
        public static DocumentImportResultForBlocks ImportDocuments(PlcBlockComposition value, DirectoryInfo directory, string name, ImportDocumentOptions options) => value.ImportFromDocuments(directory, name, options);
        public static DocumentResultState State(DocumentImportResult value) => value.State;
        public static CrossReferenceService CrossReferences(IEngineeringServiceProvider value) => value.GetService<CrossReferenceService>();
        public static CrossReferenceResult CrossReferences(CrossReferenceService value, CrossReferenceFilter filter) => value.GetCrossReferences(filter);
        public static PlcBlockProtectionProvider Protection(PlcBlock value) => value.GetService<PlcBlockProtectionProvider>();
        public static bool IsKnowHowProtected(PlcBlock value) => value.IsKnowHowProtected;
        public static IEnumerable<char> InvalidPasswordCharacters(PlcBlockProtectionProvider value) => value.GetInvalidPasswordCharacters();
        public static void Protect(PlcBlockProtectionProvider value, SecureString password) => value.Protect(password);
        public static void Unprotect(PlcBlockProtectionProvider value, SecureString password) => value.Unprotect(password);
        public static PlcBlockInterface Interface(DataBlock value) => value.Interface;
        public static OnlineState? StateOrNull(OnlineProvider value) => value?.State;
        public static InterfaceSnapshot Snapshot(IEngineeringServiceProvider value) => value.GetService<InterfaceSnapshot>();
        public static InterfaceSnapshot SnapshotOrNull(IEngineeringServiceProvider value) => value == null ? null : Snapshot(value);
        public static void Export(InterfaceSnapshot value, FileInfo file, ExportOptions options) => value.Export(file, options);
        public static ConnectionConfiguration Configuration(FingerprintDataProvider value) => value.Configuration;
        public static string Address(ConfigurationAddress value) => value.Address;
        public static OnlineConfigurationDelegate Handler(Action<OnlineConfiguration> handler) => new OnlineConfigurationDelegate(handler);
        public static FingerprintDataResult Fingerprints(FingerprintDataProvider value, ConfigurationAddress address, OnlineConfigurationDelegate handler) => value.GetFingerprintData(address, handler);
        public static FingerprintDataItemComposition Items(FingerprintDataResult value) => value.FingerprintDataItems;
        public static string Identifier(FingerprintDataItem value) => value.FingerprintDataIdentifier;
        public static string Value(FingerprintDataItem value) => value.FingerprintDataValue;
        public static void Password(OnlinePasswordConfiguration value, SecureString password) => value.SetPassword(password);
        public static TlsVerificationConfigurationSelection Selection(TlsVerificationConfiguration value) => value.CurrentSelection;
        public static void Selection(TlsVerificationConfiguration value, TlsVerificationConfigurationSelection selection) => value.CurrentSelection = selection;
        public static string PlcName(TlsVerificationConfiguration value) => value.PlcName;
        public static string VerificationInfo(TlsVerificationConfiguration value) => value.VerificationInfo;
        public static PlcBlock Find(PlcBlockComposition value, string name) => value.Find(name);
#endif
    }
}
