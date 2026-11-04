using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class HardwareFamilyWire
{
    private static bool Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);
    private static bool Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out _);
    private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static bool False(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;
    private static bool Any(JsonNode? node) => true;
    private static bool Object(JsonNode? node) => node is JsonObject;
    private static bool OptionalText(JsonNode? node) => node == null || Text(node);
    private static bool OptionalNumber(JsonNode? node) => node == null || Number(node);
    private static bool OptionalFlag(JsonNode? node) => node == null || Flag(node);
    private static bool OptionalObject(JsonNode? node) => node == null || node is JsonObject;
    private static bool Texts(JsonNode? node) => node is JsonArray items && items.All(Text);
    private static bool Objects(JsonNode? node) => node is JsonArray items && items.All(Object);
    private static bool Description(JsonNode? node) => node == null || node is JsonObject row
        && Text(row["Name"]) && row["Attributes"] is JsonArray attributes
        && attributes.All(a => a is JsonObject && Text(a["Name"]));
    // The cached project binding of SessionCallbackPrimitives.Snapshot: exactly these five members.
    private static bool Binding(JsonNode? node) => node is JsonObject binding && binding.Count == 5
        && binding.ContainsKey("identity") && (binding["identity"] is null or JsonObject)
        && binding.ContainsKey("fault") && (binding["fault"] == null || Text(binding["fault"]))
        && Flag(binding["instanceReserved"]) && Flag(binding["verifiedLive"]) && Text(binding["note"]);
    // HardwareAddressingReply, SecurityResult and OptionalProductReply share these wire members.
    // Meta is an evidence bag: refusals and failures need not contain the legacy success flag.
    private static bool Step(JsonNode? reply) => Exactly(reply, ("Message", Text), ("Meta", Object),
        ("RequiresSessionReset", Flag), ("MayHaveChanged", Flag), ("BlockReadsAfterUncertain", True));

    private static bool SoftwareFailure(JsonNode? node) => node == null || Exactly(node, ("Code", Text), ("Message", Text),
        ("Candidates", values => values == null || Texts(values)), ("Evidence", Object));

    private static bool B5Block(JsonNode? node) => Exactly(node, ("Name", OptionalText), ("TypeName", OptionalText),
        ("Namespace", OptionalText), ("ProgrammingLanguage", OptionalText), ("MemoryLayout", OptionalText),
        ("IsConsistent", OptionalFlag), ("HeaderName", OptionalText), ("ModifiedDate", OptionalText),
        ("IsKnowHowProtected", OptionalFlag), ("Attributes", values => values == null || values is JsonArray attributes
            && attributes.All(a => Exactly(a, ("Name", Text), ("AccessMode", OptionalText), ("Value", Any)))), ("Description", OptionalText));

    private static bool B5Event(JsonNode? node) => Exactly(node, ("Kind", Text), ("Stage", Text), ("State", OptionalText),
        ("ChangesProject", Flag), ("Directory", Text), ("Name", Text), ("Value", Any));

    // An object with exactly these members, each valid. WorkerJson writes every member, nulls included.
    private static bool Exactly(JsonNode? node, params (string Name, Func<JsonNode?, bool> Valid)[] members)
        => node is JsonObject reply && reply.Count == members.Length && members
            .All(member => reply.ContainsKey(member.Name) && member.Valid(reply[member.Name]));

    // The created device of HardwareDevices.HardwareCreate*Device*: null, or its name only.
    private static bool CreatedDevice(JsonNode? node) => node == null || Exactly(node, ("Name", Text));

    private static string? Request(JsonObject arguments, string field)
        => arguments["request"] is JsonObject request && request[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // SecurityWorker.ExecuteHardwareSecurity: the PUT/GET replies are the dictionaries of PutGetAccess.GetPutGetAccess and
    // HardwareSecurityAdapter.SetPutGetAccess; the two managed operations return a SecurityResult step.
    private static bool HardwareSecurity(string? operation, JsonNode? reply) => operation switch
    {
        "GetPlcPutGetAccess" => Exactly(reply, ("found", False), ("message", Text))
            || Exactly(reply, ("found", False), ("device", Text), ("message", Text))
            || Exactly(reply, ("found", True), ("device", Text), ("deviceItem", Text), ("attributeName", Text), ("enabled", Flag), ("rawValue", Text)),
        "SetPlcPutGetAccess" => Exactly(reply, ("ok", False), ("failureCode", Text), ("message", Text))
            || Exactly(reply, ("ok", False), ("failureCode", Text), ("device", Text), ("message", Text))
            || Exactly(reply, ("ok", False), ("failureCode", Text), ("mayHaveChanged", False), ("device", Text), ("message", Text))
            || Exactly(reply, ("ok", False), ("failureCode", Text), ("mayHaveChanged", False), ("message", Text))
            || Exactly(reply, ("ok", True), ("dryRun", True), ("mayHaveChanged", False), ("writeOutcomeKnown", True),
                ("device", Text), ("attributeName", Text), ("before", Flag), ("requestedValue", Flag), ("message", Text))
            || Exactly(reply, ("ok", False), ("mayHaveChanged", True), ("writeOutcomeKnown", False), ("device", Text), ("attributeName", Text),
                    ("message", Text))
            || Exactly(reply, ("ok", Flag), ("mayHaveChanged", True), ("writeOutcomeKnown", Flag), ("device", Text), ("deviceItem", Text),
                ("attributeName", Text), ("before", Text), ("after", Text), ("note", Text)),
        "ManagePlcProtection" or "ManageWatchForceTableWebAccess" => Step(reply),
        _ => false,
    };

    // PlcWorker/WorkerProjectScaffold.Execute, per primitive kind; ProjectLifecyclePortService reads each as its response type.
    private static bool Scaffold(string? kind, JsonNode? reply) => kind switch
    {
        "connect" or "createProject" or "save" or "lad" or "sclImport" or "sclGenerate" => Exactly(reply, ("Message", Text), ("Meta", Object)),
        "addDevice" => Exactly(reply, ("Ok", Flag), ("DeviceName", Text), ("Family", Text), ("MlfbUsed", OptionalText), ("VersionUsed", OptionalText),
            ("Attempts", node => node == null || Texts(node)), ("Error", OptionalText), ("Message", Text), ("Meta", Object)),
        "compile" => Exactly(reply, ("State", Text), ("ErrorCount", OptionalNumber), ("WarningCount", OptionalNumber), ("Errors", Texts),
            ("Warnings", Texts), ("Info", Texts), ("RawMessages", Texts), ("Message", Text), ("Meta", Object)),
        "hmiInfo" => Exactly(reply, ("Name", OptionalText), ("ProgramType", OptionalText), ("Screens", node => node == null || node is JsonArray),
            ("Message", Text), ("Meta", Object)),
        // HmiDescribe of EnsureUnifiedHmiConnection with the scaffold's Meta.
        "hmiConnect" => Exactly(reply, ("Meta", Object), ("Message", OptionalText), ("ObjectKind", OptionalText), ("ObjectPath", OptionalText),
            ("TypeName", OptionalText), ("Members", node => node == null || node is JsonArray)),
        // HmiMessage of the Unified HMI step tools.
        "hmiScreen" or "hmiDesign" or "hmiTag" => Exactly(reply, ("Message", OptionalText), ("Meta", Object)),
        _ => false,
    };

    internal static void Validate(string operation, JsonNode? reply) => Validate(operation, new JsonObject(), reply);

    internal static void Validate(string operation, JsonObject arguments, JsonNode? reply)
    {
        string name = operation[(operation.LastIndexOf('.') + 1)..];
        bool valid = name switch {
            "ExecutePlcSimAdvanced" => reply is JsonObject simulation && Flag(simulation["Ok"])
                && Text(simulation["Message"]) && simulation["Data"] is JsonObject
                && simulation["Meta"] is JsonObject simulationMeta && Flag(simulationMeta["success"])
                && !simulation.ContainsKey("RequiresSessionReset"),
            "HardwareDescribeDeviceAt" or "HardwareDescribeItemAt" => Description(reply),
            "HardwareDescribeDevices" => reply is JsonArray devices && devices.All(d => d != null && Description(d)),
            "HardwareReadItemTree" or "HardwareCreateDevice" or "HardwareProbeConnectDeviceNodesToSubnet" => Text(reply),
            "HardwareReadPlcNames" => reply is JsonArray names && names.All(Text),
            "HardwareLegacySearchInstalledGsdDevices" or "HardwareLegacySearchHardwareCatalog" => reply is JsonArray catalog
                && catalog.All(c => c is JsonObject && Text(c["TypeIdentifier"])),
            // HardwareDevices.HardwareCreateGsdDevice/HardwareCreateCatalogDevice and HardwareCreateDeviceFallback, member for member.
            "HardwareCreateGsdDevice" or "HardwareCreateCatalogDevice" => Exactly(reply, ("Device", CreatedDevice), ("Candidate", OptionalObject),
                ("Candidates", Objects), ("Attempts", Texts), ("Error", OptionalText)),
            "HardwareCreateDeviceFallback" => Exactly(reply, ("Device", CreatedDevice), ("MlfbUsed", OptionalText), ("VersionUsed", OptionalText),
                ("Attempts", Texts), ("Error", OptionalText)),
            "HardwareLegacyDumpDeviceAttributes" => reply is JsonObject attributes && Flag(attributes["found"]),
            "HardwareReadPlugLocations" => reply == null || reply is JsonObject slots && slots["free"] is JsonArray
                && slots["occupied"] is JsonArray,
            "HardwarePlugModule" => reply is JsonObject module && Flag(module["Ok"]) && Text(module["Message"])
                && Flag(module["MayHaveChanged"]) && Flag(module["RequiresSessionReset"])
                && (!(bool)module["RequiresSessionReset"]! || (bool)module["MayHaveChanged"]!),
            "HardwareGetDeviceItemNetworkInfo" => reply == null || reply is JsonArray network
                && network.All(a => a is JsonObject && Text(a["Name"])),
            "HardwareGetProjectTopology" => reply is JsonObject topology && (Text(topology["failureCode"])
                || Number(topology["deviceCount"]) && topology["devices"] is JsonArray),
            "ReadHardwareOwnerCandidates" or "ReadHardwareWhitelistedServices" => reply is JsonObject probe
                && probe["Lines"] is JsonArray lines && lines.All(Text),
            "ExportHardwareAml" => reply is JsonObject export && Flag(export["Success"]) && Text(export["State"])
                && Text(export["FilePath"]) && Number(export["ErrorCount"]) && Number(export["WarningCount"]),
            // Values that the shared port services read directly (ProjectLifecyclePortService, EngineeringServicesPortService);
            // the engine serves the same services in process. Each operation keeps its one exact shape.
            "EnsureOpennessUserGroup" or "ArchiveSaveProjectCopy" or "ReadEngineeringTransaction" => Flag(reply),
            "BeginEngineeringTransaction" => reply is JsonValue scope && scope.TryGetValue<string>(out var id) && id.Length > 0,
            "CommitEngineeringTransaction" or "DisposeEngineeringTransaction" => reply == null,
            "ReadProjectLifecycleBinding" => Binding(reply),
            "ExecuteHardwareSecurity" => HardwareSecurity(Request(arguments, "Operation"), reply),
            "ExecuteProjectScaffoldPrimitive" => Scaffold(Request(arguments, "Kind"), reply),
            // Contract replies of the ported families without step members, as WorkerJson writes them.
            "ExecutePlcB5" => Exactly(reply, ("Blocks", node => node == null || node is JsonArray blocks && blocks.All(B5Block)), ("Count", OptionalNumber),
                ("Events", node => node is JsonArray events && events.All(B5Event)), ("ExportFailures", Texts), ("ImportFailures", Texts),
                    ("ImportedNames", Texts),
                ("Scanned", Number), ("Selected", Number), ("Attempted", Number), ("Succeeded", Number),
                ("Stopped", Flag), ("Ok", Flag), ("Value", Flag), ("Message", Text), ("Meta", OptionalObject),
                ("Data", OptionalObject), ("Failure", SoftwareFailure), ("RequiresSessionReset", Flag),
                ("MayHaveChanged", Flag), ("BlockReadsAfterUncertain", True)),
            "ExecuteVersionControl" or "ExecuteTeamcenter" => Exactly(reply, ("Message", Text),
                ("Items", node => node == null || Texts(node)), ("Meta", Object), ("RequiresSessionReset", Flag),
                ("MayHaveChanged", Flag), ("BlockReadsAfterUncertain", True)),
            "ExecuteSivarc" or "ExecuteStartdrive" or "ExecuteDcc" or "ExecuteSafety" or "ExecuteSafetyValidation"
                or "ExecuteOptionalProduct" or "ExecuteEngineeringServices" or "ReadPortalInfo" or "ConnectIsolatedPortal"
                or "ArchiveSavedProject" or "RetrieveProjectArchive" or "ExecuteMultiuserSession" or "ValidateAutomationContext"
                or "ReadProjectProtection" or "ExecuteProjectUsers" or "ExecuteCertificate" or "ExecuteSecurityDeep" or "ExecuteDeviceUsers"
                or "HardwareLegacySetDeviceItemAttribute" or "HardwareLegacySetCpuCommonSettings" or "HardwareManageHardwareObject"
                or "HardwareReadCommunicationConnections" or "HardwareManageCommunicationConnection" or "HardwareExchangeSystemDiagnosticsSettings"
                or "HardwareReadHardwareFeatures" or "HardwareManageDeviceServiceObjects" or "HardwareManageHardwareUtilities"
                or "HardwareManageDeviceUserGroup" or "HardwareReadIoSystems" or "HardwareManageIoSystem" or "HardwareReadNetworkDomains"
                or "HardwareManageNetworkDomain" or "HardwareReadTransferAreas" or "HardwareManageTransferArea" or "HardwareReadDeviceItemChannels"
                or "HardwareUpdateDeviceItemChannel" or "HardwareManagePortInterconnection" or "HardwareEnsureSubnet"
                or "HardwareAttachDeviceNodeToSubnet" or "ImportHardwareAml" => Step(reply),
            "ExecuteOnline" => Exactly(reply, ("Value", Any), ("Failure", OptionalObject), ("MayHaveChanged", Flag),
                ("RequiresSessionReset", Flag), ("BlockReadsAfterUncertain", Flag)),
            "ExecutePlcOrganisation" => Exactly(reply, ("Report", OptionalObject), ("Names", node => node == null || Texts(node)),
                ("TagAddresses", OptionalObject), ("Message", Text), ("Meta", Object), ("Data", OptionalObject),
                ("Created", Texts), ("References", node => node == null || Objects(node)), ("Reason", OptionalText),
                ("Queried", Flag), ("Batch", OptionalObject), ("Compiler", OptionalObject), ("Found", Flag),
                ("Failure", SoftwareFailure),
                ("RequiresSessionReset", Flag), ("MayHaveChanged", Flag), ("BlockReadsAfterUncertain", Flag)),
            "ExecuteHmi" or "ExecuteUnified" or "ExecuteUnifiedHmi" or "ExecuteUnifiedHmiMetadata" or "ReleaseUnifiedReadCursor" => Exactly(reply,
                ("Value", Any), ("Notes", OptionalText), ("Failure", OptionalObject), ("MayHaveChanged", Flag),
                ("RequiresSessionReset", Flag), ("BlockReadsAfterUncertain", Flag)),
            "ExecuteLibrary" => Exactly(reply, ("Step", OptionalObject), ("Probe", OptionalObject), ("Import", OptionalObject),
                ("MayHaveChanged", Flag), ("RequiresSessionReset", Flag), ("BlockReadsAfterUncertain", Flag)),
            "ExecuteOpennessReflection" => Exactly(reply, ("Observation", OptionalObject), ("Failure", OptionalObject), ("MayHaveChanged", Flag),
                ("RequiresSessionReset", Flag), ("BlockReadsAfterUncertain", Flag)),
            "SeedReferenceHmi" => Exactly(reply, ("Message", OptionalText), ("Imported", node => node == null || Texts(node)),
                ("Failed", node => node == null || node is JsonArray failures && failures
                        .All(f => Exactly(f, ("Path", OptionalText), ("Error", OptionalText)))),
                ("Meta", OptionalObject)),
            _ => false
        };
        if (!valid) throw new IOException("Malformed hardware worker reply; session stopped: " + operation);
    }
}
