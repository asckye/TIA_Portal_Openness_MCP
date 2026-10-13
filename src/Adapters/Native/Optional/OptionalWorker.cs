using System;
using System.Runtime.CompilerServices;
using Siemens.Engineering.Library.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Optional;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    internal object OptionalHmiScreen(string software, string screen)
        => new TiaMcp.Adapters.Native.Hmi.HmiSession(this, EngineeringPlcSession, new HmiRequest { Operation = "ManageSivarcScreenLayout" })
            .ResolveHmiScreenOrThrow(software, screen);

    public static string OptionalLibraryKind(object value)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
        {
            switch (type.FullName)
            {
                case "Siemens.Engineering.SiVArc.ScreenRuleTableType": return "sivarcScreenRuleTable";
                case "Siemens.Engineering.SiVArc.TagRuleTableType": return "sivarcTagRuleTable";
                case "Siemens.Engineering.SiVArc.AdvancedTagRuleTableType": return "sivarcAdvancedTagRuleTable";
                case "Siemens.Engineering.SiVArc.AlarmRuleTableType": return "sivarcAlarmRuleTable";
                case "Siemens.Engineering.SiVArc.CopyRuleTableType": return "sivarcCopyRuleTable";
                case "Siemens.Engineering.SiVArc.TextlistRuleTableType": return "sivarcTextlistRuleTable";
                case "Siemens.Engineering.SiVArc.ExpressionTableType": return "sivarcExpressionTable";
                case "Siemens.Engineering.MC.Drives.Dcc.DcbBlockTypeLibraryType": return "dccBlockType";
            }
        }
        return "other";
    }
    internal LibraryType OptionalLibraryType(object library, string path) => ExactLibraryType(library, path);
    internal LibraryTypeVersion OptionalTypeVersion(LibraryType type, string version) => ExactTypeVersion(type, version);
    internal string OptionalLibraryPath(object value, string root) => LibraryPathOf(value, root);
    internal object? OptionalMultilingual(Siemens.Engineering.MultilingualText text) => MultilingualJson(text);
    internal object OptionalTechnology(string software, string path, bool writing) => new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(PlcB5Session).ExactTechnology(software, path, writing);
    public HardwareAddressingReply ExecuteSivarc(OptionalEngineeringRequest request, bool dryRun = true)
        => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.Sivarc", () => ExecuteSivarcCore(request, dryRun));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HardwareAddressingReply ExecuteSivarcCore(OptionalEngineeringRequest request, bool dryRun)
    {
        Check();
        if (OptionalEngineeringCapabilities.Unsupported(ReleaseKey, request) is string unavailable) throw new NotSupportedException(unavailable);
        if (request.IsWrite != OptionalEngineeringMutations.Writes(request) || request.IsWrite == dryRun) throw new ArgumentException("Conflicting optional-product preview mode.");
#if OPTIONAL_V20_PLUS
        var previous = OptionalInputs.Current;
        OptionalInputs.Current = request;
        try
        {
            var session = new OptionalSession(this);
            switch (request.Operation)
            {
                case "GetSivarcRuleTree": return new SivarcAdapter(session).ReadSivarcRuleTree(request.Category, request.FolderPath, request.TablePath, request.IncludeRules, request.MaxDepth, request.Offset, request.Limit);
                case "ManageSivarcRuleContainer": return new SivarcAdapter(session).ManageSivarcRuleContainer(request.Category, request.Kind, request.Path, request.Action, request.LibraryName, request.TypePath, request.TypeVersion, request.ConfirmDelete, request.DryRun);
                case "ManageSivarcTableRule": return new SivarcAdapter(session).ManageSivarcTableRule(request.Category, request.TablePath, request.RulePath, request.Kind, request.Action, request.PropertiesJson, request.ReferencesJson, request.DeviceSelectionJson, request.DeviceNamesJson, request.LibraryName, request.MasterCopyPath, request.CreateOption, request.ConfirmDelete, request.DryRun);
                case "ListSivarcBlockDefinitions": return new SivarcAdapter(session).ReadSivarcBlockDefinitions(request.SoftwarePath, request.BlockPath, request.IncludeBlockParameters);
                case "ManageSivarcBlockDefinition": return new SivarcAdapter(session).ManageSivarcBlockDefinition(request.SoftwarePath, request.BlockPath, request.Kind, request.Name, request.Action, request.PropertiesJson, request.TextsJson, request.ConfirmDelete, request.DryRun);
#if SIVARC_UPGRADER
                case "ResolveSivarcExpression": return new SivarcAdapter(session).ResolveSivarcExpression(request.SoftwarePath, request.BlockPath, request.DevicePathJson, request.ItemPathJson, request.LibraryItemKind, request.LibraryItemPath, request.Expression, request.LibraryName, request.MaxResults);
#endif
                case "ManageSivarcScreenLayout": return new SivarcAdapter(session).ManageSivarcScreenLayout(request.SoftwarePath, request.ScreenName, request.Action, request.FilePath, request.DryRun);
#if SIVARC_UPGRADER
                case "UpgradeSivarcDefinitions": return new SivarcAdapter(session).UpgradeSivarcDefinitions(request.SoftwarePath, request.DryRun);
#endif
                case "GenerateSivarc": return new SivarcAdapter(session).GenerateSiVArc(request.HmiDeviceName, request.PlcSoftwarePathsJson, request.GenerationOptions, request.DryRun, request.AdditionalHmiDeviceNamesJson);
                case "ListSivarcRules": return new OptionalEngineeringAdapter(session).ReadSiVArcRules(request.Category, request.ObjectPathJson, request.Offset, request.Limit);
                case "ManageSivarcRule": return new OptionalEngineeringAdapter(session).ManageSiVArcRule(request.Category, request.CollectionPathJson, request.Name, request.Action, request.PropertiesJson, request.DryRun);
            }
        }
        finally { OptionalInputs.Current = previous; }
#else
        return new LegacyOptionalAdapter(this, request).Execute();
#endif
        throw new NotSupportedException("The requested optional-product operation is unavailable on this release.");
    }
    public HardwareAddressingReply ExecuteStartdrive(OptionalEngineeringRequest request, bool dryRun = true)
        => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.Startdrive", () => ExecuteStartdriveCore(request, dryRun));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HardwareAddressingReply ExecuteStartdriveCore(OptionalEngineeringRequest request, bool dryRun)
    {
        Check();
        if (OptionalEngineeringCapabilities.Unsupported(ReleaseKey, request) is string unavailable) throw new NotSupportedException(unavailable);
        if (request.IsWrite != OptionalEngineeringMutations.Writes(request) || request.IsWrite == dryRun) throw new ArgumentException("Conflicting optional-product preview mode.");
#if OPTIONAL_V20_PLUS
        var previous = OptionalInputs.Current;
        OptionalInputs.Current = request;
        try
        {
            var session = new OptionalSession(this);
            switch (request.Operation)
            {
                case "ListDriveObjects": return new StartdriveAdapter(session).ReadDriveObjects(request.DevicePathJson, request.ItemPathJson, request.IncludeTelegrams, request.IncludeFunctions, request.IncludeTechnologyExtensions, request.IncludeDcc);
                case "GetDriveParameters": return new StartdriveAdapter(session).ReadDriveParameters(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Source, request.NamesJson, request.NumbersJson, request.IncludeBits, request.IncludeEnumValues, request.Offset, request.Limit, request.IncludeValue);
                case "ManageStartdriveParameter": return new StartdriveAdapter(session).ManageStartdriveParameter(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.Parameter, request.Action, request.ValueJson, request.DryRun, request.DriveObjectIndex);
                case "ManageDriveTelegrams": return new StartdriveAdapter(session).ManageDriveTelegrams(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Action, request.TelegramType, request.TelegramNumber, request.InputSize, request.OutputSize, request.Direction, request.Size, request.KeepOriginalAddress, request.SoftwarePath, request.ObjectPath, request.InterfaceKind, request.SensorIndex, request.ConnectOption, request.DryRun);
                case "ManageDriveFunctions": return new StartdriveAdapter(session).ManageDriveFunctions(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Action, request.ValueJson, request.DryRun);
                case "ManageDriveSecurity": return new StartdriveAdapter(session).ManageDriveSecurity(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Action, request.Password, request.DryRun);
                case "ManageTechnologyExtensions": return new StartdriveAdapter(session).ManageTechnologyExtensions(request.Action, request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Identifier, request.FilePath, request.ConfirmUninstallInUse, request.IncludeParameters, request.DryRun);
                case "ManageDriveHardwareModule": return new StartdriveAdapter(session).ManageDriveHardwareModule(request.DevicePathJson, request.ItemPathJson, request.Action, request.TypeIdentifier, request.PositionNumber, request.DryRun);
                case "ManageDriveSafetyAcceptanceTest": return new StartdriveAdapter(session).ManageDriveSafetyAcceptanceTest(request.DevicePathJson, request.ItemPathJson, request.Action, request.Identifier, request.Active, request.FilePath, request.FileOperation, request.DryRun);
#if OPTIONAL_READ_PARAMETERS
                case "GetOnlineDriveParameters": return new StartdriveAdapter(session).ReadOnlineDriveParameters(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.NamesJson, request.NumbersJson, request.IncludeBits, request.IncludeEnumValues, request.Offset, request.Limit, request.IncludeValue);
#endif
                case "ManageOnlineDriveFunctions": return new StartdriveAdapter(session).ManageOnlineDriveFunctions(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.Action, request.ResetMode, request.ActivationState, request.ConfirmOnline);
            }
        }
        finally { OptionalInputs.Current = previous; }
#else
        return new LegacyOptionalAdapter(this, request).Execute();
#endif
        throw new NotSupportedException("The requested optional-product operation is unavailable on this release.");
    }
    public HardwareAddressingReply ExecuteDcc(OptionalEngineeringRequest request, bool dryRun = true)
        => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.Startdrive",
            () => OptionalAssemblyAdmission.Run(ReleaseKey, "Siemens.Engineering.DCC", () => ExecuteDccCore(request, dryRun)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HardwareAddressingReply ExecuteDccCore(OptionalEngineeringRequest request, bool dryRun)
    {
        Check();
        if (OptionalEngineeringCapabilities.Unsupported(ReleaseKey, request) is string unavailable) throw new NotSupportedException(unavailable);
        if (request.IsWrite != OptionalEngineeringMutations.Writes(request) || request.IsWrite == dryRun) throw new ArgumentException("Conflicting optional-product preview mode.");
#if OPTIONAL_V20_PLUS
        var previous = OptionalInputs.Current;
        OptionalInputs.Current = request;
        try
        {
            var session = new OptionalSession(this);
            switch (request.Operation)
            {
                case "ListDccCharts": return new DccAdapter(session).ReadDccCharts(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.ChartPath, request.MaxDepth, request.IncludeBlocks, request.IncludePins, request.IncludeLibraries);
                case "ManageDccChart": return new DccAdapter(session).ManageDccChart(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.ChartName, request.Action, request.FilePath, request.ImportOptions, request.PropertiesJson, request.DryRun, request.DriveObjectIndex, request.ConfirmDelete, request.SequenceIndex);
                case "ManageDccBlock": return new DccAdapter(session).ManageDccBlock(request.DevicePathJson, request.ItemPathJson, request.ChartPath, request.BlockName, request.Action, request.BlockType, request.LibraryName, request.PropertiesJson, request.DriveObjectNumber, request.DriveObjectIndex, request.ConfirmDelete, request.SequenceIndex, request.DryRun);
                case "ManageDccPin": return new DccAdapter(session).ManageDccPin(request.DevicePathJson, request.ItemPathJson, request.ChartPath, request.BlockName, request.PinName, request.Action, request.PropertiesJson, request.PartnerJson, request.SetAsSignal, request.ParameterNumber, request.ArrayIndex, request.DriveObjectNumber, request.DriveObjectIndex, request.DryRun);
                case "ManageDccChartInterface": return new DccAdapter(session).ManageDccChartInterface(request.DevicePathJson, request.ItemPathJson, request.ChartPath, request.InterfaceName, request.Action, request.SourceBlock, request.SourcePin, request.PropertiesJson, request.DriveObjectNumber, request.DriveObjectIndex, request.ConfirmDelete, request.DryRun);
                case "ManageDccChartPartition": return new DccAdapter(session).ManageDccChartPartition(request.DevicePathJson, request.ItemPathJson, request.ChartPath, request.PartitionName, request.Action, request.PropertiesJson, request.DriveObjectNumber, request.DriveObjectIndex, request.ConfirmDelete, request.DryRun);
                case "ManageDcbLibraries": return new DccAdapter(session).ManageDcbLibraries(request.Action, request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.DriveObjectIndex, request.FilePath, request.DryRun);
                case "GetDccObject": return new DccAdapter(session).ReadDccObject(request.DevicePathJson, request.ItemPathJson, request.DriveObjectNumber, request.ObjectPathJson, request.Offset, request.Limit, request.DriveObjectIndex);
            }
        }
        finally { OptionalInputs.Current = previous; }
#else
        return new LegacyOptionalAdapter(this, request).Execute();
#endif
        throw new NotSupportedException("The requested optional-product operation is unavailable on this release.");
    }
}
