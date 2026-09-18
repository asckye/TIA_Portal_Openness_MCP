using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Siemens.Engineering.Safety: SafetyAdministration (settings, runtime groups, program signatures, password state),
    // GlobalSettings (portal-wide), SafetySignatureProvider (per block), SafetyPrintout and SafetyBaseIdProvider
    // (PLC device item). F-compile is not part of the PublicAPI; nothing here replaces the safety acceptance test.
    public partial class Portal
    {
        // Program-modifying actions need the F-program login when a password is set; the login itself and the
        // password administration are exempt so a caller can get in (or out) without a second tool.
        private static readonly string[] SafetyProgramEditingActions =
            { "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "updateSettings", "generateGlobalFIOStatusBlock", "cleanSystemGeneratedObjects", "generateBaseId" };

        private DeviceItem SafetyCpuItem(string softwarePath)
            => GetSoftwareContainer(softwarePath)?.Parent as DeviceItem
               ?? throw new PortalException(PortalErrorCode.NotFound, "PLC device item not found for " + softwarePath);

        private SafetyAdministration RequireSafetyAdministration(string softwarePath, PlcSoftware plc)
            => ResolvePlcService<SafetyAdministration>(softwarePath, plc)
               ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyAdministration unavailable: not an S7-1200/1500 F-CPU or Safety option not installed.");

        // Documented dynamic attributes are read one by one; a missing value is reported as null with its native reason.
        private static JsonObject ReadSafetyAttributes(IEngineeringObject owner, IEnumerable<string> names)
        {
            var values = new JsonObject(); var failures = new JsonObject();
            foreach (var name in names)
            {
                try { values[name] = EngineeringScalarProperties.Json(owner.GetAttribute(name)); }
                catch (Exception ex) { values[name] = null; failures[name] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            if (failures.Count > 0) values["unavailable"] = failures;
            return values;
        }

        private static JsonArray SignatureRows(SafetySignatureProvider? provider)
        {
            var rows = new JsonArray();
            if (provider == null) return rows;
            SafetySignatureComposition signatures = provider.Signatures;
            foreach (SafetySignature signature in signatures)
                rows.Add(SafetyLogic.SignatureRow(signature.Type.ToString(), signature.Value));
            return rows;
        }

        private static SafetySignatureProvider? ProgramSignatures(SafetyAdministration administration)
        {
#if TIA_V20
            return null;
#else
            return administration.ProgramSignatures;
#endif
        }

        private static JsonObject ReadSafetySettings(SafetySettings settings)
        {
            var result = EngineeringScalarProperties.Read(settings);
            var numbers = settings.AssignmentOfBlockNumbers;
            result["assignmentOfBlockNumbers"] = numbers == null ? null : new JsonObject
            {
                ["managementMode"] = numbers.ManagementMode.ToString(),
                ["fromFB"] = numbers.FromFB, ["toFB"] = numbers.ToFB, ["fromFC"] = numbers.FromFC, ["toFC"] = numbers.ToFC, ["fromDB"] = numbers.FromDB, ["toDB"] = numbers.ToDB
            };
            try { result["safetySystemVersion"] = settings.SafetySystemVersion?.Value; }
            catch (Exception ex) { result["safetySystemVersion"] = null; result["safetySystemVersionError"] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            try { result["applicableSafetySystemVersions"] = new JsonArray(settings.GetApplicableSafetySystemVersions().Select(v => (JsonNode)JsonValue.Create(v.Value)!).ToArray()); }
            catch (Exception ex) { result["applicableSafetySystemVersions"] = null; result["applicableSafetySystemVersionsError"] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            result["attributes"] = ReadSafetyAttributes(settings, SafetyLogic.SettingsAttributes);
            return result;
        }

        private static JsonObject ReadRuntimeGroup(RuntimeGroup group)
        {
            var result = EngineeringScalarProperties.Read(group);
            result["attributes"] = ReadSafetyAttributes(group, SafetyLogic.RuntimeGroupAttributes);
            return result;
        }

        private JsonObject ReadSafetyGlobalSettings()
        {
            var settings = _portal?.GetService<GlobalSettings>();
            if (settings == null) return new JsonObject { ["available"] = false, ["reason"] = "GlobalSettings service unavailable on this TIA Portal (Safety option not installed?)." };
            var result = new JsonObject { ["available"] = true };
            var failures = new JsonObject();
            Read("SafetyModificationsPossible", () => settings.SafetyModificationsPossible());
            Read("GenerationOfDefaultFailsafeProgram", () => settings.GenerationOfDefaultFailsafeProgram());
            Read("ManagementOfFailsafeInSoftwareUnitsEnvironment", () => settings.ManagementOfFailsafeInSoftwareUnitsEnvironment());
            Read("UsernameForFChangeHistory", () => settings.UsernameForFChangeHistory());
            if (failures.Count > 0) result["unavailable"] = failures;
            return result;
            void Read(string name, Func<object?> getter)
            {
                try { result[name] = EngineeringScalarProperties.Json(getter()); }
                catch (Exception ex) { result[name] = null; failures[name] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
        }

        private JsonObject ReadSafetyCpu(string softwarePath)
        {
            var cpu = SafetyCpuItem(softwarePath);
            var result = new JsonObject { ["deviceItemName"] = cpu.Name, ["typeIdentifier"] = cpu.TypeIdentifier };
            try { result["fCapabilityActivated"] = EngineeringScalarProperties.Json(cpu.GetAttribute("Failsafe_FCapabilityActivated")); }
            catch (Exception ex) { result["fCapabilityActivated"] = null; result["fCapabilityError"] = ex.GetBaseException().Message; if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            result["services"] = new JsonObject
            {
                ["safetyPrintout"] = cpu.GetService<SafetyPrintout>() != null,
                ["safetyBaseIdProvider"] = SafetyBaseIdService(cpu) != null,
                ["safetySignatureProvider"] = cpu.GetService<SafetySignatureProvider>() != null
            };
            return result;
        }

#if TIA_V20
        private static object? SafetyBaseIdService(DeviceItem cpu) => null;
        private static void GenerateSafetyBaseId(DeviceItem cpu)
            => throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyBaseIdProvider is a V21 API (S7-1200 G2/1500 F-CPUs from firmware V4.1 with F-BaseID).");
#else
        private static SafetyBaseIdProvider? SafetyBaseIdService(DeviceItem cpu) => cpu.GetService<SafetyBaseIdProvider>();
        private static void GenerateSafetyBaseId(DeviceItem cpu)
        {
            var provider = SafetyBaseIdService(cpu) ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyBaseIdProvider unavailable on " + cpu.Name + ": needs an S7-1200 G2/1500 F-CPU from firmware V4.1 with F-BaseID.");
            provider.GenerateBaseId();
        }
#endif

        private static string BlockGroupPath(PlcBlock block)
        {
            var names = new List<string>();
            var parent = block.Parent;
            while (parent is PlcBlockUserGroup group) { names.Insert(0, group.Name); parent = group.Parent; }
            return SafetyLogic.JoinBlockPath(names, block.Name);
        }

        public ResponseMessage ManagePlcSafety(string softwarePath, string action = "read", string runtimeGroup = "", string propertiesJson = "{}", bool dryRun = true,
            string password = "", bool confirmSafetyChange = false, string mainSafetyBlockPath = "", string mainSafetyInstanceDbPath = "")
            => RunHmiStepTool("ManagePlcSafety", meta => {
                bool writing = SafetyLogic.ValidateRequest(action, runtimeGroup, password, confirmSafetyChange, dryRun);
                bool editsProgram = SafetyProgramEditingActions.Contains(action);
                using var access = writing ? AcquireHmiEditAccess() : null;
                // Login/logoff/password administration do not change the F-program, so Offline is only demanded for program edits.
                var plc = ExactPlcForEngineering(softwarePath, writing && editsProgram);
                var administration = RequireSafetyAdministration(softwarePath, plc);
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["administration"] = new JsonObject
                {
                    ["isSafetyOfflineProgramPasswordSet"] = administration.IsSafetyOfflineProgramPasswordSet,
                    ["isLoggedOnToSafetyOfflineProgram"] = administration.IsLoggedOnToSafetyOfflineProgram
                };
                RuntimeGroupComposition groups = administration.RuntimeGroups;
                if (action == "read")
                {
                    meta["settings"] = ReadSafetySettings(administration.Settings);
                    meta["runtimeGroups"] = new JsonArray(groups.Select(g => (JsonNode)ReadRuntimeGroup(g)).ToArray());
                    var signatures = ProgramSignatures(administration);
                    meta["programSignatures"] = signatures == null ? null : SignatureRows(signatures);
                    if (signatures == null) meta["programSignaturesNote"] = "SafetyAdministration.ProgramSignatures is a V21 API; on V20 read per-block signatures with ReadSafetyBlockSignatures.";
                    meta["globalSettings"] = ReadSafetyGlobalSettings();
                    meta["cpu"] = ReadSafetyCpu(softwarePath);
                    meta["apiCallSuccess"] = true;
                    return "Safety program metadata read (settings incl. block-number ranges and system version, runtime groups incl. F-OB attributes, collective signatures, global settings, CPU F-capability). No login, compile, save or download; not a safety acceptance.";
                }
                if (writing && editsProgram && administration.IsSafetyOfflineProgramPasswordSet && !administration.IsLoggedOnToSafetyOfflineProgram)
                    throw new PortalException(PortalErrorCode.InvalidState, "Safety offline program is password protected and not logged on. Use action=login with the password (passed to TIA only) or log in inside TIA.");
                RuntimeGroup? target = null;
                if (SafetyLogic.RuntimeGroupActions.Contains(action))
                {
                    target = groups.Find(runtimeGroup);
                    if (action == "createRuntimeGroup" && target != null) throw new InvalidOperationException("Runtime group exists: " + runtimeGroup);
                    if (action != "createRuntimeGroup" && target == null) throw new PortalException(PortalErrorCode.NotFound, "Runtime group not found: " + runtimeGroup);
                    if (target != null) meta["before"] = ReadRuntimeGroup(target);
                }
                switch (action)
                {
                    case "createRuntimeGroup":
                    {
                        PlcBlock? mainBlock = null, instanceDb = null;
                        if (!string.IsNullOrWhiteSpace(mainSafetyBlockPath)) mainBlock = ExactMasterCopyPlcSource(softwarePath, mainSafetyBlockPath, true) as PlcBlock ?? throw new ArgumentException("mainSafetyBlockPath must identify a block.");
                        if (!string.IsNullOrWhiteSpace(mainSafetyInstanceDbPath))
                        {
                            if (mainBlock == null) throw new ArgumentException("mainSafetyInstanceDbPath requires mainSafetyBlockPath (main safety FB).");
                            instanceDb = ExactMasterCopyPlcSource(softwarePath, mainSafetyInstanceDbPath, true) as PlcBlock ?? throw new ArgumentException("mainSafetyInstanceDbPath must identify a block.");
                        }
                        meta["overload"] = mainBlock == null ? "Create(name): TIA generates main safety FB + IDB" : instanceDb == null ? "Create(name, mainSafetyFC)" : "Create(name, mainSafetyFB, mainSafetyIDB)";
                        if (!writing) return "Runtime group creation preview; no modification.";
                        meta["mayHaveChanged"] = true;
                        var created = mainBlock == null ? groups.Create(runtimeGroup) : instanceDb == null ? groups.Create(runtimeGroup, mainBlock) : groups.Create(runtimeGroup, mainBlock, instanceDb);
                        meta["apiCallSuccess"] = true; meta["after"] = ReadRuntimeGroup(created);
                        break;
                    }
                    case "deleteRuntimeGroup":
                    {
                        if (!writing) return "Runtime group deletion preview; no modification.";
                        meta["mayHaveChanged"] = true;
                        target!.Delete();
                        if (groups.Find(runtimeGroup) != null) throw new InvalidOperationException("Runtime group remains after Delete; do not blindly retry.");
                        meta["apiCallSuccess"] = true; meta["verifiedAbsent"] = true;
                        break;
                    }
                    case "updateRuntimeGroup":
                    {
                        var (scalars, attributes) = SafetyLogic.SplitRuntimeGroupChanges(propertiesJson);
                        var prepared = EngineeringScalarProperties.Prepare(typeof(RuntimeGroup), scalars);
                        meta["requestedProperties"] = scalars.DeepClone(); meta["requestedAttributes"] = attributes.DeepClone();
                        if (!writing) return "Runtime group update preview; no modification.";
                        EngineeringScalarProperties.Apply(target!, prepared, meta);
                        ApplySafetyAttributes(target!, attributes, meta);
                        meta["apiCallSuccess"] = true; meta["after"] = ReadRuntimeGroup(target!);
                        break;
                    }
                    case "updateSettings":
                    {
                        var settings = administration.Settings;
                        var changes = SafetyLogic.SplitSettingsChanges(propertiesJson);
                        if (changes.Scalars.ContainsKey("SafetyModeCanBeDisabled") && !confirmSafetyChange) throw new ArgumentException("Changing SafetyModeCanBeDisabled requires confirmSafetyChange=true.");
                        var prepared = EngineeringScalarProperties.Prepare(typeof(SafetySettings), changes.Scalars);
                        var preparedNumbers = EngineeringScalarProperties.Prepare(typeof(AssignmentOfBlockNumbers), changes.BlockNumbers);
                        SafetySystemVersion? version = null;
                        if (changes.SafetySystemVersion != null)
                        {
                            var applicable = settings.GetApplicableSafetySystemVersions();
                            version = applicable.SingleOrDefault(v => v.Value == changes.SafetySystemVersion)
                                ?? throw new ArgumentException("SafetySystemVersion '" + changes.SafetySystemVersion + "' is not applicable; applicable: " + string.Join(", ", applicable.Select(v => v.Value)));
                        }
                        meta["before"] = ReadSafetySettings(settings);
                        meta["requestedProperties"] = changes.Scalars.DeepClone(); meta["requestedBlockNumbers"] = changes.BlockNumbers.DeepClone();
                        meta["requestedAttributes"] = changes.Attributes.DeepClone(); meta["requestedSafetySystemVersion"] = changes.SafetySystemVersion;
                        if (!writing) return "Safety settings update preview; no modification.";
                        EngineeringScalarProperties.Apply(settings, prepared, meta);
                        if (preparedNumbers.Count > 0)
                        {
                            var numbers = settings.AssignmentOfBlockNumbers ?? throw new NotSupportedException("AssignmentOfBlockNumbers unavailable on this F-CPU.");
                            // ManagementMode first: fixed ranges are rejected natively while the F-system manages the numbers.
                            EngineeringScalarProperties.Apply(numbers, preparedNumbers.OrderBy(p => p.Property.Name == "ManagementMode" ? 0 : 1).ToList(), meta);
                        }
                        if (version != null) { meta["mayHaveChanged"] = true; settings.SafetySystemVersion = version; if (settings.SafetySystemVersion?.Value != version.Value) throw new InvalidOperationException("SafetySystemVersion readback differs."); }
                        ApplySafetyAttributes(settings, changes.Attributes, meta);
                        meta["apiCallSuccess"] = true; meta["after"] = ReadSafetySettings(settings);
                        break;
                    }
                    case "generateGlobalFIOStatusBlock":
                    {
                        if (!writing) return "Global F-I/O status block generation preview; native call creates or overwrites the status block.";
                        meta["mayHaveChanged"] = true;
                        var block = target!.GenerateGlobalFIOStatusBlock();
                        meta["apiCallSuccess"] = true;
                        meta["generatedBlock"] = block == null ? null : new JsonObject { ["name"] = block.Name, ["number"] = block.Number, ["path"] = BlockGroupPath(block), ["programmingLanguage"] = block.ProgrammingLanguage.ToString() };
                        break;
                    }
                    case "cleanSystemGeneratedObjects":
                    {
                        if (!writing) return "Clean-up preview; native call removes the objects generated by the last F-compile.";
                        meta["mayHaveChanged"] = true;
                        administration.Settings.CleanSystemGeneratedObjects();
                        meta["apiCallSuccess"] = true;
                        break;
                    }
                    case "generateBaseId":
                    {
                        var cpu = SafetyCpuItem(softwarePath);
                        meta["cpu"] = ReadSafetyCpu(softwarePath);
                        if (SafetyBaseIdService(cpu) == null) throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyBaseIdProvider unavailable on " + cpu.Name + ": V21 API for S7-1200 G2/1500 F-CPUs from firmware V4.1 with F-BaseID.");
                        if (!writing) return "F-BaseID generation preview; native call assigns a new random 64-bit F-BaseID to all connected PROFIsafe address type 3 F-I/O.";
                        meta["mayHaveChanged"] = true;
                        GenerateSafetyBaseId(cpu);
                        meta["apiCallSuccess"] = true;
                        break;
                    }
                    case "login":
                    {
                        if (administration.IsLoggedOnToSafetyOfflineProgram) throw new InvalidOperationException("Already logged on to the safety offline program.");
                        if (!administration.IsSafetyOfflineProgramPasswordSet) throw new InvalidOperationException("No safety offline program password is set; login is not needed.");
                        if (!writing) return "Login preview; no native call.";
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password)) administration.LoginToSafetyOfflineProgram(secure);
                        meta["apiCallSuccess"] = true;
                        if (!administration.IsLoggedOnToSafetyOfflineProgram) throw new InvalidOperationException("Login returned but IsLoggedOnToSafetyOfflineProgram is still false.");
                        break;
                    }
                    case "logoff":
                    {
                        if (!administration.IsLoggedOnToSafetyOfflineProgram) throw new InvalidOperationException("Not logged on to the safety offline program.");
                        if (!writing) return "Logoff preview; no native call.";
                        administration.LogoffFromSafetyOfflineProgram();
                        meta["apiCallSuccess"] = true;
                        if (administration.IsLoggedOnToSafetyOfflineProgram) throw new InvalidOperationException("Logoff returned but IsLoggedOnToSafetyOfflineProgram is still true.");
                        break;
                    }
                    case "setPassword":
                    {
                        if (administration.IsSafetyOfflineProgramPasswordSet) throw new InvalidOperationException("A safety offline program password is already set; revoke it first.");
                        if (!writing) return "Set-password preview; no native call.";
                        meta["mayHaveChanged"] = true;
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password)) administration.SetSafetyOfflineProgramPassword(secure);
                        meta["apiCallSuccess"] = true;
                        if (!administration.IsSafetyOfflineProgramPasswordSet) throw new InvalidOperationException("SetSafetyOfflineProgramPassword returned but IsSafetyOfflineProgramPasswordSet is still false.");
                        break;
                    }
                    case "revokePassword":
                    {
                        if (!administration.IsSafetyOfflineProgramPasswordSet) throw new InvalidOperationException("No safety offline program password is set.");
                        if (!writing) return "Revoke-password preview; no native call.";
                        meta["mayHaveChanged"] = true;
                        using (var secure = PlcBlockServicesLogic.ToSecureString(password)) administration.RevokeSafetyOfflineProgramPassword(secure);
                        meta["apiCallSuccess"] = true;
                        if (administration.IsSafetyOfflineProgramPasswordSet) throw new InvalidOperationException("RevokeSafetyOfflineProgramPassword returned but the password is still set.");
                        break;
                    }
                }
                meta["administrationAfter"] = new JsonObject
                {
                    ["isSafetyOfflineProgramPasswordSet"] = administration.IsSafetyOfflineProgramPasswordSet,
                    ["isLoggedOnToSafetyOfflineProgram"] = administration.IsLoggedOnToSafetyOfflineProgram
                };
                return editsProgram
                    ? "Offline Safety engineering action completed; no F-compile, save or download. Collective/block F-signatures change only after an F-compile in TIA; safety acceptance is still required."
                    : "Safety administration action completed; no project content changed.";
            });

        private static void ApplySafetyAttributes(IEngineeringObject owner, JsonObject attributes, JsonObject meta)
        {
            if (attributes.Count == 0) return;
            var applied = meta["appliedAttributes"] as JsonArray ?? new JsonArray(); meta["appliedAttributes"] = applied;
            foreach (var attribute in attributes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = attribute.Key;
                object value = attribute.Value is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : attribute.Value!.GetValue<int>();
                owner.SetAttribute(attribute.Key, value);
                var readback = owner.GetAttribute(attribute.Key);
                if (!Equals(Convert.ToString(readback), Convert.ToString(value))) throw new InvalidOperationException("Attribute readback differs: " + attribute.Key + ". Changes are not rolled back.");
                applied.Add(attribute.Key);
            }
        }

        public ResponseMessage ManageSafetyGlobalSettings(string action = "read", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManageSafetyGlobalSettings", meta => {
                if (action != "read" && action != "update") throw new ArgumentException("action must be read or update.");
                if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                bool writing = action == "update" && !dryRun;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["before"] = ReadSafetyGlobalSettings();
                if (action == "read") { meta["apiCallSuccess"] = true; return "Safety GlobalSettings read (TIA Portal scope, not project scope)."; }
                var changes = SafetyLogic.ParseGlobalSettingChanges(propertiesJson);
                var settings = _portal.GetService<GlobalSettings>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "GlobalSettings service unavailable on this TIA Portal.");
                meta["requested"] = new JsonObject(changes.Select(c => new KeyValuePair<string, JsonNode?>(c.Key, EngineeringScalarProperties.Json(c.Value))));
                if (!writing) return "Safety GlobalSettings update preview; no modification.";
                var applied = new JsonArray(); meta["applied"] = applied;
                foreach (var change in changes)
                {
                    meta["mayHaveChanged"] = true; meta["lastAttempted"] = change.Key;
                    switch (change.Key)
                    {
                        case "SafetyModificationsPossible": settings.SafetyModificationsPossible((bool)change.Value); break;
                        case "GenerationOfDefaultFailsafeProgram": settings.GenerationOfDefaultFailsafeProgram((bool)change.Value); break;
                        case "ManagementOfFailsafeInSoftwareUnitsEnvironment": settings.ManagementOfFailsafeInSoftwareUnitsEnvironment((bool)change.Value); break;
                        case "UsernameForFChangeHistory": settings.UsernameForFChangeHistory((string)change.Value); break;
                    }
                    applied.Add(change.Key);
                }
                meta["apiCallSuccess"] = true; meta["after"] = ReadSafetyGlobalSettings();
                // An empty user name resets to the TIA default, so its readback is informational rather than an equality check.
                foreach (var change in changes.Where(c => c.Key != "UsernameForFChangeHistory"))
                    if (!Equals(meta["after"]![change.Key]?.GetValue<bool>(), (bool)change.Value)) throw new InvalidOperationException("GlobalSettings readback differs: " + change.Key);
                return "Safety GlobalSettings updated and read back; portal-wide effect, no project save.";
            }, requiresProject: false);

        public ResponseMessage ReadSafetyBlockSignatures(string softwarePath, string blockPath = "", bool includeProgramSignatures = true, int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadSafetyBlockSignatures", meta => {
                var plc = ExactPlcForEngineering(softwarePath, false);
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath;
                var administration = ResolvePlcService<SafetyAdministration>(softwarePath, plc);
                meta["fCpu"] = administration != null;
                if (includeProgramSignatures)
                {
                    var program = administration == null ? null : ProgramSignatures(administration);
                    meta["programSignatures"] = program == null ? null : SignatureRows(program);
                    if (program == null) meta["programSignaturesNote"] = administration == null ? "No SafetyAdministration on this PLC." : "SafetyAdministration.ProgramSignatures is a V21 API.";
                }
                var blocks = new List<PlcBlock>();
                if (!string.IsNullOrWhiteSpace(blockPath))
                    blocks.Add(ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock ?? throw new ArgumentException("blockPath must identify a block."));
                else GetBlocksRecursive(plc.BlockGroup, blocks);
                var rows = new List<JsonNode>(); int scanned = 0, withoutProvider = 0;
                foreach (var block in blocks)
                {
                    scanned++;
                    var provider = block.GetService<SafetySignatureProvider>();
                    if (provider == null) { withoutProvider++; if (!string.IsNullOrWhiteSpace(blockPath)) meta["notAnFBlock"] = true; continue; }
                    rows.Add(new JsonObject
                    {
                        ["path"] = BlockGroupPath(block), ["name"] = block.Name, ["number"] = block.Number, ["kind"] = block.GetType().Name,
                        ["programmingLanguage"] = block.ProgrammingLanguage.ToString(), ["isKnowHowProtected"] = block.IsKnowHowProtected,
                        ["signatures"] = SignatureRows(provider)
                    });
                }
                meta["blocksScanned"] = scanned; meta["blocksWithoutSafetySignature"] = withoutProvider;
                var (skip, take) = PlcBlockServicesLogic.Paginate(meta, rows.Count, offset, limit);
                meta["records"] = new JsonArray(rows.Skip(skip).Take(take).ToArray());
                meta["apiCallSuccess"] = true;
                meta["semantics"] = "Value 0 = no valid F-signature (block changed since the last F-compile, or not an F-block). Signatures are offline values from the project, not read from the CPU.";
                return rows.Count == 0
                    ? (string.IsNullOrWhiteSpace(blockPath) ? "No block exposes SafetySignatureProvider (no F-blocks, or not an F-CPU)." : "Block is not an F-block of an F-CPU (no SafetySignatureProvider).")
                    : "Per-block F-signatures read from the offline project; no change.";
            });

        public ResponseMessage ExportSafetyPrintout(string softwarePath, string filePath, string printer = "MicrosoftPrintToPdf", string option = "All", string documentLayout = "", bool dryRun = true)
            => RunHmiStepTool("ExportSafetyPrintout", meta => {
                var request = SafetyLogic.ValidatePrintoutRequest(filePath, printer, option, documentLayout);
                var file = NativeFileOutput.Plan(filePath);
                ExactPlcForEngineering(softwarePath, false);
                var cpu = SafetyCpuItem(softwarePath);
                var service = cpu.GetService<SafetyPrintout>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "SafetyPrintout unavailable on " + cpu.Name + ": not an S7-1200/1500 F-CPU or Safety option not installed.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["filePath"] = file.FullName;
                meta["printer"] = request.Printer; meta["option"] = request.Option; meta["documentLayout"] = request.Layout; meta["cpu"] = cpu.Name;
                meta["requirement"] = "The selected Windows printer driver (Microsoft Print to PDF / XPS Document Writer) must be enabled on the TIA machine; the printout is the official STEP 7 Safety printout of the offline project.";
                if (dryRun) return "Safety printout preview; native Print not called.";
                meta["mayHaveWrittenFiles"] = true;
                var filePrinter = (SafetyPrintoutFilePrinter)Enum.Parse(typeof(SafetyPrintoutFilePrinter), request.Printer);
                var printOption = (SafetyPrintoutOption)Enum.Parse(typeof(SafetyPrintoutOption), request.Option);
                bool success = service.Print(filePrinter, file, request.Layout, printOption);
                meta["nativeResult"] = success;
                if (!success) throw new PortalException(PortalErrorCode.ExportFailed, "SafetyPrintout.Print returned false (printer driver disabled, invalid layout name, or F-program not compiled).");
                meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file); meta["dataComplete"] = false;
                return "Safety printout written by TIA and hashed; document content not parsed. No project change.";
            });
    }
}
