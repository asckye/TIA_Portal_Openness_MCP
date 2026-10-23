using System;
using TiaMcpServer.Siemens;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
#if SECURITY_WATCH_WEB
using Siemens.Engineering.SW.WatchAndForceTables;
#endif
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters.Native.Security
{
    internal sealed class HardwareSecurityAdapter
    {
        private readonly SecuritySession _session;
        private readonly HardwareSecurityRequest request;
        public HardwareSecurityAdapter(SecuritySession session, HardwareSecurityRequest request) { _session = session; this.request = request; }
#if SECURITY_WATCH_WEB
        private static HmiObject ReadAccessRule(object rule, string tableProperty)
        {
            var row = HmiScalar.Read(rule);
            row["tableName"] = rule switch { WatchTableAccessRule w => w.WatchTable?.Name, ForceTableAccessRule f => f.ForceTable?.Name, _ => LinkName(rule, tableProperty) };
            row["ruleClass"] = rule.GetType().Name;
            return row;
        }

        public SecurityResult ManageWatchForceTableWebAccess(string[] devicePathJson, string[] itemPathJson, string action = "read", string softwarePath = "",
            string tableKind = "watch", string tablePath = "", string access = "Read", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageWatchForceTableWebAccess", meta => {
                SecurityCatalog.RequireOneOf(action, new[] { "read", "assign", "unassign" }, "action");
                SecurityCatalog.RequireOneOf(tableKind, new[] { "watch", "force" }, "tableKind");
                bool writing = action != "read" && !dryRun;
                if (action != "read") _session.Validate("confirmation");
                using var exclusive = writing ? _session.AcquireHmiEditAccess() : null;
                var item = _session.ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact CPU DeviceItem path required.");
                var manager = item.GetService<WatchAndForceTableAccessManager>() ?? throw new NotSupportedException("WatchAndForceTableAccessManager unavailable on this DeviceItem.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["watchTableRules"] = new HmiArray(manager.WatchtableAccessRules.Select(r => (object?)ReadAccessRule(r, "WatchTable")).ToArray());
                meta["forceTableRules"] = new HmiArray(manager.ForcetableAccessRules.Select(r => (object?)ReadAccessRule(r, "ForceTable")).ToArray());
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                if (action == "read") return "Web-server watch/force table access rules read; no change.";
                var requested = (WatchAndForceTableAccess)Enum.Parse(typeof(WatchAndForceTableAccess), SecurityCatalog.RequireOneOf(access, new[] { "None", "Read", "Write" }, "access"));
                var plc = _session.ExactPlcForEngineering(softwarePath, writing);
                var parts = HardwareGroupOperations.Parts(tablePath);
                var group = HardwareGroupOperations.Group(plc.WatchAndForceTableGroup, string.Join("/", parts.Take(parts.Length - 1)));
                var table = HardwareGroupOperations.Find(HardwareGroupOperations.Get(group, tableKind == "watch" ? "WatchTables" : "ForceTables"), parts.Last())
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + tableKind + " table not found: " + tablePath);
                meta["tablePath"] = tablePath; meta["requestedAccess"] = requested.ToString();
                WatchTableAccessRuleComposition watchRules = manager.WatchtableAccessRules; ForceTableAccessRuleComposition forceRules = manager.ForcetableAccessRules;
                object? Existing() => tableKind == "watch" ? (object?)watchRules.Find((PlcWatchTable)table) : forceRules.Find((PlcForceTable)table);
                object Create() => tableKind == "watch" ? (object)watchRules.Create((PlcWatchTable)table, requested) : forceRules.Create((PlcForceTable)table, requested);
                var existing = Existing();
                meta["before"] = existing == null ? null : ReadAccessRule(existing, tableKind == "watch" ? "WatchTable" : "ForceTable");
                if (action == "unassign")
                {
                    if (existing == null) throw new PortalException(PortalErrorCode.NotFound, "No access rule exists for this table.");
                    if (dryRun) return "Unassign preview; nothing changed.";
                    meta["mayHaveChanged"] = true;
                    if (existing is WatchTableAccessRule watchRule) watchRule.Delete(); else if (existing is ForceTableAccessRule forceRule) forceRule.Delete(); else HardwareGroupOperations.Call(existing, "Delete", Type.EmptyTypes);
                    if (Existing() != null) throw new InvalidOperationException("Access rule remains after Delete.");
                    meta["verifiedAbsent"] = true;
                    return "Table access rule removed and verified; project not saved, compiled or downloaded.";
                }
                var accessProperty = existing?.GetType().GetProperty("Access");
                if (existing != null && Equals(accessProperty?.GetValue(existing), requested)) { meta["alreadyAssigned"] = true; return "Access rule already matches; nothing to change."; }
                if (dryRun) return "Assign preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                if (existing is WatchTableAccessRule existingWatch) existingWatch.Access = requested;
                else if (existing is ForceTableAccessRule existingForce) existingForce.Access = requested;
                else if (existing != null) accessProperty!.SetValue(existing, requested);
                else Create();
                var after = Existing() ?? throw new InvalidOperationException("Rule missing after assign.");
                if (!Equals(after.GetType().GetProperty("Access")?.GetValue(after), requested)) throw new InvalidOperationException("Access readback differs from requested value.");
                meta["after"] = ReadAccessRule(after, tableKind == "watch" ? "WatchTable" : "ForceTable");
                return "Table access rule assigned and read back; project not saved, compiled or downloaded.";
            });

#endif
        public HmiObject SetPutGetAccess(string devicePath, bool enable, bool dryRun = true)
        {
            // envelope: legacy-ok-only
            if (_session.IsProjectNull()) return new HmiObject { ["ok"] = false, ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." };
            var device = _session.GetDevice(devicePath);
            if (device == null) return new HmiObject { ["ok"] = false, ["failureCode"] = "NOT_FOUND", ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            DeviceItem? item;
            string? attrName;
            try { (item, attrName) = _session.FindPutGetAttribute(device); }
            catch (Exception error) when (error is NotSupportedException || error is InvalidOperationException)
            {
                return new HmiObject { ["ok"] = false, ["failureCode"] = "UNSUPPORTED_CAPABILITY", ["mayHaveChanged"] = false,
                    ["device"] = device.Name, ["message"] = error.Message };
            }
            if (item == null || attrName == null)
                return new HmiObject
                {
                    ["ok"] = false,
                    ["failureCode"] = "UNSUPPORTED_CAPABILITY",
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access cannot be set via Openness on this CPU/firmware (not an exposed attribute; " +
                                  "confirmed e.g. on S7-1200 1211C V4.6). Set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication', then download hardware."
                };

            object? before;
            try
            {
                before = item.GetAttribute(attrName);
                var info = item.GetAttributeInfos().Single(i => i.Name == attrName);
                SecurityAttributePolicy.RequireBooleanWrite(info.AccessMode.ToString(), before);
            }
            catch (Exception error)
            {
                return new HmiObject { ["ok"] = false, ["failureCode"] = "UNSUPPORTED_CAPABILITY", ["mayHaveChanged"] = false,
                    ["message"] = error.Message };
            }
            if (dryRun) return new HmiObject { ["ok"] = true, ["dryRun"] = true, ["mayHaveChanged"] = false,
                ["writeOutcomeKnown"] = true, ["device"] = device.Name, ["attributeName"] = attrName,
                ["before"] = before, ["requestedValue"] = enable, ["message"] = "PUT/GET preview; nothing changed." };
            using var access = _session.AcquireHmiEditAccess();
            try { item.SetAttribute(attrName, enable); }
            catch (Exception ex)
            {
                return new HmiObject { ["ok"] = false, ["mayHaveChanged"] = true, ["writeOutcomeKnown"] = false, ["device"] = device.Name, ["attributeName"] = attrName, ["message"] = $"SetAttribute failed: {ex.Message}" };
            }
            object? after = null; try { after = item.GetAttribute(attrName); } catch { /* swallow(probe-optional): Optional PUT/GET readback must not suppress the write attempt or its result. */ }

            return new HmiObject
            {
                ["ok"] = _session.AttrValueIsEnabled(after) == enable,
                ["mayHaveChanged"] = true,
                ["writeOutcomeKnown"] = after != null,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["before"] = before?.ToString() ?? string.Empty,
                ["after"] = after?.ToString() ?? string.Empty,
                ["note"] = "Hardware-config change — run DownloadPlc (hardware) for it to take effect on the CPU."
            };
        }

#if SECURITY_PROJECT_PROTECTION
        public SecurityResult ManagePlcProtection(string[] devicePathJson, string[] itemPathJson = null!, string action = "read", string accessLevel = "",
            string password = "", string newPassword = "", bool confirmChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcProtection", meta => {
                _session.Validate("protection");
                var (act, level) = (request.NormalizedAction, request.NormalizedLevel);
                var cpu = ResolveCpuItem(devicePathJson, itemPathJson, meta);
                meta["action"] = act; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                var accessProvider = cpu.GetService<PlcAccessLevelProvider>();
#if SECURITY_MASTER_SECRET
                var secretProvider = cpu.GetService<PlcMasterSecretConfigurator>();
#else
                object? secretProvider = null;
#endif
#if SECURITY_ACCESS_CONTROL
                var accessControl = cpu.GetService<PlcAccessControlConfigurationProvider>();
#else
                object? accessControl = null;
#endif
                meta["before"] = ReadProtectionState(accessProvider, secretProvider, accessControl);
                if (act == "read") return "PLC protection read (access level, master secret state, access control); no modification.";

                if (PlcProtectionRules.LevelActions.Contains(act) && accessProvider == null)
                    throw new NotSupportedException("PlcAccessLevelProvider is not available on this device item (not an S7-1200/1500 CPU?).");
                if (PlcProtectionRules.MasterSecretActions.Contains(act) && secretProvider == null)
                    throw new NotSupportedException("PlcMasterSecretConfigurator is not available on this device item (needs an S7-1500 FW >= 2.9 / S7-1200 FW >= 4.5 CPU).");
                if (act == "setAccessPassword" || act == "resetAccessPassword")
                {
                    var current = ((HmiObject)meta["before"]!)["accessLevel"]?.ToString() ?? "";
                    var warning = PlcProtectionRules.PasswordLevelWarning(current, level);
                    if (warning != null) meta["warning"] = warning;
                }
                if (act == "setAccessLevel" && PlcProtectionRules.NeedsFullAccessPassword(level))
                    meta["note"] = "TIA's hardware compile requires the FullAccess password once the level is " + level + " - set it with action setAccessPassword accessLevel FullAccess.";
                meta["plan"] = DescribeProtectionPlan(act, level);
                if (dryRun) return "Preview: " + meta["plan"] + " Set dryRun=false and confirmChange=true to execute.";
                _session.Validate("confirmation");

                using var access = _session.AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                using var secure = string.IsNullOrEmpty(password) ? null : EngineeringCredentialRules.ToSecureString(password);
                using var secureNew = string.IsNullOrEmpty(newPassword) ? null : EngineeringCredentialRules.ToSecureString(newPassword);
                switch (act)
                {
                    case "setAccessLevel":
                        accessProvider!.PlcProtectionAccessLevel = (PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level);
                        break;
                    case "setAccessPassword":
                        if (!EngineeringCredentialRules.TrySetPassword(() => accessProvider!.SetPassword(
                            (PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level), secure!), meta))
                            return EngineeringCredentialRules.PasswordPolicyRefusal;
                        break;
                    case "resetAccessPassword":
                        accessProvider!.ResetPassword((PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level));
                        break;
#if SECURITY_MASTER_SECRET
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
#endif
                    case "protectAllConfiguration":
#if !SECURITY_ALL_CONFIGURATION
                        throw new NotSupportedException("ProtectAllPlcConfiguration / ProtectAllPlcConfigurationWithPassword exist in the V21 PublicAPI only.");
#else
                        if (secure != null) secretProvider!.ProtectAllPlcConfigurationWithPassword(secure); else secretProvider!.ProtectAllPlcConfiguration();
                        break;
#endif
                    case "unprotectAllConfiguration":
#if !SECURITY_ALL_CONFIGURATION
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
                    if (!HmiValues.GetValue<bool>(meta["readbackVerified"])) throw new InvalidOperationException("Access level readback is '" + readback + "', not '" + level + "'.");
                }
                else if (PlcProtectionRules.MasterSecretActions.Contains(act) && act != "changeMasterSecret")
                {
                    var state = after["masterSecret"]?.ToString() ?? "";
                    var expected = PlcProtectionRules.ExpectedMasterSecretStates(act, !string.IsNullOrEmpty(password));
                    meta["expectedMasterSecret"] = string.Join("|", expected);
                    meta["readbackVerified"] = expected.Contains(state, StringComparer.Ordinal);
                    if (!expected.Contains(state, StringComparer.Ordinal)) throw new InvalidOperationException("MasterSecretConfiguration read back as '" + state + "', expected " + string.Join(" / ", expected) + ".");
                }
                else meta["readbackVerified"] = "password actions have no readable state; TIA raised no exception";
                return "PLC protection " + act + " executed on '" + cpu.Name + "'; state read back in meta.after. Hardware not compiled, project not saved.";
            });

        private DeviceItem ResolveCpuItem(string[] devicePathJson, string[] itemPathJson, HmiObject meta)
        {
            var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
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

        private static HmiObject ReadProtectionState(PlcAccessLevelProvider? access, object? secretValue, object? controlValue)
        {
            var state = new HmiObject();
            state["accessLevel"] = null;
            try { if (access != null) state["accessLevel"] = access.PlcProtectionAccessLevel.ToString(); }
            catch (Exception error) { state["accessLevelError"] = error.GetBaseException().Message; }
            state["accessLevelProviderPresent"] = access != null;
#if SECURITY_MASTER_SECRET
            var secret = (PlcMasterSecretConfigurator?)secretValue;
#else
            object? secret = secretValue;
#endif
#if SECURITY_ACCESS_CONTROL
            var control = (PlcAccessControlConfigurationProvider?)controlValue;
#else
            object? control = controlValue;
#endif
            state["masterSecret"] = secret == null ? null : MasterSecretState(secret);
            state["masterSecretConfiguratorPresent"] = secret != null;

#if SECURITY_ACCESS_CONTROL
            if (control != null)
            {
                try { state["accessControl"] = control.PlcAccessControlConfiguration.ToString(); } catch (Exception ex) { state["accessControlError"] = ex.Message; }
#if SECURITY_UMC_EXTENDED
                try { state["umcServerAddress"] = control.UmcServerAddress; } catch (Exception) { /* swallow(probe-optional): The UMC server address is absent when access control is not configured. */ /* not configured */ }
#endif
            }
#endif
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



#endif
        public HmiObject GetPutGetAccess(string devicePath) => _session.GetPutGetAccess(devicePath);
        private static string? MasterSecretState(object secret)
        {
#if SECURITY_MASTER_SECRET
            return ((PlcMasterSecretConfigurator)secret).MasterSecretConfiguration.ToString();
#else
            return null;
#endif
        }
        private static string? LinkName(object connection, string property)
        {
            var link = connection.GetType().GetProperty(property)?.GetValue(connection);
            return link == null ? null : link.GetType().GetProperty("Name")?.GetValue(link)?.ToString() ?? link.GetType().Name;
        }
    }
}
