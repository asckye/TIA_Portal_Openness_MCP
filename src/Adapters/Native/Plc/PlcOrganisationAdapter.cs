using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;


namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private readonly IPlcOrganisationSession _session;
        internal bool MutationStarted { get; private set; }
        private readonly object _blockGroupDeleteGate = new object();
        private const int TagPreviewLimit = 200;
        public PlcOrganisationAdapter(IPlcOrganisationSession session) => _session = session;
        private bool IsProjectNull() => _session.IsProjectNull();
        private static object? TryGetPropertyValue(object? target, params string[] names)
        {
            if (target == null) return null;
            foreach (var name in names)
            {
                try { var property = target.GetType().GetProperty(name); if (property != null) return property.GetValue(target); }
                catch { /* swallow(probe-optional): optional tag metadata remains unavailable */ }
            }
            return null;
        }

        public Dictionary<string, object?> DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun=true)
        {
            EmptyPlcGroupPolicy.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal==null) throw new PlcSoftwareException("InvalidState", "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                // Destructive operations use the shared exact container resolver, never fuzzy PLC selection.
                var sc=_session.ResolveSoftwareContainerUncached(softwarePath);
                var plc=PlcOrganisationPrimitives.SoftwareOrNull(sc) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                PlcBlockUserGroup? Find(string[] parts)
                {
                    PlcBlockGroup current=PlcOrganisationPrimitives.BlockGroup(plc);
                    foreach(var name in parts)
                    {
                        var matches=PlcOrganisationPrimitives.Groups(current)
                            .Where(g=>string.Equals(PlcOrganisationPrimitives.Name(g),name,StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                        if(matches.Count==0) return null;
                        if(matches.Count!=1) throw new PlcSoftwareException("InvalidParams","Ambiguous user group: " + name);
                        current=matches[0];
                    }
                    return current as PlcBlockUserGroup ?? throw new PlcSoftwareException("InvalidParams","Only user block groups can be deleted.");
                }
                var result=EmptyPlcGroupPolicy
                    .Execute(groupPath,dryRun,Find,g=>PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Blocks(g)),g=>PlcOrganisationPrimitives
                        .Count(PlcOrganisationPrimitives.Groups(g)),
                    ()=>PlcOrganisationPrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath,plc))?.ToString() ?? "Unknown",
                    g=> { MutationStarted = true; PlcOrganisationPrimitives.Delete(g); });
                result["softwarePath"]=softwarePath;
                result["resolvedSoftwareName"]=PlcOrganisationPrimitives.Name(plc);
                result["success"]=true;
                return result;
            }
        }

        private static int CountOf(object? collection)
        {
            if (collection == null) return 0;
            if (TryGetPropertyValue(collection, "Count") is int n) return n;
            if (collection is IEnumerable e and not string) return e.Cast<object?>().Count(x => x != null);
            return 0;
        }

        private static bool TryInvokeVoidMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(target, Array.Empty<object>());
            return true;
        }

#if PLC_SOFTWARE_PROTECTION
        public HardwareAddressingReply ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "",
                bool confirmProtectionChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcBlockProtection", meta => {
                bool writing = PlcProtectionPolicy.ValidateProtectionRequest(action, password, confirmProtectionChange, dryRun);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                _session.ExactPlcForEngineering(softwarePath, writing);
                var block = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock
                    ?? throw new ArgumentException("blockPath must identify a block.");
                var provider = PlcOrganisationPrimitives.Protection(block)
                    ?? throw new NotSupportedException("PlcBlockProtectionProvider unavailable on this block/version.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["before"] = new Dictionary<string, object?> {
                    ["name"] = PlcOrganisationPrimitives.Name(block), ["isKnowHowProtected"] = PlcOrganisationPrimitives.IsKnowHowProtected(block),
                        ["programmingLanguage"] = PlcOrganisationPrimitives.Language(block).ToString()
                };
                var invalid = PlcOrganisationPrimitives.InvalidPasswordCharacters(provider)?.ToArray() ?? Array.Empty<char>();
                meta["invalidPasswordCharacters"] = new List<object?>(invalid.Select(c => (object?)PlcSoftwareValues.Value(c.ToString())!).ToArray());
                if (action == "read") return "Block know-how protection state read.";
                bool expected = action == "protect";
                if (PlcOrganisationPrimitives.IsKnowHowProtected(block) == expected)
                    throw new InvalidOperationException(expected ? "Block is already know-how protected; unprotect first."
                        : "Block is not know-how protected.");
                if (expected && PlcProtectionPolicy.ContainsInvalidPasswordCharacter(password, invalid))
                    throw new ArgumentException("Password contains characters rejected by the native password policy.");
                if (!writing) return "Block protection preview; no changes.";
                meta["mayHaveChanged"] = true;
                using (var secure = PlcProtectionPolicy.ToSecureString(password))
                {
                    if (!EngineeringCredentialRules.TrySetPassword(() => {
                        if (expected) PlcOrganisationPrimitives.Protect(provider, secure); else PlcOrganisationPrimitives.Unprotect(provider, secure);
                    }, meta)) return EngineeringCredentialRules.PasswordPolicyRefusal;
                }
                meta["apiCallSuccess"] = true;
                meta["after"] = new Dictionary<string, object?> { ["isKnowHowProtected"] = PlcOrganisationPrimitives.IsKnowHowProtected(block) };
                if (PlcOrganisationPrimitives.IsKnowHowProtected(block) != expected)
                    throw new InvalidOperationException("Protection readback differs from the requested state.");
                return "Block know-how protection changed and verified by readback; no save/compile/download.";
            });

#else
        public HardwareAddressingReply ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "",
                bool confirmProtectionChange = false, bool dryRun = true)
            => throw new NotSupportedException("PlcBlockProtectionProvider requires V15.1 or later.");
#endif

        public Dictionary<string, object?> CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            PlcGroupCreation.Parse(groupPath);
            if (IsProjectNull() || _session.CurrentPortal == null)
                throw new PlcSoftwareException("InvalidState", "No TIA project is open.");
            lock (_blockGroupDeleteGate)
            {
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcOrganisationPrimitives.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                var result = PlcGroupCreation.Execute<PlcTypeGroup>(PlcOrganisationPrimitives.TypeGroup(plc), groupPath, dryRun,
                    g => PlcOrganisationPrimitives.Groups(g), g => PlcOrganisationPrimitives.Name(g), (g, name) => {
                        MutationStarted = true; return PlcOrganisationPrimitives.Create(PlcOrganisationPrimitives.Groups(g), name); });
                result["softwarePath"] = softwarePath;
                result["resolvedSoftwareName"] = PlcOrganisationPrimitives.Name(plc);
                return result;
            }
        }

        public HardwareAddressingReply ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "",
                bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcUserGroup", meta => {
                // Emptiness covers every documented object composition of the group (API audit 2026-10-19 F3, F19):
                // PlcTypeGroup.Documents (V20/V21) and PlcWatchAndForceTableGroup.ForceTables.
                var shape = family switch {
                    "blocks" => ("BlockGroup", "Blocks"),
#if PLC_DOCUMENT_EXPORT
                    "types" => ("TypeGroup", "Types,Documents"),
#else
                    "types" => ("TypeGroup", "Types"),
#endif
                    "tags" => ("TagTableGroup", "TagTables"), "technology" => ("TechnologicalObjectGroup", "TechnologicalObjects"),
                    "watchTables" => ("WatchAndForceTableGroup", "WatchTables,ForceTables"), "externalSources" => ("ExternalSourceGroup", "ExternalSources"),
                    _ => throw new ArgumentException("family must be blocks, types, tags, technology, watchTables or externalSources.") };
                PlcGroupOperations.Parts(groupPath);
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var plc = PlcOrganisationPrimitives.SoftwareOrNull(_session.ResolveSoftwareContainerUncached(softwarePath)) as PlcSoftware
                    ?? throw new PlcSoftwareException("NotFound", "PLC software not found: " + softwarePath);
                meta["softwarePath"] = softwarePath; meta["resolvedSoftwareName"] = PlcOrganisationPrimitives.Name(plc);
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (!dryRun && PlcOrganisationPrimitives.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString() != "Offline")
                    throw new PlcSoftwareException("InvalidState", "Confirmed Offline state is required for group editing.");
                if (!dryRun) meta["mayHaveChanged"] = true;
                meta["result"] = PlcGroupOperations.Manage(PlcGroupOperations.Get(plc, shape.Item1), groupPath, action, newName, dryRun, shape.Item2);
                if (action != "deleteEmpty" || dryRun) {
                    try {
                        meta["group"] =
                            UserGroupRow(PlcGroupOperations
                                .Group(PlcGroupOperations.Get(plc, shape.Item1), action == "rename" && !dryRun ? string
                                    .Join("/", PlcGroupOperations.Parts(groupPath).Take(PlcGroupOperations.Parts(groupPath).Length - 1).Append(newName))
                                    : groupPath));
                    } catch (Exception ex) { meta["groupError"] = ex.GetBaseException().Message; }
                }
                return dryRun ? "Group operation preview; nothing changed." : "Group operation completed. Project not saved.";
            });

        private static Dictionary<string, object?> UserGroupRow(object group) => group switch
        {
            global::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup b => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(b), ["groupClass"] = b.GetType().Name, ["blocks"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.Blocks(b)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(b))
            },
#if PLC_DOCUMENT_EXPORT
            global::Siemens.Engineering.SW.Types.PlcTypeUserGroup ty => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(ty), ["groupClass"] = ty.GetType().Name, ["types"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.Types(ty)), ["documents"] = PlcGroupOperations.Items(ty.Documents).Count(), ["groups"] =
                    PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(ty))
            },
#else
            global::Siemens.Engineering.SW.Types.PlcTypeUserGroup ty => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(ty), ["groupClass"] = ty.GetType().Name, ["types"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.Types(ty)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(ty))
            },
#endif
            global::Siemens.Engineering.SW.Tags.PlcTagTableUserGroup tg => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(tg), ["groupClass"] = tg.GetType().Name, ["tagTables"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.TagTables(tg)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(tg))
            },
#if PLC_SOFTWARE_PROTECTION
            global::Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup w => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(w), ["groupClass"] = w.GetType().Name, ["watchTables"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.WatchTables(w)), ["forceTables"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.ForceTables(w)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(w))
            },
#endif
            global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup e => new Dictionary<string, object?> {
                ["name"] = PlcOrganisationPrimitives.Name(e), ["groupClass"] = e.GetType().Name, ["externalSources"] = PlcOrganisationPrimitives
                    .Count(PlcOrganisationPrimitives.ExternalSources(e)), ["groups"] = PlcOrganisationPrimitives.Count(PlcOrganisationPrimitives.Groups(e))
            },
            _ => new Dictionary<string, object?> { ["name"] = PlcGroupOperations.Get(group, "Name").ToString(), ["groupClass"] = group.GetType().Name }
        };

        public PlcBlockGroup? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>();
            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcOrganisationPrimitives.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcOrganisationPrimitives
                    .BlockGroup(plcSoftware) == null)
            {
                return null;
            }

            var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            PlcBlockGroup currentGroup = PlcOrganisationPrimitives.BlockGroup(plcSoftware);
            foreach (var groupName in groupNames)
            {
                var next = PlcOrganisationPrimitives.Groups(currentGroup)
                    .FirstOrDefault(g => PlcOrganisationPrimitives.Name(g).Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    MutationStarted = true;
                    next = PlcOrganisationPrimitives.Create(PlcOrganisationPrimitives.Groups(currentGroup), groupName);
                    created.Add(groupName);
                    _session.GroupCreated(groupName);
                }
                currentGroup = next;
            }
            return currentGroup;
        }

        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
        {
            if (IsProjectNull())
            {
                throw new PlcSoftwareException("InvalidState", "No project is open in TIA Portal");
            }

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcOrganisationPrimitives.SoftwareOrNull(softwareContainer) is not PlcSoftware plcSoftware || PlcOrganisationPrimitives
                    .BlockGroup(plcSoftware) == null)
            {
                throw new PlcSoftwareException("NotFound", $"PlcSoftware not found at '{softwarePath}'");
            }

            // 1) find the block anywhere by exact name
            var all = new List<PlcBlock>();
            _session.GetBlocksRecursive(PlcOrganisationPrimitives.BlockGroup(plcSoftware), all);
            var block = all.FirstOrDefault(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null)
            {
                throw new PlcSoftwareException("NotFound", $"Block '{blockName}' not found in '{softwarePath}'");
            }

            // An OB's type, number and event assignment are not verified to survive a delete/import round trip.
            // Do not delete an original OB through this generic move route.
            if (block is OB)
            {
                var existingGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                if (existingGroup != null && ReferenceEquals(PlcOrganisationPrimitives.Parent(block), existingGroup))
                    return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
                throw new PlcSoftwareException("NotSupportedOnVersion",
                    "Moving organization blocks by export/delete/import is disabled: the OB type, number and event assignment are not verified to survive " +
                        "the round trip. Move this OB in TIA Portal.");
            }

            // API audit 2026-10-19 F22: the round trip deletes the source block. A know-how-protected block exports only its public
            // interface and cannot be imported again ("Exporting blocks with know-how protection", "Basic principles of
            // importing/exporting"), and only consistent blocks export ("Exporting blocks"). Both are refused before any change.
            var refusal = PlcMovePolicy.Refusal(block.IsKnowHowProtected, block.IsConsistent);
            if (refusal != null) throw new AdapterPreconditionException(refusal, "blockName", false);
            int? numberBefore = PlcMovePolicy.Number(((IEngineeringObject)block).GetAttribute("Number"));

            // 2) ensure the target group exists
            var targetGroup = autoCreateGroup
                ? EnsurePlcBlockGroup(softwarePath, targetGroupPath, out _)
                : _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            if (targetGroup == null)
            {
                throw new PlcSoftwareException("NotFound",
                    $"Target block group '{targetGroupPath}' not found (set autoCreateGroup=true to create it)");
            }

            // already in the target group?
            if (ReferenceEquals(PlcOrganisationPrimitives.Parent(block), targetGroup))
            {
                return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
            }
            if (PlcOrganisationPrimitives.Blocks(targetGroup).Any(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase)))
                throw new PlcSoftwareException("InvalidParams",
                    $"Target group already contains '{blockName}'; relocation never overwrites another block.");

            // 3) export -> delete -> import into target group (no native reparent). Only SimaticML XML is used: it carries the
            // block number, while SIMATIC SD documents do not store it and TIA assigns a new number on import (STEP 7 help V20,
            // "Exporting and importing blocks in SIMATIC SD format"). The recovery export includes default values (WithDefaults).
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_move", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            const string method = "xml(SimaticML)";
            bool moveVerified = false;
            int? numberAfter = null;
            try
            {
                var xml = Path.Combine(tempDir, blockName + ".xml");
                PlcOrganisationPrimitives.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ExportOptions.WithDefaults);
                MutationStarted = true;
                PlcOrganisationPrimitives.Delete(block);
                var imp = PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks(targetGroup), new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(xml)), ImportOptions.None);
                if (imp == null || imp.Count == 0)
                {
                    throw new PlcSoftwareException("ImportFailed",
                        $"Re-import of '{blockName}' into '{targetGroupPath}' failed (xml)");
                }
                var verifyGroup = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                var moved = PlcOrganisationPrimitives.BlocksOrNull(verifyGroup)?
                    .FirstOrDefault(b => PlcOrganisationPrimitives.Name(b).Equals(blockName, StringComparison.OrdinalIgnoreCase));
                numberAfter = moved == null ? null : PlcMovePolicy.Number(((IEngineeringObject)moved).GetAttribute("Number"));
                var numberChange = PlcMovePolicy.NumberChange(numberBefore, numberAfter);
                moveVerified = verifyGroup != null && moved != null && numberChange == null;
                if (verifyGroup == null || moved == null) throw new PlcSoftwareException("ImportFailed",
                    $"Move of '{blockName}' to '{targetGroupPath}' could not be verified");
                if (numberChange != null)
                    throw new PlcSoftwareException("ImportFailed", $"Block '{blockName}' was moved to '{targetGroupPath}' but {numberChange}");
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed",
                    $"Move failed: {ex.Message}. Recovery export retained at '{tempDir}'; inspect the source and destination before retrying.", null, ex);
            }
            finally
            {
                // A failed import may have already deleted the source. Never erase
                // its only recovery export during exception cleanup.
                if (moveVerified)
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch {  /* swallow(cleanup): Cleanup after verified relocation must not replace the successful move result. *//* best-effort cleanup */ }
            }

            return $"Moved '{blockName}' to '{targetGroupPath}' via {method}; block number {PlcMovePolicy.Text(numberAfter)} kept";
        }
    }
}
