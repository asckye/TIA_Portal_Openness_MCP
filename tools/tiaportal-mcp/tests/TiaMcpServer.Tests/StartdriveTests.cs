using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-② (2.7.39) pure logic: Startdrive drive-object selectors, parameter selectors and BICO values, telegram requests,
    // drive function requests (valueJson per action), security / technology extension / hardware module / safety test / online gates.
    internal static class StartdriveTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string tec = Path.Combine(temp, "TRCDATA_V1_1_0_1.tec");

            // ---- enum catalogues (pinned member by member by StartdriveShapeChecks) ----
            check(StartdriveLogic.TelegramTypes.Length == 6 && StartdriveLogic.MotorTypes.Length == 19 && StartdriveLogic.EncoderTypes.Length == 10 && StartdriveLogic.EncoderInterfaces.Length == 6
                && StartdriveLogic.ActivationStates.SequenceEqual(new[] { "Deactivate", "Activate", "DeactivateAndNotPresent" }) && StartdriveLogic.FunctionKeys.Length == 2 && StartdriveLogic.ResetModes.Length == 2 && StartdriveLogic.ConnectOptions.SequenceEqual(new[] { "Default", "AllowAllModules" }), "startdrive: enum catalogues");

            // ---- drive selector ----
            var byIndex = StartdriveLogic.ParseDriveSelector(0, -1); var byNumber = StartdriveLogic.ParseDriveSelector(3, -1); var explicitIndex = StartdriveLogic.ParseDriveSelector(0, 2);
            check(!byIndex.ByNumber && byIndex.Index == 0 && byNumber.ByNumber && byNumber.Number == 3 && byNumber.Index == -1 && !explicitIndex.ByNumber && explicitIndex.Index == 2, "startdrive selector: nothing = index 0, number wins, explicit index kept");
            check(byNumber.Label == "driveObjectNumber 3" && explicitIndex.Label == "driveObjectIndex 2" && byIndex.Label == "driveObjectIndex 0", "startdrive selector: labels");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseDriveSelector(1, 0)), "startdrive selector: number and index together refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseDriveSelector(0, -2)), "startdrive selector: index -2 refused");

            // ---- parameter selectors ----
            var sel = StartdriveLogic.ParseParameterSelector("[\"p1000[0]\",\"r47\"]", "[{\"number\":947,\"arrayIndex\":6},{\"number\":96}]");
            check(sel.Names.SequenceEqual(new[] { "p1000[0]", "r47" }) && sel.Numbers.SequenceEqual(new[] { (947, 6), (96, -1) }) && !sel.Enumerate, "startdrive parameters: names + numbers (array-less = -1)");
            check(StartdriveLogic.ParseParameterSelector("", "").Enumerate && StartdriveLogic.ParseParameterSelector("[]", "[]").Enumerate, "startdrive parameters: empty selectors enumerate");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseParameterSelector("[\"p1\",\"p1\"]", "[]")), "startdrive parameters: duplicate names refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseParameterSelector("[]", "[{\"number\":-1}]")), "startdrive parameters: negative number refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseParameterSelector("[]", "[{\"number\":1,\"bits\":2}]")), "startdrive parameters: unknown numbersJson key refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateParametersRequest("read", "[\"r47\"]", "[]", 5, 10)), "startdrive parameters: offset with explicit names refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateParametersRequest("online", "[]", "[]", 0, 10)), "startdrive parameters: unknown source refused");
            check(StartdriveLogic.LooksLikeParameterName("p1000[0]") && StartdriveLogic.LooksLikeParameterName("r47") && StartdriveLogic.LooksLikeParameterName("p2080[0].6") && StartdriveLogic.LooksLikeParameterName("r2139.13"), "startdrive parameters: official name grammar accepted");
            check(!StartdriveLogic.LooksLikeParameterName("p") && !StartdriveLogic.LooksLikeParameterName("x100") && !StartdriveLogic.LooksLikeParameterName("p100[") && !StartdriveLogic.LooksLikeParameterName("p100.") && !StartdriveLogic.LooksLikeParameterName("p100 "), "startdrive parameters: fuzzy names refused");
            var scalar = StartdriveLogic.ParseParameterValue("60"); var bico = StartdriveLogic.ParseParameterValue("{\"bicoSource\":\"r19\"}");
            check(!scalar.IsBico && scalar.Scalar!.GetValue<int>() == 60 && bico.IsBico && bico.BicoSource == "r19", "startdrive parameters: scalar and BICO values");
            check(Fails<ArgumentException>(() => StartdriveLogic.ParseParameterValue("{\"source\":\"r19\"}")) && Fails<ArgumentException>(() => StartdriveLogic.ParseParameterValue("[1]")) && Fails<ArgumentException>(() => StartdriveLogic.ParseParameterValue("null")), "startdrive parameters: wrong value shapes refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateParameterWriteRequest("p1000", "delete")) && Fails<ArgumentException>(() => StartdriveLogic.ValidateParameterWriteRequest("speed", "read")), "startdrive parameters: write request gates");

            // ---- telegrams ----
            StartdriveLogic.TelegramRequest T(string action, string type = "", int number = -1, int inSize = -1, int outSize = -1, string direction = "", int size = -1, string sw = "", string obj = "", string kind = "", string option = "", bool dryRun = true)
                => StartdriveLogic.ValidateTelegramRequest(action, type, number, inSize, outSize, direction, size, true, sw, obj, kind, 0, option, dryRun);
            check(!T("read").Writes && !T("check", "MainTelegram", 1).Writes && !T("insert", "MainTelegram", 1).Writes && T("insert", "MainTelegram", 1, dryRun: false).Writes, "startdrive telegrams: read / check / preview never write");
            check(T("insert", "AdditionalTelegram", inSize: 2, outSize: 4, dryRun: false).Writes && T("erase", "TorqueTelegram", dryRun: false).Writes && T("changeNumber", "MainTelegram", 3, dryRun: false).Writes, "startdrive telegrams: insert additional / erase / changeNumber");
            var cs = T("changeSize", "MainTelegram", direction: "Input", size: 6, dryRun: false); check(cs.Writes && cs.Direction == "Input" && cs.Size == 6, "startdrive telegrams: changeSize with direction");
            var ct = T("connectTechnologyObject", "MainTelegram", sw: "PLC_1", obj: "Axis_1", kind: "actor", option: "AllowAllModules", dryRun: false); check(ct.Writes && ct.HasConnectOption && ct.ConnectOption == "AllowAllModules" && ct.InterfaceKind == "actor", "startdrive telegrams: connectTechnologyObject with option");
            check(!T("connectTechnologyObject", "MainTelegram", sw: "PLC_1", obj: "Axis_1", kind: "encoder").HasConnectOption, "startdrive telegrams: connect without option");
            check(Fails<ArgumentException>(() => T("insert", "MainTelegram")), "startdrive telegrams: insert without number refused");
            check(Fails<ArgumentException>(() => T("insert", "AdditionalTelegram")), "startdrive telegrams: additional without sizes refused");
            check(Fails<ArgumentException>(() => T("changeSize", "MainTelegram", size: 4)), "startdrive telegrams: changeSize without direction refused");
            check(Fails<ArgumentException>(() => T("read", direction: "Input")), "startdrive telegrams: direction on read refused");
            check(Fails<ArgumentException>(() => T("erase", "PkwTelegram")), "startdrive telegrams: unknown type refused");
            check(Fails<ArgumentException>(() => T("connectTechnologyObject", "MainTelegram", sw: "PLC_1", obj: "Axis_1", kind: "spindle")), "startdrive telegrams: unknown interface kind refused");
            check(Fails<ArgumentException>(() => T("erase", "MainTelegram", sw: "PLC_1")), "startdrive telegrams: softwarePath outside connect refused");

            // ---- drive functions ----
            StartdriveLogic.FunctionRequest F(string action, string json = "{}", bool dryRun = true) => StartdriveLogic.ValidateFunctionRequest(action, json, dryRun);
            check(!F("read").Writes && !F("readMotorConfiguration", "{\"dataSet\":1}", false).Writes && F("updateCheckSums", dryRun: false).Writes, "startdrive functions: read never writes, updateCheckSums writes");
            var t = F("changeDriveObjectType", "{\"driveObjectType\":\"Universal (vector)\"}", false); check(t.Writes && t.DriveObjectType == "Universal (vector)", "startdrive functions: changeDriveObjectType");
            check(F("changeActivationState", "{\"activationState\":\"Deactivate\"}").ActivationState == "Deactivate" && F("activateFunction", "{\"functionKey\":\"BasicPositioner\"}").FunctionKey == "BasicPositioner" && F("setSIAxisType", "{\"rotaryLinear\":\"Linear\"}").RotaryLinear == "Linear", "startdrive functions: enum fields");
            var mc = F("setMotorCode", "{\"motorCode\":12345}"); check(mc.MotorCode == 12345 && mc.MotorDataSet == 0 && F("setSimoGearMlfb", "{\"mlfb\":\"2KJ8001-2EG20-4DG1-D0X\"}").Mlfb.StartsWith("2KJ", StringComparison.Ordinal), "startdrive functions: commissioning fields");
            var mt = F("setMotorType", "{\"motorType\":\"InductionMotor\",\"dataSet\":0}"); check(mt.MotorType == "InductionMotor" && mt.DataSet == 0, "startdrive functions: setMotorType");
            var pm = F("projectMotorConfiguration", "{\"dataSet\":0,\"entries\":{\"p305\":20,\"307\":30}}", false); check(pm.Writes && pm.Entries.Count == 2 && pm.Entries["307"]!.GetValue<int>() == 30, "startdrive functions: projectMotorConfiguration entries by name or number");
            var se = F("setEncoder", "{\"encoderInterface\":\"Terminal\",\"encoderType\":\"HTLTTL\",\"absoluteIncremental\":\"Incremental\",\"rotaryLinear\":\"Rotary\",\"encoderNumber\":1}"); check(se.EncoderInterface == "Terminal" && se.EncoderType == "HTLTTL" && se.EncoderNumber == 1, "startdrive functions: setEncoder");
            var st = F("setEncoderType", "{\"rotaryLinear\":\"Linear\",\"absoluteIncremental\":\"Incremental\"}"); check(st.HasAbsoluteIncremental && st.AbsoluteIncremental == "Incremental" && !F("setEncoderType", "{\"rotaryLinear\":\"Rotary\"}").HasAbsoluteIncremental, "startdrive functions: setEncoderType overloads");
            check(F("setEquivalentCircuitDiagramData", "{\"equivalentCircuitDiagram\":true}").EquivalentCircuitDiagram, "startdrive functions: equivalent circuit diagram flag");
            check(Fails<ArgumentException>(() => F("changeActivationState", "{\"activationState\":\"On\"}")), "startdrive functions: unknown activation state refused");
            check(Fails<ArgumentException>(() => F("setMotorType", "{\"motorType\":\"Stepper\"}")), "startdrive functions: unknown motor type refused");
            check(Fails<ArgumentException>(() => F("read", "{\"dataSet\":1}")), "startdrive functions: keys outside the action refused");
            check(Fails<ArgumentException>(() => F("projectEncoderConfiguration", "{\"entries\":{}}")), "startdrive functions: empty entries refused");
            check(Fails<ArgumentException>(() => F("projectMotorConfiguration", "{\"entries\":{\"p305\":{\"v\":1}}}")), "startdrive functions: non-scalar entry refused");
            check(Fails<ArgumentException>(() => F("setEquivalentCircuitDiagramData", "{}")), "startdrive functions: missing bool refused");
            check(Fails<ArgumentException>(() => F("resetToFactory")), "startdrive functions: unknown action refused");

            // ---- online functions ----
            check(StartdriveLogic.ValidateOnlineFunctionRequest("read", "", "", false) == "read" && StartdriveLogic.ValidateOnlineFunctionRequest("performFactoryReset", "SafetyParameterReset", "", true) == "performFactoryReset", "startdrive online: read and confirmed reset");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateOnlineFunctionRequest("performRamToRomCopy", "", "", false)), "startdrive online: write without confirmOnline refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateOnlineFunctionRequest("performFactoryReset", "", "", true)), "startdrive online: factory reset without resetMode refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateOnlineFunctionRequest("read", "ParameterReset", "", false)), "startdrive online: resetMode on read refused");

            // ---- security ----
            check(!StartdriveLogic.ValidateSecurityRequest("read", "", true) && StartdriveLogic.ValidateSecurityRequest("activateUmac", "", false) && StartdriveLogic.ValidateSecurityRequest("activateEncryption", "s3cret", false) && !StartdriveLogic.ValidateSecurityRequest("deactivateEncryption", "s3cret", true), "startdrive security: gates");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateSecurityRequest("activateEncryption", "", false)) && Fails<ArgumentException>(() => StartdriveLogic.ValidateSecurityRequest("activateUmac", "pw", false)), "startdrive security: password only for encryption");

            // ---- technology extensions ----
            check(!StartdriveLogic.ValidateTechnologyExtensionRequest("read", "", "", false, true) && StartdriveLogic.ValidateTechnologyExtensionRequest("activate", "TRCDATA/1100802", "", false, false) && StartdriveLogic.ValidateTechnologyExtensionRequest("install", "", tec, false, false) && !StartdriveLogic.ValidateTechnologyExtensionRequest("readPackages", "", "", false, false), "startdrive extensions: gates");
            check(StartdriveLogic.IsPortalScopedExtensionAction("uninstall") && !StartdriveLogic.IsPortalScopedExtensionAction("activate"), "startdrive extensions: scope split");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateTechnologyExtensionRequest("install", "", Path.Combine(temp, "x.zip"), false, false)), "startdrive extensions: non-.tec refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateTechnologyExtensionRequest("activate", "", "", false, false)), "startdrive extensions: activate without identifier refused");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateTechnologyExtensionRequest("read", "X", "", false, false)), "startdrive extensions: identifier on read refused");

            // ---- hardware module / safety test ----
            check(!StartdriveLogic.ValidateHardwareModuleRequest("read", "", -1, true) && StartdriveLogic.ValidateHardwareModuleRequest("changeType", "OrderNumber:6SL3120-2TE21-8Axx//10014", -1, false) && StartdriveLogic.ValidateHardwareModuleRequest("setPositionNumber", "", 2, false), "startdrive hardware module: gates");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateHardwareModuleRequest("changeType", "6SL3120", -1, false)) && Fails<ArgumentException>(() => StartdriveLogic.ValidateHardwareModuleRequest("read", "", 1, true)), "startdrive hardware module: bad type identifier / position on read refused");
            check(!StartdriveLogic.ValidateSafetyTestRequest("read", "", "", "", true) && StartdriveLogic.ValidateSafetyTestRequest("setActive", "SLS", "", "", false) && StartdriveLogic.ValidateSafetyTestRequest("createProtocol", "", Path.Combine(temp, "protocol.pdf"), "Overwrite", false) && StartdriveLogic.ValidateSafetyTestRequest("resetTestFunctions", "", "", "", false), "startdrive safety test: gates");
            check(Fails<ArgumentException>(() => StartdriveLogic.ValidateSafetyTestRequest("createProtocol", "", Path.Combine(temp, "p.pdf"), "", false)) && Fails<ArgumentException>(() => StartdriveLogic.ValidateSafetyTestRequest("setActive", "", "", "", false)), "startdrive safety test: missing fileOperation / identifier refused");
        }
    }
}
