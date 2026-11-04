#if OPTIONAL_V20_PLUS

using static TiaMcp.Adapters.Native.Optional.OptionalEvidence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SiVArc;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Optional.SivarcPolicy;
using CreateOptions = Siemens.Engineering.SiVArc.CreateOptions;
using SivarcFamily = TiaMcp.Adapters.Native.Optional.OptionalSession.SivarcFamily;

namespace TiaMcp.Adapters.Native.Optional
{
    // The SiVArc option package (Siemens.Engineering.SiVArc). Official SiVArc manual, "SiVArc Openness"
    // chapter: rule tables / folders, rule objects and groups, copying rules from master copies, library objects as rule
    // references, PLC / HMI device columns, tag / text definitions and tag member settings on blocks, the expression resolver,
    // layout data export / import on screens, upgrading SiVArc definitions and generation. The project service is
    // project.GetService<Sivarc>(); SivarcDataProvider hangs on a CodeBlock, SivarcDefinitionsUpgrader on the PlcSoftware and
    // LayoutData on a classic Screen or Unified HmiScreen. The VM has no SiVArc licence: shape checks only, and every tool
    // answers NotSupported when the service is absent. Writes need the SiVArc licence (TIA raises "License not found").
    // Native observations: TIA V21; original observation date not recorded. See docs/reference/real-machine-ledger.md.
    internal sealed partial class SivarcAdapter
    {
        // ---- tools: block definitions (SivarcDataProvider on a CodeBlock) ----------------------------------------------------------------
        private (CodeBlock block, SivarcDataProvider provider) RequireSivarcBlock(string softwarePath, string blockPath, bool writing, HmiObject meta)
        {
            var plc = _session.ExactPlcForEngineering(softwarePath, writing);
            var block = _session.ExactObjectUnder(plc.BlockGroup, blockPath, "Blocks", "PLC block") as CodeBlock;
            try { Logic.RequireSupportedBlockClass(block?.GetType().Name ?? "non-code block"); }
            catch (NotSupportedException) { meta["v4Rejection"] = "UNSUPPORTED_CAPABILITY"; meta["mayHaveChanged"] = false; throw; }
            var provider = Logic.BlockProvider(block!.GetType().Name, () => block.GetService<SivarcDataProvider>())
                ?? throw new PlcSoftwareException("NotSupportedOnVersion",
                    "SivarcDataProvider unavailable on this block (SiVArc option package not installed).");
            return (block!, provider);
        }
#if OPTIONAL_V21
        private static void SetTagMember(TagMember member, string property, object? value)
        {
            switch (property)
            {
                case "AcquisitionCycle": member.AcquisitionCycle = value?.OptionalValue<string>() ?? ""; break;
                case "AcquisitionMode": member.AcquisitionMode = (AcquisitionMode)Enum
                    .Parse(typeof(AcquisitionMode), Logic.RequireOneOf(value?.OptionalValue<string>() ?? "", Logic.AcquisitionModes, "AcquisitionMode")); break;
                default: member.Comment = value?.OptionalValue<string>() ?? ""; break;
            }
        }
#endif
        public HardwareAddressingReply ReadSivarcBlockDefinitions(string softwarePath, string blockPath, bool includeBlockParameters = true)
            => _session.RunHmiStepTool("ListSivarcBlockDefinitions", meta =>
            {
                EngineeringGroupOperations.Parts(blockPath);
                var (block, provider) = RequireSivarcBlock(softwarePath, blockPath, false, meta);
                TagDefinitionComposition tags = provider.TagDefinitions; TextDefinitionComposition texts = provider.TextDefinitions;
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["block"] = new HmiObject {
                    ["name"] = block.Name, ["blockClass"] = block.GetType().Name, ["number"] = block.Number };
                meta["tagDefinitions"] =
                    new HmiArray(EngineeringGroupOperations.Items(tags).Cast<TagDefinition>().Take(500).Select(d => (object)TagDefinitionRow(d)).ToArray());
                meta["tagDefinitionCount"] = tags.Count;
                meta["textDefinitions"] =
                    new HmiArray(EngineeringGroupOperations.Items(texts).Cast<TextDefinition>().Take(500).Select(d => (object)TextDefinitionRow(d)).ToArray());
                meta["textDefinitionCount"] = texts.Count;
#if !OPTIONAL_V21
                meta["tagMemberSettings"] = null; meta["tagMemberSettingsNote"] = "TagMemberSetting / TagMember are V21 additions; absent on V20.";
#else
                Safe(meta, "tagMemberSettings", () => provider.TagMemberSettings == null ? null
                        : TagMemberSettingRow(provider.TagMemberSettings, includeBlockParameters));
#endif
                return "SiVArc tag / text definitions and tag member settings of the block read (SivarcDataProvider); no modification.";
            });

        public HardwareAddressingReply ManageSivarcBlockDefinition(string softwarePath, string blockPath, string kind, string name = "", string action = "read",
                string propertiesJson = "{}", string textsJson = "{}", bool confirmDelete = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManageSivarcBlockDefinition", meta =>
            {
                bool write = Logic.ValidateDefinitionRequest(blockPath, kind, name, action, propertiesJson, textsJson, confirmDelete, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var (block, provider) = RequireSivarcBlock(softwarePath, blockPath, write, meta);
                var properties = Logic.ParseObject(propertiesJson, "propertiesJson"); var texts = Logic.ParseObject(textsJson, "textsJson");
                meta["softwarePath"] = softwarePath; meta["blockPath"] = blockPath; meta["kind"] = kind; meta["name"] = name; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                string S(string key) => properties[key]?.OptionalValue<string>() ?? "";
                if (kind == "tagDefinition" || kind == "textDefinition")
                {
                    bool tag = kind == "tagDefinition";
                    object Fresh() => tag ? provider.TagDefinitions : provider.TextDefinitions;
                    var existing = EngineeringGroupOperations.Find(Fresh(), name);
                    if (action != "create" && existing == null) throw new PlcSoftwareException("NotFound", kind + " not found on block: " + name);
                    if (action == "create" && existing != null) throw new InvalidOperationException(kind + " already exists on block: " + name);
                    if (existing != null) meta["before"] = tag ? TagDefinitionRow((TagDefinition)existing) : TextDefinitionRow((TextDefinition)existing);
                    if (action == "read") return kind + " read; no modification.";
                    meta["nativeSignature"] = action == "create"
                        ? (tag ? "TagDefinitionComposition.Create(string)" : "TextDefinitionComposition.Create(string)") : action == "delete" ? kind
                        + ".Delete()" : "typed property setters" + (texts.Count > 0 ? " + MultilingualTextItem.Text" : "");
                    if (!write) return kind + " " + action + " preview; nothing changed (real edits need the SiVArc licence).";
                    meta["mayHaveChanged"] = true;
                    if (action == "delete")
                    {
                        if (tag) ((TagDefinition)existing!).Delete(); else ((TextDefinition)existing!).Delete();
                        meta["verifiedAbsent"] = EngineeringGroupOperations.Find(Fresh(), name) == null;
                        if (meta["verifiedAbsent"]!.OptionalValue<bool>() != true) throw new InvalidOperationException(kind + " still present after Delete().");
                        return kind + " deleted and verified absent; project not saved.";
                    }
                    if (tag)
                    {
                        TagDefinitionComposition definitions = provider.TagDefinitions;
                        var target = action == "create" ? definitions.Create(name) : (TagDefinition)existing!;
                        foreach (var pair in properties) {
                            switch (pair.Key) {
                                case "Name": target.Name = S("Name"); break; case "Value": target.Value = S("Value"); break; default: target.Comment =
                                    S("Comment"); break;
                            }
                        }
                        var fresh = EngineeringGroupOperations.Find(Fresh(), target.Name) as TagDefinition
                            ?? throw new InvalidOperationException("Tag definition not found on re-navigation."); meta["after"] = TagDefinitionRow(fresh);
                    }
                    else
                    {
                        TextDefinitionComposition definitions = provider.TextDefinitions;
                        var target = action == "create" ? definitions.Create(name) : (TextDefinition)existing!;
                        foreach (var pair in properties) {
                            switch (pair.Key) {
                                case "Name": target.Name = S("Name"); break; case "Expression": target.Expression = S("Expression"); break; default: target
                                    .Comment = S("Comment"); break;
                            }
                        }
                        if (texts.Count > 0)
                        {
                            var items = EngineeringGroupOperations.Items(target.Text.Items).Cast<MultilingualTextItem>().ToArray();
                            foreach (var pair in texts)
                            {
                                var item = items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, pair.Key, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new ArgumentException("Project language not found for text definition: " + pair.Key);
                                item.Text = pair.Value!.OptionalValue<string>();
                            }
                        }
                        var fresh = EngineeringGroupOperations.Find(Fresh(), target.Name) as TextDefinition
                            ?? throw new InvalidOperationException("Text definition not found on re-navigation."); meta["after"] = TextDefinitionRow(fresh);
                    }
                    return kind + " " + action + " applied and read back; project not saved.";
                }
#if !OPTIONAL_V21
                throw new PlcSoftwareException("NotSupportedOnVersion", "TagMemberSetting / TagMember (kind " + kind + ") are V21 additions; absent on V20.");
#else
                var settings = provider.TagMemberSettings ?? throw new PlcSoftwareException("NotFound", "TagMemberSettings unavailable on this block.");
                if (kind == "tagMemberSettings")
                {
                    meta["before"] = TagMemberSettingRow(settings, false);
                    if (action == "read") return "Tag member settings read; no modification.";
                    if (!write) return "Tag member settings update preview; nothing changed.";
                    meta["mayHaveChanged"] = true; settings.UseCommonConfiguration = properties["UseCommonConfiguration"]?.OptionalValue<bool>() ?? settings
                        .UseCommonConfiguration;
                    meta["after"] = TagMemberSettingRow(provider.TagMemberSettings!, false);
                    return "Tag member settings updated and read back; project not saved.";
                }
                TagMember member = kind == "commonParameters" ? settings.CommonParameters
                    ?? throw new PlcSoftwareException("NotFound", "CommonParameters unavailable on this block.")
                    : EngineeringGroupOperations.Find(settings.BlockParameters, name) as TagMember
                        ?? throw new PlcSoftwareException("NotFound", "Block parameter not found in tag member settings: " + name);
                meta["before"] = TagMemberRow(member);
                if (action == "read") return "Tag member read; no modification.";
                if (!write) return "Tag member update preview; nothing changed.";
                meta["mayHaveChanged"] = true;
                foreach (var pair in properties) SetTagMember(member, pair.Key, pair.Value);
                var again = provider.TagMemberSettings!;
                meta["after"] =
                    TagMemberRow(kind == "commonParameters" ? again.CommonParameters : (TagMember)EngineeringGroupOperations
                        .Find(again.BlockParameters, name)!);
                return "Tag member updated and read back; project not saved.";
#endif
            });

        // ---- tools: expression resolver --------------------------------------------------------------------------------------------------
        public HardwareAddressingReply ResolveSivarcExpression(string softwarePath, string blockPath, string devicePathJson, string itemPathJson,
                string libraryItemKind, string libraryItemPath, string expression, string libraryName = "", int maxResults = 500)
            => _session.RunHmiStepTool("ResolveSivarcExpression", meta =>
            {
                Logic.ValidateExpressionRequest(blockPath, devicePathJson, itemPathJson, libraryItemKind, libraryItemPath, expression, maxResults);
                var sivarc = _session.RequireSivarc();
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var block = _session.ExactObjectUnder(plc.BlockGroup, blockPath, "Blocks", "PLC block") as CodeBlock
                    ?? throw new ArgumentException("The expression resolver takes a code block (FB / FC), not " + blockPath + ".");
                var deviceItem = _session.ExactDeviceItem(Logic.ParseNames(devicePathJson, "devicePathJson"), Logic.ParseNames(itemPathJson, "itemPathJson"));
                object libraryItem = libraryItemKind == "masterCopy" ? _session.ExactMasterCopy(libraryName, libraryItemPath) : _session
                    .ExactLibraryType(_session.ExactOpenEngineeringLibrary(libraryName), libraryItemPath);
                meta["block"] = new HmiObject { ["name"] = block.Name, ["blockClass"] = block.GetType().Name }; meta["deviceItem"] = deviceItem.Name;
                meta["libraryItem"] = SivarcReferenceJson(libraryItem); meta["expression"] = expression;
#if !OPTIONAL_V21
                ExpressionResolver resolver = sivarc
                    .GetExpressionResolver(block, deviceItem, libraryItem as ISivarcLibraryItem
                        ?? throw new ArgumentException("Library item is not a SiVArc library item (ISivarcLibraryItem)."));
#else
                ExpressionResolver resolver = sivarc.GetExpressionResolver(block, deviceItem, (IEngineeringObject)libraryItem);
#endif
                var results = resolver.Resolve(expression);
                var rows = EngineeringGroupOperations.Items(results).Cast<ExpressionResult>()
                    .Select(r => (object)new HmiObject { ["instanceName"] = r.InstanceName, ["callPath"] = r.CallPath, ["result"] = r.Result }).ToArray();
                Page(rows, 0, maxResults, meta);
                return "SiVArc expression resolved for every block instance in the compiled call structure (ExpressionResolver.Resolve); read-only.";
            });

        // ---- tools: screen layout data (V21 LayoutData on a classic Screen or Unified HmiScreen) -------------------------------------------
        public HardwareAddressingReply ManageSivarcScreenLayout(string softwarePath, string screenName, string action, string filePath, bool dryRun = true)
            => _session.RunHmiStepTool("ManageSivarcScreenLayout", meta =>
            {
                bool write = Logic.ValidateLayoutRequest(screenName, action, filePath, dryRun);
                using var access = write ? _session.AcquireHmiEditAccess() : null;
                var screen = _session.ResolveHmiScreenOrThrow(softwarePath, screenName);
                meta["softwarePath"] = softwarePath; meta["screenName"] = screenName; meta["screenClass"] = screen.GetType().Name; meta["action"] = action;
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
#if !OPTIONAL_V21
                throw new PlcSoftwareException("NotSupportedOnVersion", "SiVArc LayoutData (screen layout export / import) is a V21 addition; absent on V20.");
#else
                var provider = screen as IEngineeringServiceProvider
                    ?? throw new NotSupportedException("Screen is not a service provider: " + screen.GetType().FullName);
                LayoutData layout = provider.GetService<LayoutData>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion",
                        "LayoutData service unavailable on this screen (SiVArc option package not installed).");
                if (action == "export")
                {
                    var file = NativeFileOutput.Plan(filePath); meta["file"] = file.FullName;
                    meta["mayHaveWrittenFiles"] = true;
                    layout.Export(file); meta["output"] = NativeFileOutput.Verify(file);
                    return "SiVArc screen layouts exported to the YML file (LayoutData.Export); project unchanged.";
                }
                var input = HardwareServicesLogic.RequireExistingInputFile(filePath, "filePath"); meta["file"] = input.FullName;
                if (!write) return "SiVArc layout import preview; nothing changed (LayoutData.Import would apply the file's layouts to the screen).";
                meta["mayHaveChanged"] = true;
                LayoutDataImportResult result = layout.Import(input);
                meta["result"] = new HmiObject { ["state"] = result.State.ToString(), ["numberOfLayouts"] = result.NumberOfLayouts };
                meta["nativeResultReturned"] = true;
                meta["nativeState"] = result.State.ToString();
                meta["nativeStateType"] = result.State.GetType().FullName;
                meta["mayHaveChanged"] = true;
                meta["logFilePath"] = null;
                meta["nativeMessages"] = null;
                if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State)) meta["operationSuccess"] = false;
                return "SiVArc screen layouts imported (LayoutData.Import); inspect result.state; project not saved.";
#endif
            });

        // ---- tools: definitions upgrader --------------------------------------------------------------------------------------------------
        public HardwareAddressingReply UpgradeSivarcDefinitions(string softwarePath, bool dryRun = true)
            => _session.RunHmiStepTool("UpgradeSivarcDefinitions", meta =>
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = _session.ExactPlcForEngineering(softwarePath, !dryRun);
                var upgrader = plc.GetService<SivarcDefinitionsUpgrader>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion",
                        "SivarcDefinitionsUpgrader unavailable on this PLC (SiVArc option package not installed).");
                meta["softwarePath"] = softwarePath; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["nativeSignature"] =
                    "SivarcDefinitionsUpgrader.Upgrade()";
                if (dryRun) return "SiVArc definitions upgrade preview (SIVARCCOND / SIVARCTEXT -> tag / text definitions); nothing changed.";
                meta["mayHaveChanged"] = true;
                UpgradeDefinitionsResult result = upgrader.Upgrade();
                meta["result"] = new HmiObject { ["warningCount"] = result.WarningCount, ["messages"] = SivarcMessageRows(result.Messages) };
                return "SiVArc definitions upgraded on the PLC; inspect warningCount / messages; project not saved.";
            });

        // ---- typed generation (GenerateSiVArc) ----------------------------------------------------------------------------------
        /// <summary>Name of the Device that owns the PLC software at softwarePath (SoftwareContainer.Parent -> DeviceItem chain -> Device).</summary>
        private string OwningDeviceName(string softwarePath)
        {
            var sc = _session.ResolveSoftwareContainerUncached(softwarePath)
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC software not found: " + softwarePath);
            IEngineeringObject? current = sc.Parent;
            while (current is DeviceItem item) current = item.Parent;
            if (current is Device owner && !string.IsNullOrEmpty(owner.Name)) return owner.Name;
            throw new PlcSoftwareException("NotFound", "Owning device of PLC software " + softwarePath
                    + " not found (Sivarc.Generate needs the PLC device name).");
        }

        public HardwareAddressingReply GenerateSiVArc(string hmiDeviceName, string plcSoftwarePathsJson, string generationOptions, bool dryRun = true,
                string additionalHmiDeviceNamesJson = "[]")
            => _session.RunHmiStepTool("GenerateSivarc", meta =>
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var device = _session.ExactEngineeringDevice(new[] { hmiDeviceName });
                var extra = Logic.ParseNames(additionalHmiDeviceNamesJson, "additionalHmiDeviceNamesJson")
                    .Select(n => _session.ExactEngineeringDevice(new[] { n }).Name).ToArray();
                var devices = new[] { device.Name }.Concat(extra).ToArray();
                if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Length) throw new ArgumentException("HMI device names repeat.");
                // Observed on TIA V21: Sivarc.Generate(deviceName, plcs, options) takes PLC *device* names - TIA answered
                // "PLC device '+S1-K1' not found" for the PlcSoftware name, the CPU lives in device "ET 200SP station_1".
                var plcPaths = OptionalSession.ExactNameList(plcSoftwarePathsJson);
                var plcSoftwareNames = plcPaths.Select(p => _session.ExactPlcForEngineering(p, !dryRun).Name).ToArray();
                var plcs = plcPaths.Select(p => OwningDeviceName(p)).ToArray();
                if (plcs.Distinct(StringComparer.Ordinal).Count() != plcs.Length)
                    throw new InvalidOperationException("Native PLC device names repeat (two PLC paths inside the same device).");
                var sivarc = _session.RequireSivarc();
                var names = Logic.ParseGenerationOptions(generationOptions);
                GenerationOptions options = names
                    .Aggregate(GenerationOptions.None, (acc, n) => acc | (GenerationOptions)Enum.Parse(typeof(GenerationOptions), n));
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["hmiDeviceName"] = device.Name; meta["hmiDeviceNames"] =
                    new HmiArray(devices.Select(d => (object)d).ToArray());
                meta["plcSoftwareNames"] = new HmiArray(plcSoftwareNames.Select(p => (object)p).ToArray()); meta["plcDeviceNames"] =
                    new HmiArray(plcs.Select(p => (object)p).ToArray()); meta["plcNames"] = new HmiArray(plcs.Select(p => (object)p).ToArray());
                meta["generationOptions"] = options.ToString(); meta["nativeSignature"] = devices.Length > 1
                    ? "Sivarc.Generate(IEnumerable<string>, IEnumerable<string>, GenerationOptions)"
                    : "Sivarc.Generate(string, IEnumerable<string>, GenerationOptions)";
                if (dryRun) return "SiVArc native generation preview; generation can create/update HMI objects according to rules and selected native options.";
                meta["mayHaveChanged"] = true;
                // Official SiVArc generation requires PLC device names and an existing HMI connection.
                // Never retry a generation write with a guessed alternate software name.
                SivarcGenerationResult result = devices.Length > 1 ? sivarc.Generate(devices, plcs, options) : sivarc.Generate(device.Name, plcs, options);
                meta["attempts"] = new HmiArray(new object[] { new HmiObject { ["plcs"] = "deviceNames", ["outcome"] = "returned" } });
                meta["result"] = new HmiObject {
                    ["isGenerationSuccessful"] = result.IsGenerationSuccessful, ["errorCount"] = result.ErrorCount, ["warningCount"] = result.WarningCount,
                        ["messages"] = SivarcMessageRows(result.Messages)
                };
                meta["generationPassed"] = result.IsGenerationSuccessful; meta["apiCallSuccess"] = true;
                if (!result.IsGenerationSuccessful) meta["operationSuccess"] = false;
                return "SiVArc generation returned (typed SivarcGenerationResult with recursive feedback messages). No automatic save/compile/download.";
            });
    }
}

#endif
