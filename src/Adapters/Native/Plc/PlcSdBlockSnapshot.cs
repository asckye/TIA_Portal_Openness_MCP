#if PLC_DOCUMENT_EXPORT
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaOpenness.Shared;
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed class SdDeclarationFiles
    {
        private readonly Dictionary<string, (string Code, string? Resource)> admitted = new(StringComparer.OrdinalIgnoreCase);

        internal string Read(FileInfo file)
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            string digest = Hash(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
            string text = reader.ReadToEnd();
            admitted.Add(file.FullName, (digest, ResourceHash(file)));
            return text;
        }

        internal void RequireUnchanged(FileInfo file)
        {
            var expected = admitted[file.FullName];
            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Hash(stream) == expected.Code && ResourceHash(file) == expected.Resource) return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw AdapterPreconditionException.Failed("Cannot recheck the admitted SD files for " + file.Name + ": " + error.Message,
                    "sd-input-changed", "importPath");
            }
            throw AdapterPreconditionException.Failed("SD declaration or resource changed after admission for " + file.Name + ".",
                "sd-input-changed", "importPath");
        }

        private static string? ResourceHash(FileInfo file)
        {
            string resource = Path.ChangeExtension(file.FullName, ".s7res");
            // Fail admission if the directory cannot be inspected or a present resource cannot be opened.
            if (!Directory.EnumerateFileSystemEntries(file.DirectoryName!, Path.GetFileName(resource)).Any()) return null;
            using var stream = File.OpenRead(resource);
            return Hash(stream);
        }

        private static string Hash(Stream stream)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }
    }

    internal sealed class PlcSdBlockSnapshot
    {
        private readonly SdBlockDeclaration declaration;
        private readonly string name;
        private BlockAttributes? before;
        private readonly Func<PlcBlock?> find;
        private readonly Func<bool> elsewhere;
        private readonly bool replace;
        internal PlcBlock? Existing { get; private set; }
        internal string? Refusal { get; private set; }
        internal Dictionary<string, object?> Evidence { get; } = new();

        internal PlcSdBlockSnapshot(string text, string name, Func<PlcBlock?> find, bool replace, Func<bool> elsewhere)
        {
            declaration = SdBlockDeclaration.Scan(text);
            declaration.RequireBlock(name);
            this.name = name;
            this.find = find;
            this.elsewhere = elsewhere;
            this.replace = replace;
            Evidence["notCarried"] = SdBlockOverridePolicy.NotCarried;
            Evidence["comparisonScope"] = "SD text comparison covers .s7dcl/.s7res after line-ending normalization. " +
                "OB kind, number and DB attributes require separate block property readback.";
            Evidence["contentVerified"] = null;
            Inspect();
        }

        private void Inspect()
        {
            Existing = find();
            before = null;
            Evidence["existingBlock"] = null;
            Evidence["notCompared"] = Array.Empty<string>();
            try
            {
                if (Existing != null)
                {
                    before = Read(Existing);
                    Evidence["existingBlock"] = BlockRow(before);
                    Evidence["notCompared"] = before.NotCompared.ToArray();
                }
                Refusal = SdBlockOverridePolicy.Refusal(name, declaration.Declarations[0].Kind, replace,
                    replace && elsewhere(), before, declaration.InstanceType);
            }
            catch (Exception error) when (!HmiReadPolicy.ConnectionUnavailable(error))
            {
                Refusal = $"Block '{name}': {error.Message} SIMATIC SD preservation cannot be established. " +
                    "Use a SimaticML export with ImportPlcBlock, ImportPlcBlockVerified or ImportPlcBlocksFromDirectory.";
            }
            Evidence["applyBlocked"] = Refusal != null;
            Evidence["applyBlockedReason"] = Refusal;
            Evidence["applyBlockedReasons"] = Refusal == null ? Array.Empty<string>() : new[] { Refusal };
        }

        internal static Dictionary<string, PlcSdBlockSnapshot> PrepareBatch(FileInfo[] selected, SdDeclarationFiles inputs,
            PlcBlockGroup group, bool replace, Func<string, bool> elsewhere, bool dryRun = false)
        {
            var plans = new Dictionary<string, PlcSdBlockSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected)
            {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                var plan = new PlcSdBlockSnapshot(inputs.Read(file), name, () => group.Blocks.Find(name), replace,
                    () => elsewhere(name));
                if (!dryRun) plan.RequireAdmission();
                plans.Add(name, plan);
            }
            if (!dryRun) foreach (var plan in plans.Values) plan.Backup();
            return plans;
        }

        // A previous batch import can renumber or replace this target after the initial admission and backup.
        internal void RefreshForImport()
        {
            var admitted = before;
            var previousTarget = Existing;
            Inspect();
            RequireAdmission();
            if (!replace || (before == null && admitted == null)) return;
            if (Equals(previousTarget, Existing) && before != null && admitted != null && before.NotCompared.Count == 0
                && admitted.NotCompared.Count == 0 && before.Count == admitted.Count &&
                before.All(pair => admitted.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value))) return;
            if (Evidence.TryGetValue("recoveryDirectory", out var initial)) Evidence["initialRecoveryDirectory"] = initial;
            Evidence.Remove("recoveryDirectory");
            Evidence.Remove("backupWarning");
            Backup();
        }

        internal void RequireAdmission()
        {
            if (Refusal != null) throw AdapterPreconditionException.Failed(Refusal, "sd-override-not-preserved", "name");
        }

        internal void Backup()
        {
            var existing = Existing;
            if (!replace || existing == null) return;
            var target = new NativeExportPolicy.RecoveryTarget { Object = name,
                InspectBlocker = () => NativeExportPolicy.ExportBlocker(existing.IsConsistent),
                Export = file => existing.Export(file, ExportOptions.WithDefaults) };
            var recovery = NativeExportPolicy.SingleImportRecovery(new[] { target }, PlcBatchImportRecovery.Directory,
                DataLocations.Current.RecoveryAttemptedPath);
            Evidence["recoveryDirectory"] = recovery.Directory;
            if (recovery.Warning != null) Evidence["backupWarning"] = recovery.Warning;
            recovery.RequireExportableBackups();
        }

        private sealed class BlockAttributes : Dictionary<string, object?>
        {
            internal SortedSet<string> NotCompared { get; } = new(StringComparer.Ordinal);

            internal string RecordType(string name, object value)
            {
                NotCompared.Add(name);
                return value.GetType().FullName!;
            }
        }

        private static BlockAttributes Read(PlcBlock block)
        {
            var attributes = new BlockAttributes();
            string reading = "GetAttributeInfos";
            try
            {
                var engineering = (IEngineeringObject)block;
                foreach (var info in engineering.GetAttributeInfos())
                {
                    reading = info.Name;
                    if (SdBlockOverridePolicy.Volatile(reading)) continue;
                    var value = engineering.GetAttribute(reading);
                    attributes[reading] = value switch
                    {
                        null or string or decimal or DateTime or TimeSpan or Guid => value,
                        Enum or Version => value.ToString(),
                        _ when value.GetType().IsPrimitive => value,
                        _ when !SdBlockOverridePolicy.RequiredAttribute(reading, block.GetType().Name) => attributes.RecordType(reading, value),
                        _ => throw new InvalidOperationException("Unsupported attribute value type " + value.GetType().FullName)
                    };
                }
                void Capture(string key, Func<object> read)
                {
                    reading = key;
                    attributes[key] = read();
                }
                attributes["Type"] = block.GetType().Name;
                Capture("ProgrammingLanguage", () => block.ProgrammingLanguage.ToString());
                Capture("Number", () => block.Number);
                Capture("AutoNumber", () => block.AutoNumber);
                Capture("IsKnowHowProtected", () => block.IsKnowHowProtected);
#if PLC_BLOCK_WRITE_PROTECTION
                reading = "IsWriteProtected";
                if (!attributes.ContainsKey(reading) && block.GetService<PlcBlockWriteProtectionProvider>() is { } protection)
                    attributes[reading] = protection.IsProtected;
#endif
                if (block is OB ob) Capture("SecondaryType", () => ob.SecondaryType.ToString());
                return attributes;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(reading + " is unreadable (" + error.Message + ").", error);
            }
        }

        private Dictionary<string, object?> BlockRow(Dictionary<string, object?> snapshot)
        {
            snapshot.TryGetValue("SecondaryType", out var secondary);
            return new() {
                ["name"] = name, ["type"] = snapshot["Type"], ["kind"] = secondary ?? snapshot["Type"],
                ["number"] = snapshot["Number"], ["autoNumber"] = snapshot["AutoNumber"], ["secondaryType"] = secondary,
                ["programmingLanguage"] = snapshot["ProgrammingLanguage"], ["attributes"] = snapshot
            };
        }

        internal bool Verify(Func<PlcBlock?> find)
        {
            PlcBlock Find() => find() ?? throw new InvalidOperationException("Cannot read imported block '" + name + "' in its exact group.");
            var imported = Find();
            var after = Read(imported);
            string? setterFailure = null;
            string restoreProperty = "Number";
            bool restore = replace && before != null && !declaration.Pragmas.ContainsKey("S7_BlockNumber")
                && (!Equals(before["Number"], after["Number"]) || !Equals(before["AutoNumber"], after["AutoNumber"]));
            if (restore)
            {
                try
                {
                    if (!Equals(before!["Number"], after["Number"]))
                        RestoreNumber(imported, (int)before!["Number"]!, (bool)before["AutoNumber"]!);
                    else
                    {
                        restoreProperty = "AutoNumber";
                        Documents.SetAutoNumber(imported, (bool)before["AutoNumber"]!);
                    }
                }
                catch (Exception error) when (!HmiReadPolicy.ConnectionUnavailable(error)) { setterFailure = error.Message; }
                after = Read(Find());
            }
            Evidence["importedBlock"] = BlockRow(after);
            Evidence["numberRestored"] = restore && setterFailure == null && Equals(before!["Number"], after["Number"])
                && Equals(before["AutoNumber"], after["AutoNumber"]);
            var notCompared = new SortedSet<string>(after.NotCompared, StringComparer.Ordinal);
            if (before != null) notCompared.UnionWith(before.NotCompared);
            Evidence["notCompared"] = notCompared.ToArray();
            var differences = SdBlockOverridePolicy.Compare(replace ? before : null, after, declaration, notCompared);
            if (before == null && differences.SingleOrDefault(item => Equals(item["name"], "Number")) is { } number)
            {
                Evidence["warning"] = $"SD requested block number {number["expected"]}. TIA assigned {number["after"]} to new block '{name}'.";
                differences.Remove(number);
            }
            if (setterFailure != null)
            {
                var difference = differences.FirstOrDefault(item => Equals(item["name"], restoreProperty));
                if (difference == null)
                {
                    difference = new() { ["name"] = restoreProperty, ["before"] = before![restoreProperty],
                        ["after"] = after[restoreProperty], ["restored"] = false };
                    differences.Add(difference);
                }
                difference["restoreError"] = setterFailure;
            }
            Evidence["propertyDifferences"] = differences;
            if (differences.Count != 0) Evidence["propertiesPreserved"] = false;
            else if (replace && before != null) Evidence["propertiesPreserved"] = notCompared.Count == 0 ? true : (bool?)null;
            return differences.Count == 0;
        }

        private static void RestoreNumber(PlcBlock imported, int number, bool autoNumber)
        {
            bool reachable = true;
            try
            {
                Documents.SetAutoNumber(imported, false);
                Documents.SetNumber(imported, number);
            }
            catch (Exception error) when (HmiReadPolicy.ConnectionUnavailable(error))
            {
                reachable = false;
                throw;
            }
            finally { if (reachable) Documents.SetAutoNumber(imported, autoNumber); }
        }
    }
}
#endif
