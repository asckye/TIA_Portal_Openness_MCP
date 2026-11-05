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
            => ManagePlcUserGroup(softwarePath, "blocks", groupPath, "deleteEmpty", dryRun: dryRun).Meta;

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
            => ManagePlcUserGroup(softwarePath, "types", groupPath, "create", dryRun: dryRun).Meta;

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
            if (IsProjectNull()) return null;
            var reply = ManagePlcUserGroup(softwarePath, "blocks", groupPath, "create", dryRun: false);
            if (reply.RequiresSessionReset || reply.MayHaveChanged) MutationStarted = true;
            if (!reply.Meta.TryGetValue("operationSuccess", out var success) || !Equals(success, true))
                throw new PlcSoftwareException("OpennessError", reply.Message);
            if (reply.Meta.TryGetValue("result", out var result) && result is Dictionary<string, object?> details
                && details.TryGetValue("createdPaths", out var paths) && paths is IEnumerable<object?> values)
                created.AddRange(values.Select(value => value!.ToString()!).Select(path => path.Substring(path.LastIndexOf('/') + 1)));
            return _session.GetPlcBlockGroupByPath(softwarePath, groupPath);
        }

        public HardwareAddressingReply MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true,
            string recoveryPath = "", bool dryRun = true, string expectedPlanHash = "")
        {
            if (IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open in TIA Portal");
            PlcGroupOperations.Parts(targetGroupPath, true);
            var block = _session.GetBlock(softwarePath, blockName)
                ?? throw new PlcSoftwareException("NotFound", $"Block '{blockName}' not found in '{softwarePath}'");
            var parent = PlcOrganisationPrimitives.Parent(block);
            var target = _session.GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            // An OB's type, number and event assignment are not verified to survive the round trip.
            if (block is OB)
            {
                if (target != null && object.Equals(parent, target))
                    return new HardwareAddressingReply { Message = "Block is already in the target group.",
                        Meta = new Dictionary<string, object?>(StringComparer.Ordinal) { ["operationSuccess"] = true, ["mayHaveChanged"] = false,
                            ["dryRun"] = dryRun, ["plannedChangeCount"] = 0 } };
                throw new PlcSoftwareException("NotSupportedOnVersion",
                    "Moving organization blocks by export/delete/import is disabled: the OB type, number and event assignment are not verified to survive " +
                        "the round trip. Move this OB in TIA Portal.");
            }
            var refusal = PlcMovePolicy.Refusal(block.IsKnowHowProtected, block.IsConsistent);
            if (refusal != null) throw new AdapterPreconditionException(refusal, "blockName", false);
            string name = PlcOrganisationPrimitives.Name(block);
            int number = PlcOrganisationPrimitives.Number(block);
            string language = PlcOrganisationPrimitives.Language(block).ToString();
            var plan = new Dictionary<string, object?>(StringComparer.Ordinal) { ["softwarePath"] = softwarePath, ["sourcePath"] = _session.GetBlockPath(block),
                ["name"] = name, ["number"] = number, ["language"] = language,
                ["targetGroupPath"] = targetGroupPath, ["autoCreateGroup"] = autoCreateGroup,
                ["binding"] = _session.BindingIdentity(), ["targetExists"] = target != null };
            if (target != null) plan["targetBlocks"] = string
                .Join("|", PlcOrganisationPrimitives.Blocks(target)
                    .Select(item => PlcOrganisationPrimitives.Name(item) + ":" + PlcOrganisationPrimitives.Number(item))
                    .OrderBy(value => value, StringComparer.Ordinal));
            string planJson = _session.SerializeEvidence(plan);
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = string
                    .Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(planJson))
                        .Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            var meta = new Dictionary<string, object?>(StringComparer.Ordinal) { ["planHash"] = hash, ["plan"] = plan, ["dryRun"] = dryRun,
                ["operationSuccess"] = true, ["mayHaveChanged"] = false, ["plannedChangeCount"] = target != null && object.Equals(parent, target) ? 0 : 1,
                ["validationScope"] =
                    "Source identity/number/consistency and target inventory; the recovery export identity is verified before deletion; target import " +
                    "failure triggers restoration to the original group."
                };
            if (dryRun) return new HardwareAddressingReply { Message = "Move preview; no export, import or deletion.", Meta = meta };
            if (expectedPlanHash != hash) throw new AdapterPreconditionException("Move plan changed; preview again.", "expectedPlanHash", false);
            if (target != null && object.Equals(parent, target)) return new HardwareAddressingReply {
                Message = "Block is already in the target group.", Meta = meta };
            if (target == null && !autoCreateGroup) throw new AdapterPreconditionException("Target group does not exist.", "targetGroupPath", false);
            string root = Path.GetFullPath(TiaOpenness.Shared.DataLocations.Current.RecoveryAttemptedPath).TrimEnd(Path.DirectorySeparatorChar) + Path
                .DirectorySeparatorChar;
            string recovery = recoveryPath.Length == 0 ? Path.Combine(root, "plc-move-" + Guid.NewGuid().ToString("N"), "original.xml") : Path
                .GetFullPath(recoveryPath);
            if (!recovery.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Recovery path is outside the recovery directory.", nameof(recoveryPath));
            using var editAccess = _session.AcquireHmiEditAccess();
            var movePlc = _session.ExactPlcForEngineering(softwarePath, true);
            PlcBlock? imported = null; PlcBlock? restored = null;
            void Verify(PlcBlock? candidate, object expectedParent)
            {
                if (candidate == null || !object.Equals(PlcOrganisationPrimitives.Parent(candidate), expectedParent)
                    || PlcOrganisationPrimitives.Name(candidate) != name || PlcOrganisationPrimitives.Number(candidate) != number
                    || PlcOrganisationPrimitives.Language(candidate).ToString() != language)
                    throw new IOException("The imported block parent/name/number/language could not be verified.");
            }
            var evidence = PlcMovePolicy.ExportDeleteImportRestore(recovery, name, number, language, () => {
                TiaMcp.Adapters.PlcBatchImportRecovery.Precheck(); Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                if (File.Exists(recovery)) throw new IOException("The recovery path already exists.");
                PlcOrganisationPrimitives.Export(block, new FileInfo(recovery), ExportOptions.WithDefaults);
            }, () => {
                MutationStarted = true;
                PlcOrganisationPrimitives.Delete(block);
                if (PlcOrganisationPrimitives.Blocks((PlcBlockGroup)parent).Any(item => PlcOrganisationPrimitives.Name(item) == name))
                    throw new IOException("Source block remains after delete.");
            }, () => {
                if (target == null) {
                    PlcGroupOperations.Manage(PlcOrganisationPrimitives.BlockGroup(movePlc), targetGroupPath, "create", "", false, "Blocks"); target = _session
                        .GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
                }
                var copies = PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks(target!), new FileInfo(recovery), ImportOptions.None);
                if (copies == null || copies.Count != 1) throw new IOException("Import did not return exactly one target block.");
                imported = copies[0];
                return imported.IsConsistent;
            }, () => Verify(imported, target!), () => {
                PlcOrganisationPrimitives.Delete(imported!);
            }, () => {
                var copies = PlcOrganisationPrimitives.Import(PlcOrganisationPrimitives.Blocks((PlcBlockGroup)parent),
                    new FileInfo(recovery), ImportOptions.None);
                if (copies == null || copies.Count != 1) throw new IOException("Restore did not return exactly one original-group block.");
                restored = copies[0];
            }, () => Verify(restored, parent));
            foreach (var pair in evidence) meta[pair.Key] = pair.Value;
            meta["operationSuccess"] = true; meta["mayHaveChanged"] = true;
            string message = "Source exported and verified, deleted, then imported and verified in the target group. Recovery export retained.";
            if (evidence["importedConsistent"] is false) message += " Compile the imported block before use; it is currently inconsistent.";
            return new HardwareAddressingReply { Message = message, Meta = meta };
        }
    }
}
