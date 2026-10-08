#nullable disable
// Unused native shapes let the complete shared primitive source compile without an SDK.
// Operations outside software-tree reads throw; only the real SDK builds establish compatibility.
using System.Security;
namespace Siemens.Engineering
{
    public enum ExportOptions { None }
    public enum ImportOptions { None }
}
namespace Siemens.Engineering.HW
{
    public class HardwareObject : EngineeringObject { }
    public class DeviceItem : HardwareObject { }
    public class Software : EngineeringObject { }
}
namespace Siemens.Engineering.HW.Features
{
    public class HardwareFeature : EngineeringObject { }
    public class SoftwareContainer : HardwareFeature { public Software Software => throw new NotSupportedException(); }
}
namespace Siemens.Engineering.Compiler
{
    public interface ICompilable { CompilerResult Compile(); }
    public enum CompilerResultState { Success, Warning, Error }
    public class CompilerResult
    {
        public CompilerResultState State => throw new NotSupportedException();
        public int ErrorCount => throw new NotSupportedException();
        public int WarningCount => throw new NotSupportedException();
        public CompilerResultMessageComposition Messages => throw new NotSupportedException();
    }
    public class CompilerResultMessage : CompilerResult
    {
        public string Path => throw new NotSupportedException();
        public string Description => throw new NotSupportedException();
        public DateTime DateTime => throw new NotSupportedException();
    }
    public class CompilerResultMessageComposition : List<CompilerResultMessage> { }
}
namespace Siemens.Engineering.Safety
{
    public class SafetyAdministration
    {
        public bool IsLoggedOnToSafetyOfflineProgram => throw new NotSupportedException();
        public void LoginToSafetyOfflineProgram(SecureString password) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public class DataBlock : PlcBlock { public Interface.PlcBlockInterface Interface => throw new NotSupportedException(); }
    public enum DocumentResultState { Success }
    public enum ImportDocumentOptions { None }
    public class DocumentExportResult { public DocumentResultState State => throw new NotSupportedException(); }
    public class DocumentImportResult { public DocumentResultState State => throw new NotSupportedException(); }
    public class DocumentImportResultForBlocks : DocumentImportResult { }
    public class PlcBlockProtectionProvider
    {
        public IEnumerable<char> GetInvalidPasswordCharacters() => throw new NotSupportedException();
        public void Protect(SecureString password) => throw new NotSupportedException();
        public void Unprotect(SecureString password) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Blocks.Interface
{
    public class PlcBlockInterface : EngineeringObject { }
    public class InterfaceSnapshot { public void Export(FileInfo file, ExportOptions options) => throw new NotSupportedException(); }
}
namespace Siemens.Engineering.SW.Tags
{
    public class PlcTag { }
    public class PlcSystemConstant { }
    public class PlcTagComposition : List<PlcTag> { }
    public class PlcSystemConstantComposition : List<PlcSystemConstant> { }
    public class PlcTagTable
    {
        public PlcTagComposition Tags => throw new NotSupportedException();
        public PlcSystemConstantComposition SystemConstants => throw new NotSupportedException();
    }
    public class PlcTagTableGroup
    {
        public PlcTagTableComposition TagTables => throw new NotSupportedException();
        public PlcTagTableUserGroupComposition Groups => throw new NotSupportedException();
    }
    public class PlcTagTableUserGroup : PlcTagTableGroup { public string Name => throw new NotSupportedException(); }
    public class PlcTagTableComposition : List<PlcTagTable> { }
    public class PlcTagTableUserGroupComposition : List<PlcTagTableUserGroup> { }
}
namespace Siemens.Engineering.SW.WatchAndForceTables
{
    public class PlcWatchTable { }
    public class PlcForceTable { }
    public class PlcWatchTableComposition : List<PlcWatchTable> { }
    public class PlcForceTableComposition : List<PlcForceTable> { }
    public class PlcWatchAndForceTableGroup
    {
        public PlcWatchTableComposition WatchTables => throw new NotSupportedException();
        public PlcForceTableComposition ForceTables => throw new NotSupportedException();
        public PlcWatchAndForceTableUserGroupComposition Groups => throw new NotSupportedException();
    }
    public class PlcWatchAndForceTableUserGroup : PlcWatchAndForceTableGroup { public string Name => throw new NotSupportedException(); }
    public class PlcWatchAndForceTableUserGroupComposition : List<PlcWatchAndForceTableUserGroup> { }
}
namespace Siemens.Engineering.SW.ExternalSources
{
    public class PlcExternalSource { }
    public class PlcExternalSourceComposition : List<PlcExternalSource> { }
    public class PlcExternalSourceGroup
    {
        public PlcExternalSourceComposition ExternalSources => throw new NotSupportedException();
        public PlcExternalSourceUserGroupComposition Groups => throw new NotSupportedException();
    }
    public class PlcExternalSourceUserGroup : PlcExternalSourceGroup { public string Name => throw new NotSupportedException(); }
    public class PlcExternalSourceUserGroupComposition : List<PlcExternalSourceUserGroup> { }
}
namespace Siemens.Engineering.CrossReference
{
    public enum CrossReferenceFilter { AllObjects }
    public class CrossReferenceResult { }
    public class CrossReferenceService { public CrossReferenceResult GetCrossReferences(CrossReferenceFilter filter) => throw new NotSupportedException(); }
}
namespace Siemens.Engineering.Online
{
    public enum OnlineState { Offline, Online }
    public class OnlineProvider { public OnlineState State => throw new NotSupportedException(); }
}
namespace Siemens.Engineering.Online.Configurations
{
    public class OnlineConfiguration { }
    public delegate void OnlineConfigurationDelegate(OnlineConfiguration configuration);
    public class OnlinePasswordConfiguration : OnlineConfiguration { public void SetPassword(SecureString password) => throw new NotSupportedException(); }
    public enum TlsVerificationConfigurationSelection { Trusted }
    public class TlsVerificationConfiguration : OnlineConfiguration
    {
        public TlsVerificationConfigurationSelection CurrentSelection { get; set; }
        public string PlcName => throw new NotSupportedException();
        public string VerificationInfo => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.Connection
{
    public class ConnectionConfiguration { }
    public class ConfigurationAddress { public string Address => throw new NotSupportedException(); }
}
namespace Siemens.Engineering.FingerprintData
{
    public class FingerprintDataProvider
    {
        public Connection.ConnectionConfiguration Configuration => throw new NotSupportedException();
        public FingerprintDataResult GetFingerprintData(Connection.ConfigurationAddress address, Online.Configurations.OnlineConfigurationDelegate handler) => throw new NotSupportedException();
    }
    public class FingerprintDataResult { public FingerprintDataItemComposition FingerprintDataItems => throw new NotSupportedException(); }
    public class FingerprintDataItemComposition : List<FingerprintDataItem> { }
    public class FingerprintDataItem
    {
        public string FingerprintDataIdentifier => throw new NotSupportedException();
        public string FingerprintDataValue => throw new NotSupportedException();
    }
}
