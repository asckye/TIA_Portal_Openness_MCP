using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-② (2.7.39): pure logic (no Siemens dependency) for the Startdrive option package - drive objects, offline / online
    // drive parameters (incl. BICO sources), telegrams, the drive function interface (object type, activation, function in use,
    // commissioning, hardware projection of motors / encoders, safety checksums), drive security (UMAC / DDE), technology
    // extensions, hardware modules, safety acceptance tests and the online drive domain functions.
    internal static class StartdriveLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        internal static JsonObject ParseObject(string json, string parameter)
            => (string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject) ?? throw new ArgumentException(parameter + " must be a JSON object.");

        // ---- official enum names (pinned member by member by StartdriveShapeChecks) ---------------------------------------------
        internal static readonly string[] TelegramTypes = { "MainTelegram", "SupplementaryTelegram", "AdditionalTelegram", "SafetyTelegram", "TorqueTelegram", "EdgeTelegram" };
        internal static readonly string[] Directions = { "Input", "Output" };                                  // HW.AddressIoType subset a telegram channel has
        internal static readonly string[] ActivationStates = { "Deactivate", "Activate", "DeactivateAndNotPresent" };
        internal static readonly string[] FunctionKeys = { "BasicPositioner", "TechnologyController" };
        internal static readonly string[] RotaryLinearFlags = { "Rotary", "Linear" };
        internal static readonly string[] AbsoluteIncrementalFlags = { "Absolute", "Incremental" };
        internal static readonly string[] EncoderInterfaces = { "None", "Terminal", "DSub", "DriveCliQ", "HTL", "SSI" };
        internal static readonly string[] EncoderTypes = { "NoEncoder", "Resolver", "HTLTTL", "SSIProtocoll", "SinCos", "EnDat", "HTL", "SSIProtocollAndHTLTTL", "SSIProtocollAndSinCos", "DriveCliQ" };
        internal static readonly string[] MotorTypes =
        {
            "NoMotor", "InductionMotor", "SynchronousMotor", "NoCodeNumber1LE1InductionMotor", "NoCodeNumber1LG6InductionMotor",
            "NoCodeNumber1xx1SIMOTICSFDInductionMotor", "NoCodeNumber1LA7InductionMotorNoCodeNumber", "MotorSeriesNumber1LA81PQ8StandardInduction",
            "NoCodeNumber1LA9InductionMotor", "InductionMotor1LE1", "InductionMotor1PC1", "InductionMotor1PH4", "InductionMotor1LE5", "InductionMotor1PH7",
            "InductionMotor1PH8", "NoEncoder1FG1GearedSynchronousMotor", "NoEncoder1FK7SynchronousMotor", "DriveCliqMotor", "DriveCliqMotorDataSet"
        };
        internal static readonly string[] ResetModes = { "ParameterReset", "SafetyParameterReset" };
        internal static readonly string[] FileOperations = { "None", "Overwrite" };                              // V21 SafetyAcceptanceTestReport.CreateProtocol
        internal static readonly string[] ConnectOptions = { "Default", "AllowAllModules" };                       // V20 MC.Drives.Enums / V21 Motion.ConnectOption
        internal static readonly string[] InterfaceKinds = { "actor", "sensor", "torque", "encoder" };

        // ---- drive object selection ----------------------------------------------------------------------------------------------
        // A drive object is addressed by its official DriveObjectNumber or by its position in DriveObjectContainer.DriveObjects.
        // 2.7.38 real project: DriveObject.DriveObjectNumber throws on a G120C ("Drive object number could not be retrieved"), so the
        // index is the reliable selector there; neither given means the first (and on G120 the only) drive object.
        internal sealed class DriveSelector
        {
            public ushort Number; public int Index = -1;
            public bool ByNumber => Number > 0;
            public string Label => ByNumber ? "driveObjectNumber " + Number : "driveObjectIndex " + Math.Max(Index, 0);
        }
        internal static DriveSelector ParseDriveSelector(ushort driveObjectNumber, int driveObjectIndex)
        {
            if (driveObjectIndex < -1 || driveObjectIndex > 1023) throw new ArgumentException("driveObjectIndex must be -1 (unused) or 0..1023.");
            if (driveObjectNumber > 0 && driveObjectIndex >= 0) throw new ArgumentException("Give driveObjectNumber or driveObjectIndex, not both.");
            return new DriveSelector { Number = driveObjectNumber, Index = driveObjectNumber > 0 ? -1 : Math.Max(driveObjectIndex, 0) };
        }

        // ---- parameters ----------------------------------------------------------------------------------------------------------
        internal static readonly string[] ParameterSources = { "read", "write" };                                 // ReadParameters (offline / online read view) or Parameters (writable view)
        internal sealed class ParameterSelector
        {
            public string[] Names = Array.Empty<string>();
            public (int Number, int ArrayIndex)[] Numbers = Array.Empty<(int, int)>();
            public bool Enumerate => Names.Length == 0 && Numbers.Length == 0;
        }
        // namesJson ["p1000[0]","r47"] -> Find(name); numbersJson [{"number":947,"arrayIndex":6},{"number":96}] -> Find(number, arrayIndex) with -1 for array-less parameters.
        internal static ParameterSelector ParseParameterSelector(string namesJson, string numbersJson)
        {
            var selector = new ParameterSelector();
            var names = (string.IsNullOrWhiteSpace(namesJson) ? new JsonArray() : JsonNode.Parse(namesJson) as JsonArray) ?? throw new ArgumentException("namesJson must be a JSON array of parameter names.");
            selector.Names = names.Select(n => n?.GetValue<string>() ?? "").ToArray();
            if (selector.Names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 64) || selector.Names.Distinct(StringComparer.Ordinal).Count() != selector.Names.Length) throw new ArgumentException("namesJson needs distinct nonempty parameter names such as p1000[0], r47 or p2080[0].6.");
            var numbers = (string.IsNullOrWhiteSpace(numbersJson) ? new JsonArray() : JsonNode.Parse(numbersJson) as JsonArray) ?? throw new ArgumentException("numbersJson must be a JSON array of {number, arrayIndex} objects.");
            var list = new List<(int, int)>();
            foreach (var node in numbers)
            {
                var o = node as JsonObject ?? throw new ArgumentException("numbersJson entries must be objects {number, arrayIndex}.");
                foreach (var pair in o) if (pair.Key != "number" && pair.Key != "arrayIndex") throw new ArgumentException("Unknown numbersJson key: " + pair.Key);
                if (o["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number) || number < 0 || number > 65535) throw new ArgumentException("numbersJson number must be 0..65535.");
                int arrayIndex = -1;
                if (o.ContainsKey("arrayIndex")) { if (o["arrayIndex"] is not JsonValue av || !av.TryGetValue<int>(out arrayIndex) || arrayIndex < -1 || arrayIndex > 0x7FFF) throw new ArgumentException("numbersJson arrayIndex must be -1 (no array) or 0..32767."); }
                list.Add((number, arrayIndex));
            }
            selector.Numbers = list.ToArray();
            if (selector.Names.Length + selector.Numbers.Length > 200) throw new ArgumentException("At most 200 parameters per call.");
            return selector;
        }
        internal static ParameterSelector ValidateParametersRequest(string source, string namesJson, string numbersJson, int offset, int limit)
        {
            RequireOneOf(source, ParameterSources, "source"); HardwareServicesLogic.ValidatePagination(offset, limit);
            var selector = ParseParameterSelector(namesJson, numbersJson);
            if (!selector.Enumerate && offset != 0) throw new ArgumentException("offset applies to the enumeration only (no namesJson / numbersJson).");
            return selector;
        }
        // Official parameter name grammar: p|r + number [ '[' index ']' ] [ '.' bit ]; used by the write tool to refuse fuzzy input before TIA.
        internal static bool LooksLikeParameterName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            if (name[0] != 'p' && name[0] != 'r' && name[0] != 'P' && name[0] != 'R') return false;
            int i = 1; while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i == 1) return false;
            if (i < name.Length && name[i] == '[') { int close = name.IndexOf(']', i); if (close < 0 || close == i + 1 || !name.Substring(i + 1, close - i - 1).All(char.IsDigit)) return false; i = close + 1; }
            if (i < name.Length && name[i] == '.') { if (i + 1 >= name.Length || !name.Substring(i + 1).All(char.IsDigit)) return false; i = name.Length; }
            return i == name.Length;
        }
        // valueJson for ManageStartdriveParameter: a JSON scalar, or {"bicoSource":"r19"} to wire a BICO sink to a source parameter.
        internal sealed class ParameterValue { public bool IsBico; public string BicoSource = ""; public JsonNode? Scalar; }
        internal static ParameterValue ParseParameterValue(string valueJson)
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(valueJson) ? "null" : valueJson);
            if (node is JsonObject o)
            {
                foreach (var pair in o) if (pair.Key != "bicoSource") throw new ArgumentException("valueJson object accepts only {\"bicoSource\":\"r19\"}.");
                var source = o["bicoSource"]?.GetValue<string>() ?? "";
                if (!LooksLikeParameterName(source)) throw new ArgumentException("bicoSource must be an exact parameter name such as r19 or r2050[0].");
                return new ParameterValue { IsBico = true, BicoSource = source };
            }
            if (node is JsonArray) throw new ArgumentException("valueJson must be a scalar or {\"bicoSource\":name}.");
            if (node == null) throw new ArgumentException("valueJson is required for action write.");
            return new ParameterValue { Scalar = node };
        }
        internal static void ValidateParameterWriteRequest(string parameter, string action)
        {
            if (action != "read" && action != "write") throw new ArgumentException("action must be read/write.");
            if (!LooksLikeParameterName(parameter)) throw new ArgumentException("Exact parameter name such as p1000[0], r47 or p2080[0].6 required (no fuzzy matching).");
        }

        // ---- telegrams -----------------------------------------------------------------------------------------------------------
        internal static readonly string[] TelegramActions = { "read", "check", "insert", "erase", "changeNumber", "changeSize", "connectTechnologyObject" };
        internal sealed class TelegramRequest
        {
            public string Action = "", TelegramType = "", Direction = "", SoftwarePath = "", ObjectPath = "", InterfaceKind = "", ConnectOption = "";
            public int TelegramNumber = -1, InputSize = -1, OutputSize = -1, Size = -1, SensorIndex; public bool KeepOriginalAddress, HasConnectOption;
            public bool Writes;
        }
        internal static TelegramRequest ValidateTelegramRequest(string action, string telegramType, int telegramNumber, int inputSize, int outputSize, string direction, int size, bool keepOriginalAddress,
            string softwarePath, string objectPath, string interfaceKind, int sensorIndex, string connectOption, bool dryRun)
        {
            RequireOneOf(action, TelegramActions, "action");
            var r = new TelegramRequest { Action = action, TelegramNumber = telegramNumber, InputSize = inputSize, OutputSize = outputSize, Size = size, KeepOriginalAddress = keepOriginalAddress, SensorIndex = sensorIndex };
            bool additional = action == "insert" && telegramType == "AdditionalTelegram";
            if (action != "read") r.TelegramType = RequireOneOf(telegramType, TelegramTypes, "telegramType");
            if (action == "insert" || action == "check")
            {
                if (additional || (action == "check" && telegramType == "AdditionalTelegram"))
                {
                    if (inputSize < 0 || outputSize < 0 || inputSize + outputSize == 0) throw new ArgumentException("AdditionalTelegram needs inputSize / outputSize (words, one of them may be 0).");
                }
                else if (telegramNumber < 0) throw new ArgumentException(action + " needs telegramNumber (999 = free telegram).");
            }
            if (action == "changeNumber" && telegramNumber < 0) throw new ArgumentException("changeNumber needs the new telegramNumber.");
            if (action == "changeSize")
            {
                r.Direction = RequireOneOf(direction, Directions, "direction");
                if (size < 0) throw new ArgumentException("changeSize needs size (words).");
            }
            else Refuse(direction, "direction", "applies to action changeSize only.");
            if (action == "connectTechnologyObject")
            {
                RequireText(softwarePath, "softwarePath"); RequireText(objectPath, "objectPath", 1024); EngineeringGroupOperations.Parts(objectPath);
                r.SoftwarePath = softwarePath; r.ObjectPath = objectPath; r.InterfaceKind = RequireOneOf(interfaceKind, InterfaceKinds, "interfaceKind");
                if (sensorIndex < 0 || sensorIndex > 15) throw new ArgumentException("sensorIndex 0..15 required.");
                r.HasConnectOption = !string.IsNullOrEmpty(connectOption); if (r.HasConnectOption) r.ConnectOption = RequireOneOf(connectOption, ConnectOptions, "connectOption");
            }
            else { Refuse(softwarePath, "softwarePath", "applies to action connectTechnologyObject only."); Refuse(objectPath, "objectPath", "applies to action connectTechnologyObject only."); }
            r.Writes = action != "read" && action != "check" && !dryRun;
            return r;
        }

        // ---- drive function interface --------------------------------------------------------------------------------------------
        internal static readonly string[] FunctionActions =
        {
            "read", "changeDriveObjectType", "changeActivationState", "activateFunction", "deactivateFunction", "setSIAxisType", "setMotorCode", "setSimoGearMlfb", "updateCheckSums",
            "setMotorType", "readMotorConfiguration", "projectMotorConfiguration", "setEquivalentCircuitDiagramData", "setEncoder", "readEncoderConfiguration", "setEncoderType", "projectEncoderConfiguration"
        };
        internal static readonly string[] OnlineFunctionActions = { "read", "performFactoryReset", "performRamToRomCopy", "changeActivationState" };
        internal sealed class FunctionRequest
        {
            public string Action = "", DriveObjectType = "", ActivationState = "", FunctionKey = "", RotaryLinear = "", AbsoluteIncremental = "", Mlfb = "", MotorType = "", EncoderInterface = "", EncoderType = "";
            public int MotorCode = -1, MotorDataSet = -1, DataSet, EncoderNumber = 1; public bool EquivalentCircuitDiagram, HasAbsoluteIncremental;
            public Dictionary<string, JsonNode?> Entries = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            public bool Writes;
        }
        // valueJson carries the action-specific fields; unknown keys are refused so a typo never turns into a silent no-op.
        internal static FunctionRequest ValidateFunctionRequest(string action, string valueJson, bool dryRun)
        {
            RequireOneOf(action, FunctionActions, "action");
            var o = ParseObject(valueJson, "valueJson");
            var r = new FunctionRequest { Action = action };
            string Str(string key, bool required, string[]? allowed = null)
            {
                if (!o.ContainsKey(key)) { if (required) throw new ArgumentException(action + " needs valueJson." + key + "."); return ""; }
                var s = o[key]?.GetValue<string>() ?? ""; if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException("valueJson." + key + " must be a nonempty string.");
                return allowed == null ? s : RequireOneOf(s, allowed, "valueJson." + key);
            }
            int Int(string key, bool required, int min, int max, int fallback)
            {
                if (!o.ContainsKey(key)) { if (required) throw new ArgumentException(action + " needs valueJson." + key + "."); return fallback; }
                if (o[key] is not JsonValue v || !v.TryGetValue<int>(out var i) || i < min || i > max) throw new ArgumentException("valueJson." + key + " must be an integer " + min + ".." + max + ".");
                return i;
            }
            var allowedKeys = new List<string>();
            switch (action)
            {
                case "read": break;
                case "changeDriveObjectType": allowedKeys.Add("driveObjectType"); r.DriveObjectType = Str("driveObjectType", true); break;
                case "changeActivationState": allowedKeys.Add("activationState"); r.ActivationState = Str("activationState", true, ActivationStates); break;
                case "activateFunction": case "deactivateFunction": allowedKeys.Add("functionKey"); r.FunctionKey = Str("functionKey", true, FunctionKeys); break;
                case "setSIAxisType": allowedKeys.Add("rotaryLinear"); r.RotaryLinear = Str("rotaryLinear", true, RotaryLinearFlags); break;
                case "setMotorCode": allowedKeys.AddRange(new[] { "motorCode", "motorDataSet" }); r.MotorCode = Int("motorCode", true, 0, int.MaxValue, -1); r.MotorDataSet = Int("motorDataSet", false, 0, 65535, 0); break;
                case "setSimoGearMlfb": allowedKeys.Add("mlfb"); r.Mlfb = Str("mlfb", true); break;
                case "updateCheckSums": break;
                case "setMotorType": allowedKeys.AddRange(new[] { "motorType", "dataSet" }); r.MotorType = Str("motorType", true, MotorTypes); r.DataSet = Int("dataSet", false, 0, 65535, 0); break;
                case "readMotorConfiguration": case "setEquivalentCircuitDiagramData": case "projectMotorConfiguration":
                    allowedKeys.AddRange(new[] { "dataSet", "entries", "equivalentCircuitDiagram" }); r.DataSet = Int("dataSet", false, 0, 65535, 0);
                    if (action == "setEquivalentCircuitDiagramData") { if (o["equivalentCircuitDiagram"] is not JsonValue b || !b.TryGetValue<bool>(out r.EquivalentCircuitDiagram)) throw new ArgumentException("setEquivalentCircuitDiagramData needs valueJson.equivalentCircuitDiagram (bool)."); }
                    else Refuse(o.ContainsKey("equivalentCircuitDiagram") ? "x" : "", "valueJson.equivalentCircuitDiagram", "applies to setEquivalentCircuitDiagramData only.");
                    if (action == "projectMotorConfiguration") ReadEntries(o, r); else if (o.ContainsKey("entries")) throw new ArgumentException("valueJson.entries applies to projectMotorConfiguration / projectEncoderConfiguration only.");
                    break;
                case "setEncoder":
                    allowedKeys.AddRange(new[] { "encoderInterface", "encoderType", "absoluteIncremental", "rotaryLinear", "encoderNumber" });
                    r.EncoderInterface = Str("encoderInterface", true, EncoderInterfaces); r.EncoderType = Str("encoderType", true, EncoderTypes);
                    r.AbsoluteIncremental = Str("absoluteIncremental", true, AbsoluteIncrementalFlags); r.RotaryLinear = Str("rotaryLinear", true, RotaryLinearFlags); r.EncoderNumber = Int("encoderNumber", false, 0, 65535, 1);
                    break;
                case "readEncoderConfiguration": case "projectEncoderConfiguration":
                    allowedKeys.AddRange(new[] { "encoderNumber", "entries" }); r.EncoderNumber = Int("encoderNumber", false, 0, 65535, 1);
                    if (action == "projectEncoderConfiguration") ReadEntries(o, r); else if (o.ContainsKey("entries")) throw new ArgumentException("valueJson.entries applies to projectEncoderConfiguration only.");
                    break;
                case "setEncoderType":
                    allowedKeys.AddRange(new[] { "encoderNumber", "rotaryLinear", "absoluteIncremental" }); r.EncoderNumber = Int("encoderNumber", false, 0, 65535, 1);
                    r.RotaryLinear = Str("rotaryLinear", true, RotaryLinearFlags); r.HasAbsoluteIncremental = o.ContainsKey("absoluteIncremental"); if (r.HasAbsoluteIncremental) r.AbsoluteIncremental = Str("absoluteIncremental", true, AbsoluteIncrementalFlags);
                    break;
            }
            foreach (var pair in o) if (!allowedKeys.Contains(pair.Key)) throw new ArgumentException("valueJson key " + pair.Key + " does not apply to action " + action + ".");
            r.Writes = action != "read" && action != "readMotorConfiguration" && action != "readEncoderConfiguration" && !dryRun;
            return r;
        }
        private static void ReadEntries(JsonObject o, FunctionRequest r)
        {
            var entries = o["entries"] as JsonObject ?? throw new ArgumentException("valueJson.entries must be an object {\"p305\": 20, ...} of configuration entry names (or numbers) and scalar values.");
            if (entries.Count == 0 || entries.Count > 200) throw new ArgumentException("valueJson.entries needs 1..200 entries.");
            foreach (var pair in entries)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("valueJson.entries keys must be entry names such as p305 or numbers such as 305.");
                if (pair.Value is JsonObject || pair.Value is JsonArray) throw new ArgumentException("valueJson.entries values must be scalars.");
                r.Entries[pair.Key] = pair.Value;
            }
        }
        internal static string ValidateOnlineFunctionRequest(string action, string resetMode, string activationState, bool confirmOnline)
        {
            RequireOneOf(action, OnlineFunctionActions, "action");
            if (action == "performFactoryReset") RequireOneOf(resetMode, ResetModes, "resetMode"); else Refuse(resetMode, "resetMode", "applies to performFactoryReset only.");
            if (action == "changeActivationState") RequireOneOf(activationState, ActivationStates, "activationState"); else Refuse(activationState, "activationState", "applies to changeActivationState only.");
            if (action != "read" && !confirmOnline) throw new ArgumentException("confirmOnline=true is required: " + action + " changes the connected drive, not the project.");
            return action;
        }

        // ---- security ------------------------------------------------------------------------------------------------------------
        internal static readonly string[] SecurityActions = { "read", "activateUmac", "deactivateUmac", "activateEncryption", "deactivateEncryption" };
        internal static bool ValidateSecurityRequest(string action, string password, bool dryRun)
        {
            RequireOneOf(action, SecurityActions, "action");
            if (action == "activateEncryption" || action == "deactivateEncryption") { if (string.IsNullOrEmpty(password)) throw new ArgumentException(action + " needs password (the DDE password; never logged)."); }
            else if (!string.IsNullOrEmpty(password)) throw new ArgumentException("password applies to activateEncryption / deactivateEncryption only.");
            return action != "read" && !dryRun;
        }

        // ---- technology extensions -----------------------------------------------------------------------------------------------
        internal static readonly string[] TechnologyExtensionActions = { "read", "activate", "deactivate", "readPackages", "install", "installAndGetIdentifier", "uninstall" };
        internal static bool ValidateTechnologyExtensionRequest(string action, string identifier, string filePath, bool confirmUninstallInUse, bool dryRun)
        {
            RequireOneOf(action, TechnologyExtensionActions, "action");
            bool portal = action == "readPackages" || action == "install" || action == "installAndGetIdentifier" || action == "uninstall";
            if (action == "activate" || action == "deactivate" || action == "uninstall") RequireText(identifier, "identifier"); else Refuse(identifier, "identifier", "applies to activate / deactivate / uninstall only.");
            if (action == "install" || action == "installAndGetIdentifier")
            {
                RequireText(filePath, "filePath", 1024);
                if (!string.Equals(Path.GetExtension(filePath), ".tec", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("filePath must be a .tec technology extension package.");
            }
            else Refuse(filePath, "filePath", "applies to install / installAndGetIdentifier only.");
            if (action == "uninstall" && !confirmUninstallInUse && !dryRun) { /* allowed: TIA refuses itself when the package is in use */ }
            return portal ? action != "readPackages" && !dryRun : action != "read" && !dryRun;
        }
        internal static bool IsPortalScopedExtensionAction(string action) => action == "readPackages" || action == "install" || action == "installAndGetIdentifier" || action == "uninstall";

        // ---- hardware modules (V21 DriveItemHardwareModule) + module access point --------------------------------------------------
        internal static readonly string[] HardwareModuleActions = { "read", "changeType", "setPositionNumber" };
        internal static bool ValidateHardwareModuleRequest(string action, string typeIdentifier, int positionNumber, bool dryRun)
        {
            RequireOneOf(action, HardwareModuleActions, "action");
            if (action == "changeType") { RequireText(typeIdentifier, "typeIdentifier"); if (!typeIdentifier.StartsWith("OrderNumber:", StringComparison.Ordinal) && !typeIdentifier.StartsWith("GSD:", StringComparison.Ordinal) && !typeIdentifier.StartsWith("System:", StringComparison.Ordinal)) throw new ArgumentException("typeIdentifier must be an official TypeIdentifier such as OrderNumber:6SL3120-2TE21-8Axx//10014."); }
            else Refuse(typeIdentifier, "typeIdentifier", "applies to action changeType only.");
            if (action == "setPositionNumber") { if (positionNumber < 0) throw new ArgumentException("setPositionNumber needs positionNumber >= 0."); }
            else if (positionNumber >= 0) throw new ArgumentException("positionNumber applies to action setPositionNumber only.");
            return action != "read" && !dryRun;
        }

        // ---- safety acceptance test (V21) ----------------------------------------------------------------------------------------
        internal static readonly string[] SafetyTestActions = { "read", "setActive", "resetTestFunctions", "createProtocol" };
        internal static bool ValidateSafetyTestRequest(string action, string identifier, string filePath, string fileOperation, bool dryRun)
        {
            RequireOneOf(action, SafetyTestActions, "action");
            if (action == "setActive") RequireText(identifier, "identifier"); else Refuse(identifier, "identifier", "applies to action setActive only.");
            if (action == "createProtocol") { RequireText(filePath, "filePath", 1024); RequireOneOf(fileOperation, FileOperations, "fileOperation"); }
            else { Refuse(filePath, "filePath", "applies to action createProtocol only."); Refuse(fileOperation, "fileOperation", "applies to action createProtocol only."); }
            return action != "read" && !dryRun;
        }
    }
}
