using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // 2.7.53 (real machine, 项目1): TIA V21 refuses the first hardware download of an S7-1500 FW 2.9 CPU whose access level is above
        // "Full access" without a full-access password, or whose confidential PLC configuration data has no password; nothing in the
        // roster could change either. Official pages "Access level setting" and "Managing PLC Master Secret in PLCs": both live as
        // HW features on the CPU DeviceItem (PlcAccessLevelProvider / PlcMasterSecretConfigurator). Passwords go in as SecureString and
        // are never echoed; the readable state (access level enum, MasterSecretConfiguration enum) is read back after every change.
        public ResponseMessage ManagePlcProtection(string devicePathJson, string itemPathJson = "[]", string action = "read", string accessLevel = "",
            string password = "", string newPassword = "", bool confirmChange = false, bool dryRun = true)
            => RunHmiStepTool("ManagePlcProtection", meta => {
                var (act, level) = PlcProtectionLogic.Validate(action, accessLevel, password, newPassword);
                var cpu = ResolveCpuItem(devicePathJson, itemPathJson, meta);
                meta["action"] = act; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                var accessProvider = cpu.GetService<PlcAccessLevelProvider>();
                var secretProvider = cpu.GetService<PlcMasterSecretConfigurator>();
                var accessControl = cpu.GetService<PlcAccessControlConfigurationProvider>();
                meta["before"] = ReadProtectionState(accessProvider, secretProvider, accessControl);
                if (act == "read") return "PLC protection read (access level, master secret state, access control); no modification.";

                if (PlcProtectionLogic.LevelActions.Contains(act) && accessProvider == null)
                    throw new NotSupportedException("PlcAccessLevelProvider is not available on this device item (not an S7-1200/1500 CPU?).");
                if (PlcProtectionLogic.MasterSecretActions.Contains(act) && secretProvider == null)
                    throw new NotSupportedException("PlcMasterSecretConfigurator is not available on this device item (needs an S7-1500 FW >= 2.9 / S7-1200 FW >= 4.5 CPU).");
                if (act == "setAccessPassword" || act == "resetAccessPassword")
                {
                    var current = accessProvider!.PlcProtectionAccessLevel.ToString();
                    var warning = PlcProtectionLogic.PasswordLevelWarning(current, level);
                    if (warning != null) meta["warning"] = warning;
                }
                if (act == "setAccessLevel" && PlcProtectionLogic.NeedsFullAccessPassword(level))
                    meta["note"] = "TIA's hardware compile requires the FullAccess password once the level is " + level + " - set it with action setAccessPassword accessLevel FullAccess.";
                meta["plan"] = DescribeProtectionPlan(act, level);
                if (dryRun) return "Preview: " + meta["plan"] + " Set dryRun=false and confirmChange=true to execute.";
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);

                using var access = AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                using var secure = string.IsNullOrEmpty(password) ? null : ProjectSecurityLogic.Secure(password);
                using var secureNew = string.IsNullOrEmpty(newPassword) ? null : ProjectSecurityLogic.Secure(newPassword);
                switch (act)
                {
                    case "setAccessLevel":
                        accessProvider!.PlcProtectionAccessLevel = (PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level);
                        break;
                    case "setAccessPassword":
                        accessProvider!.SetPassword((PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level), secure!);
                        break;
                    case "resetAccessPassword":
                        accessProvider!.ResetPassword((PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level));
                        break;
                    case "protectMasterSecret":
                        secretProvider!.Protect(secure!);
                        break;
                    case "changeMasterSecret":
                        secretProvider!.ChangePassword(secure!, secureNew!);
                        break;
                    case "unprotectMasterSecret":
                        if (secure != null) secretProvider!.Unprotect(secure); else secretProvider!.Unprotect();
                        break;
                    case "resetMasterSecret":
                        secretProvider!.Reset();
                        break;
                    case "protectAllConfiguration":
#if TIA_V20
                        throw new NotSupportedException("ProtectAllPlcConfiguration / ProtectAllPlcConfigurationWithPassword exist in the V21 PublicAPI only.");
#else
                        if (secure != null) secretProvider!.ProtectAllPlcConfigurationWithPassword(secure); else secretProvider!.ProtectAllPlcConfiguration();
                        break;
#endif
                    case "unprotectAllConfiguration":
#if TIA_V20
                        throw new NotSupportedException("UnprotectAllPlcConfiguration exists in the V21 PublicAPI only.");
#else
                        secretProvider!.UnprotectAllPlcConfiguration();
                        break;
#endif
                }
                meta["apiCallSuccess"] = true;
                var after = ReadProtectionState(accessProvider, secretProvider, accessControl);
                meta["after"] = after;
                if (act == "setAccessLevel")
                {
                    var readback = after["accessLevel"]?.ToString() ?? "";
                    meta["readbackVerified"] = string.Equals(readback, level, StringComparison.Ordinal);
                    if (!meta["readbackVerified"]!.GetValue<bool>()) throw new InvalidOperationException("Access level readback is '" + readback + "', not '" + level + "'.");
                }
                else if (PlcProtectionLogic.MasterSecretActions.Contains(act) && act != "changeMasterSecret")
                {
                    var state = after["masterSecret"]?.ToString() ?? "";
                    var expected = PlcProtectionLogic.ExpectedMasterSecretStates(act, !string.IsNullOrEmpty(password));
                    meta["expectedMasterSecret"] = string.Join("|", expected);
                    meta["readbackVerified"] = expected.Contains(state, StringComparer.Ordinal);
                    if (!expected.Contains(state, StringComparer.Ordinal)) throw new InvalidOperationException("MasterSecretConfiguration read back as '" + state + "', expected " + string.Join(" / ", expected) + ".");
                }
                else meta["readbackVerified"] = "password actions have no readable state; TIA raised no exception";
                return "PLC protection " + act + " executed on '" + cpu.Name + "'; state read back in meta.after. Hardware not compiled, project not saved.";
            });

        private DeviceItem ResolveCpuItem(string devicePathJson, string itemPathJson, JsonObject meta)
        {
            var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
            if (owner is DeviceItem given && given.Classification == DeviceItemClassifications.CPU) { meta["cpu"] = given.Name; return given; }
            // An empty item path (or the device / rail) resolves to the CPU item of the station, the same object the TIA UI edits.
            var cpu = FindCpuItem(owner) ?? throw new InvalidOperationException("No CPU device item found under '" + owner.Name + "'; give the CPU item path (e.g. [\"导轨_0\",\"PLC_1\"]).");
            meta["cpu"] = cpu.Name;
            return cpu;
        }

        private static DeviceItem? FindCpuItem(HardwareObject owner)
        {
            foreach (var item in owner.DeviceItems)
            {
                if (item.Classification == DeviceItemClassifications.CPU) return item;
                var nested = FindCpuItem(item);
                if (nested != null) return nested;
            }
            return null;
        }

        private static JsonObject ReadProtectionState(PlcAccessLevelProvider? access, PlcMasterSecretConfigurator? secret, PlcAccessControlConfigurationProvider? control)
        {
            var state = new JsonObject();
            state["accessLevel"] = access == null ? null : access.PlcProtectionAccessLevel.ToString();
            state["accessLevelProviderPresent"] = access != null;
            state["masterSecret"] = secret == null ? null : secret.MasterSecretConfiguration.ToString();
            state["masterSecretConfiguratorPresent"] = secret != null;
            if (control != null)
            {
                try { state["accessControl"] = control.PlcAccessControlConfiguration.ToString(); } catch (Exception ex) { state["accessControlError"] = ex.Message; }
                try { state["umcServerAddress"] = control.UmcServerAddress; } catch (Exception) { /* not configured */ }
            }
            state["accessControlProviderPresent"] = control != null;
            state["meaning"] = "masterSecret: None = 'Protect confidential PLC configuration data' unchecked; WithoutPassword = checked without a password (TIA's hardware compile refuses the download); WithPassword / WithPasswordAllDataProtection = password configured.";
            return state;
        }

        private static string DescribeProtectionPlan(string action, string level) => action switch
        {
            "setAccessLevel" => "set PlcAccessLevelProvider.PlcProtectionAccessLevel = " + level + ".",
            "setAccessPassword" => "PlcAccessLevelProvider.SetPassword(" + level + ", <password>).",
            "resetAccessPassword" => "PlcAccessLevelProvider.ResetPassword(" + level + ").",
            "protectMasterSecret" => "PlcMasterSecretConfigurator.Protect(<password>) - configures the password for confidential PLC configuration data.",
            "changeMasterSecret" => "PlcMasterSecretConfigurator.ChangePassword(<password>, <newPassword>).",
            "unprotectMasterSecret" => "PlcMasterSecretConfigurator.Unprotect(<password when configured>) - unchecks the protection.",
            "resetMasterSecret" => "PlcMasterSecretConfigurator.Reset() - removes the master secret; certificates encrypted with it are lost.",
            "protectAllConfiguration" => "PlcMasterSecretConfigurator.ProtectAllPlcConfiguration[WithPassword] - 'protect all PLC configuration data'.",
            "unprotectAllConfiguration" => "PlcMasterSecretConfigurator.UnprotectAllPlcConfiguration().",
            _ => "read only."
        };

        // 2.7.53: the hardware compile TIA runs before a download ("硬件配置编译完成，但出现错误") had no tool of its own - CompileSoftware
        // only compiles the program. ICompilable on the Device (or a given item) is what the TIA UI's "Compile > Hardware" does.
        public ResponseMessage CompileDevice(string devicePathJson, string itemPathJson = "[]")
            => RunHmiStepTool("CompileDevice", meta => {
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["target"] = owner.Name; meta["targetType"] = owner.GetType().Name;
                var compilable = ServiceProvider(owner).GetService<ICompilable>()
                    ?? throw new NotSupportedException("ICompilable is not available on '" + owner.Name + "'.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                CompilerResult result = compilable.Compile();
                meta["compileElapsedMs"] = watch.ElapsedMilliseconds;
                meta["apiCallSuccess"] = true;
                var collected = McpServer.CollectCompilerMessages(result.Messages);
                foreach (var kv in collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount)) meta[kv.Key] = kv.Value?.DeepClone();
                meta["errors"] = new JsonArray(collected.Errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
                meta["warnings"] = new JsonArray(collected.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
                meta["success"] = result.State != CompilerResultState.Error;
                meta["operationSuccess"] = result.State != CompilerResultState.Error;
                return "Hardware compile of '" + owner.Name + "' finished: " + result.State + " (errors " + result.ErrorCount + ", warnings " + result.WarningCount + "). Project not saved.";
            });
    }
}
