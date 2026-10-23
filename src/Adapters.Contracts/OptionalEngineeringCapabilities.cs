using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public static class OptionalEngineeringCapabilities
{
    public static HardwareAddressingReply? Refusal(Exception error)
    {
        if (error == null) throw new ArgumentNullException(nameof(error));
        var cause = error.GetBaseException();
        return cause is NotSupportedException || cause is PlcSoftwareException native && native.Code is "NotSupportedOnVersion" or "NotSupported"
            ? Refusal(cause.Message) : null;
    }

    public static HardwareAddressingReply Refusal(string message) => new HardwareAddressingReply {
        Message = message,
        Meta = new System.Collections.Generic.Dictionary<string, object?> {
            ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false,
            ["status"] = "NotSupportedOnVersion", ["v4Rejection"] = "UNSUPPORTED_CAPABILITY",
            ["rejectionCode"] = "UNSUPPORTED_CAPABILITY", ["operationNotStarted"] = true,
            ["requiresSessionReset"] = false }
    };

    public static string? Unsupported(string release, OptionalEngineeringRequest request)
    {
        int major = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release, System.Globalization.CultureInfo.InvariantCulture);
        if (major == 14 && request.Operation == "ListDriveObjects" && request.IncludeFunctions)
            return "DriveFunctionInterface requires V15.1 or later; set includeFunctions=false on this release.";
        if (major == 14 && request.DriveObjectIndex > 0)
            return "V14 SP1 exposes one DriveObject service per device item; select driveObjectIndex=0.";
        if (major == 14 && request.Operation == "ManageDriveTelegrams"
            && (request.Action is not ("read" or "erase") || request.Action == "erase" && request.TelegramType is not ("MainTelegram" or "SupplementaryTelegram")))
            return "V14 SP1 supports telegram read/erase only; the modern insertion/change overloads and telegram types are unavailable.";
        if (major < 20 && request.DriveObjectNumber != 0)
            return "DriveObjectNumber requires V20 or later; select driveObjectIndex on this release.";
        if (major < 18 && request.Operation == "ListDriveObjects" && request.IncludeTechnologyExtensions)
            return "TechnologyExtensionContainer requires V18 or later; set includeTechnologyExtensions=false on this release.";
        if (major < 16 && request.Operation == "ListDriveObjects" && request.IncludeDcc)
            return "DriveControlChartContainer requires V16 or later; set includeDcc=false on this release.";
        if (major < 18 && request.Operation == "ListDccCharts" && (request.IncludeLibraries || request.IncludeBlocks || request.IncludePins))
            return "DCC blocks, pins and DCB libraries require V18 or later; disable those include flags on this release.";
        if (major < 20 && request.Operation == "GetDriveParameters" && request.Source == "read")
            return "ReadDriveParameter requires V20 or later; source=write reads the writable parameter view.";
        if (major == 15 && request.Operation == "ManageSivarcRule" && request.Action == "create")
            return "SiVArc rule Create(string) requires V16 or later; read/update/delete are available.";
        if (major < 20 && request.Operation is "ResolveSivarcExpression" or "UpgradeSivarcDefinitions")
            return "The requested SiVArc service requires V20 or later.";
        if (major < 21 && request.Operation is "ManageSivarcScreenLayout" or "ManageDriveSafetyAcceptanceTest")
            return "The requested optional-product service requires V21.";
        if (major < 21 && request.Operation == "ManageDriveHardwareModule" && request.Action != "read")
            return "DriveItemHardwareModule writes require V21.";
        if (major < 18 && request.Operation == "ManageDcbLibraries" && request.Action == "read")
            return "Reading drive DCB libraries requires V18 or later; extension-library import is available from V16.";
        if (major < 21 && request.Operation == "ManageSivarcBlockDefinition" && request.Kind is "tagMemberSettings" or "commonParameters" or "blockParameter")
            return "SiVArc tag member configuration requires V21.";
        if (major < 18 && request.Operation == "ManageDccChart" && request.Action == "create")
            return "The requested DCC chart creation overload requires V18 or later.";
        if (major < 18 && request.Operation == "ManageDccChart" && request.Action == "readSequence" && !string.IsNullOrEmpty(request.ChartName))
            return "Chart-level GetRunSequence requires V18 or later; container-level sequence reads are available.";
        if (major == 15 && request.Operation == "GenerateSivarc" && request.Inputs.TryGetValue(request.AdditionalHmiDeviceNamesJson, out var devices) && devices.Items.Length != 0)
            return "The multiple-HMI generation overload requires V16 or later.";
        if (major < 20 && request.Category == "advancedTags") return "AdvancedTagRules requires V20 or later.";
        if (major < 18 && request.Operation == "ManageSivarcRuleContainer")
            return "SiVArc rule table and folder operations require V18 or later.";
        if (major < 18 && request.Operation is "ListSivarcRules" or "ManageSivarcRule")
        {
            string path = request.Operation == "ListSivarcRules" ? request.ObjectPathJson : request.CollectionPathJson;
            if (request.Inputs.TryGetValue(path, out var steps) && steps.Items.Any(step => step.Properties.TryGetValue("property", out var property) && property.Text is "Tables" or "Folders"))
                return "SiVArc table/folder addressing requires V18 or later; root Rules/Groups are available.";
        }
        if (major < 18 && request.Operation == "ManageDccChart" && (request.SequenceIndex >= 0
            || request.Inputs.TryGetValue(request.PropertiesJson, out var chartProperties) && chartProperties.Properties.Keys.Any(k => k != "Name")))
            return "DCC Statement properties and runtime sequence moves require V18 or later.";
        if (major < 18 && request.Operation is "GetSivarcRuleTree" or "ManageSivarcTableRule" && !string.IsNullOrEmpty(request.TablePath))
            return "SiVArc rule tables require V18 or later; the release exposes the root rule composition.";
        if (major < 20 && request.Operation == "ManageDccPin" && request.Action == "publish" && request.ArrayIndex >= 0)
            return "The array-index Publish overload requires V20 or later.";
        if (request.Operation == "ManageDriveTelegrams" && request.Action == "connectTechnologyObject"
            && (major < 19 || major < 20 && request.InterfaceKind == "torque"))
            return "Telegram actor/sensor/encoder connections require V19; torque connections require V20.";
        if (major < 19 && request.Operation == "ManageDriveTelegrams" && request.TelegramType == "MainTelegram" && request.Action is "check" or "insert")
            return "The MainTelegram insertion overload requires V19 or later.";
        if (major < 20 && request.Operation == "ManageDriveTelegrams" && request.TelegramType is "TorqueTelegram" or "EdgeTelegram")
            return "Generic telegram insertion for the selected type requires V20 or later.";
        if (major < 20 && request.Operation == "ManageDriveFunctions" && request.Action is "activateFunction" or "deactivateFunction" or "setSIAxisType" or "setMotorCode")
            return "The requested drive function requires V20 or later.";
        if (major < 19 && request.Operation == "ManageDriveFunctions" && request.Action is "setEncoderType" or "setEquivalentCircuitDiagramData" or "updateCheckSums")
            return "The requested drive configuration/safety checksum function requires V19 or later.";
        if (major < 20 && request.Operation == "ManageTechnologyExtensions" && request.Action == "installAndGetIdentifier")
            return "InstallAndGetIdentifier requires V20 or later.";
        if (major < 20 && request.Operation == "ManageTechnologyExtensions" && request.IncludeParameters)
            return "TechnologyExtension.Parameters requires V20 or later.";
        if (major < 20 && request.Operation == "ManageSivarcTableRule")
        {
            if (request.Inputs.TryGetValue(request.PropertiesJson, out var properties)
                && properties.Properties.Keys.Any(k => k == "LoopCount" || request.Kind == "group" && k == "LayoutField"))
                return "The requested SiVArc layout property requires V20 or later.";
            if (request.Kind == "group" && request.Inputs.TryGetValue(request.ReferencesJson, out var references) && references.Properties.Count != 0)
                return "Screen rule group references require V20 or later.";
        }
        return null;
    }
}
