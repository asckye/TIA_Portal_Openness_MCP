using System;
using System.Globalization;

namespace TiaMcp.Adapters.Contracts;

/// <summary>Release boundaries of the SDK members used by hosts and adapters.</summary>
public sealed class OpennessApi
{
    // The verified V21 Base, Step7, Safety and Startdrive assemblies have a different identity from Siemens.Engineering.dll.
    public const string SplitAssemblyRelease = "21";
    // V20 HW.Device.ShowInEditor(View) requires a view. V21 also exposes Device.ShowInEditor().
    public const string DeviceEditorRequiresViewRelease = "20";

    // SW.Units.PlcUnitProvider
    public static readonly OpennessApi PlcUnits = new(16);
    // SW.SWImportOptions.IgnoreUnitAttributes
    public static readonly OpennessApi IgnoreUnitAttributes = new(16);
    // VersionControl.VersionControlInterface
    public static readonly OpennessApi VersionControl = new(16);
    // HW.HardwareCatalog.HardwareCatalog
    public static readonly OpennessApi HardwareCatalog = new(18);
    // Schemas/SW.InterfaceSections_v5.xsd ships alongside v4 on V17. The V17 declaration builder selects v4.
    public static readonly OpennessApi InterfaceSectionsV5 = new(17);
    // HmiUnified.HmiSoftware
    public static readonly OpennessApi UnifiedHmi = new(19);
    // Online.Configurations.OnlineAuthenticationConfiguration
    public static readonly OpennessApi OnlineAuthentication = new(19);
    // ImportOptions.SkipInactiveCultures and ActivateInactiveCultures
    public static readonly OpennessApi CultureImportOptions = new(19);
    // SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup
    public static readonly OpennessApi TechnologyGroups = new(19);

    // SW.ImportDocumentOptions and SW.Blocks.PlcBlock.ExportAsDocuments(DirectoryInfo, String)
    public static readonly OpennessApi PlcDocuments = new(20);
    // ObjectIdentifierProvider
    public static readonly OpennessApi ObjectIdentifiers = new(20);
    // SW.Blocks.PlcBlockComposition.CreateFrom overloads with MasterCopyMode / UpdatePathsMode
    public static readonly OpennessApi LibraryCopyModes = new(20);
    // Library.Types.LibraryTypeVersion.Edit, Release and Discard
    public static readonly OpennessApi LibraryVersionEditing = new(20);
    // Library.Types.LibraryType.DoNotUse
    public static readonly OpennessApi LibraryDoNotUse = new(20);
    // Library.Types.LibraryTypeComposition.CreateFromDocuments
    public static readonly OpennessApi LibraryDocuments = new(20);
    // SW.Alarm.TextLists.PlcAlarmUserTextlistComposition.CreateFrom(MasterCopy, MasterCopyMode)
    public static readonly OpennessApi AlarmTextListCopyMode = new(20);
    // HW.Utilities.CardReaderPscProvider.Export(Device, FileInfo, SecureString)
    public static readonly OpennessApi EncryptedPscExport = new(20);
    // HW.Features.TelecontrolManagement.ExportDataPoints and ImportDataPoints
    public static readonly OpennessApi TelecontrolFiles = new(20);
    // HmiUnified.HmiSoftware.HmiTextLists and HmiSystemTextLists
    public static readonly OpennessApi UnifiedTextLists = new(20);
    // HmiUnified.HmiSoftware.HmiAlarmAuditClass
    public static readonly OpennessApi UnifiedAlarmAudit = new(20);
    // HmiUnified.UI.Dynamization.ExpressionDynamization and TagParameterDynamization
    public static readonly OpennessApi UnifiedExpressionDynamization = new(20);
    // HmiUnified.RuntimeSettings.HmiRuntimeSetting.EnableLanguageCompatibleFontFamilies
    public static readonly OpennessApi UnifiedRuntimeFontSettings = new(20);

    // HW.Features.TelecontrolManagement.TelecontrolDataPoints
    public static readonly OpennessApi TelecontrolPoints = new(21);
    // SW.Blocks.Interface.ValueService
    public static readonly OpennessApi SnapshotValues = new(21);
    // SW.OpcUa.ServerInterfaceGroup.AccessControl
    public static readonly OpennessApi OpcUaAccessControl = new(21);
    // SW.PlcDocumentComposition.CreateFrom
    public static readonly OpennessApi PlcDocumentCreation = new(21);
    // HW.CommunicationConnections.ConnectionComposition
    public static readonly OpennessApi CommunicationConnections = new(21);
    // HW.Features.DefaultWebPagesFeature
    public static readonly OpennessApi DefaultWebPages = new(21);
    // HW.Features.PlcMasterSecretConfigurator.ProtectAllPlcConfiguration
    public static readonly OpennessApi ProtectAllConfiguration = new(21);
    // Safety.SafetyBaseIdProvider
    public static readonly OpennessApi SafetyBaseId = new(21);
    // HmiUnified.HmiSoftware.HmiGraphicLists
    public static readonly OpennessApi UnifiedGraphicLists = new(21);
    // HmiUnified.RuntimeSettings.HmiRuntimeSetting.CentralInputHint
    public static readonly OpennessApi UnifiedRuntimeInputSettings = new(21);
    // HmiUnified.UI.Widgets.HmiAlarmIndicator and the process diagnosis controls
    public static readonly OpennessApi UnifiedAlarmAndDiagnosisItems = new(21);
    // MC.Drives.DriveItemHardwareModule.ChangeType
    public static readonly OpennessApi DriveHardwareChanges = new(21);
    // SW.ExternalSources.PlcExternalSourceUserGroup.Name gains a public setter.
    public static readonly OpennessApi ExternalSourceGroupRename = new(21);
    // SiVArc.TagMemberSetting
    public static readonly OpennessApi SivarcTagMembers = new(21);

    private readonly string[] releases;
    public int FirstMajor { get; }

    private OpennessApi(int firstMajor)
    {
        FirstMajor = firstMajor;
        releases = ReleaseRange.Since(firstMajor.ToString(CultureInfo.InvariantCulture));
    }

    public bool Includes(string? release) => Array.IndexOf(releases, release) >= 0;
}
