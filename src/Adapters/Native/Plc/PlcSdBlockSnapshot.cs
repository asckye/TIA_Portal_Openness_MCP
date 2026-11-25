#if PLC_DOCUMENT_EXPORT
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
    // Keep each inspected declaration read-locked until the corresponding native import returns.
    internal sealed class SdDeclarationFiles : IDisposable
    {
        private readonly List<FileStream> files = new();
        internal string Read(FileInfo file)
        {
            var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(stream);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
            return reader.ReadToEnd();
        }
        public void Dispose() { foreach (var file in files) file.Dispose(); }
    }

    internal sealed class PlcSdBlockSnapshot
    {
        private readonly SdBlockDeclaration declaration;
        private readonly string name;
        private readonly Dictionary<string, object?>? before;
        private readonly bool replace;
        internal PlcBlock? Existing { get; }
        internal string? Refusal { get; }
        internal Dictionary<string, object?> Evidence { get; } = new();

        internal PlcSdBlockSnapshot(string text, string name, Func<PlcBlock?> find, bool replace, Func<bool> elsewhere)
        {
            declaration = SdBlockDeclaration.Scan(text);
            string kind = declaration.RequireBlock(name);
            this.name = name;
            Existing = find();
            this.replace = replace;
            Evidence["existingBlock"] = null;
            Evidence["notCarried"] = SdBlockOverridePolicy.NotCarried;
            Evidence["comparisonScope"] = "SD text comparison covers .s7dcl/.s7res after line-ending normalization. " +
                "OB kind, number and DB attributes require separate block property readback.";
            Evidence["contentVerified"] = null;
            try
            {
                if (Existing != null)
                {
                    before = Read(Existing);
                    Evidence["existingBlock"] = BlockRow(before);
                }
                Refusal = SdBlockOverridePolicy.Refusal(name, kind, replace, replace && elsewhere(), before);
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
            var recovery = NativeExportPolicy.SingleImportRecovery(new[] { target }, PlcBatchImportRunner.SingleImportRecoveryDirectory,
                DataLocations.Current.RecoveryAttemptedPath);
            Evidence["recoveryDirectory"] = recovery.Directory;
            if (recovery.Warning != null) Evidence["backupWarning"] = recovery.Warning;
            recovery.RequireExportableBackups();
        }

        internal static Dictionary<string, object?> Read(PlcBlock block)
        {
            var attributes = new Dictionary<string, object?>();
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
            bool restore = replace && before != null && !declaration.Pragmas.ContainsKey("S7_BlockNumber") && !Equals(before["Number"], after["Number"]);
            if (restore)
            {
                try { RestoreNumber(imported, (int)before!["Number"]!, (bool)before["AutoNumber"]!); }
                catch (Exception error) when (!HmiReadPolicy.ConnectionUnavailable(error)) { setterFailure = error.Message; }
                after = Read(Find());
            }
            Evidence["importedBlock"] = BlockRow(after);
            Evidence["numberRestored"] = restore && setterFailure == null && Equals(before!["Number"], after["Number"])
                && Equals(before["AutoNumber"], after["AutoNumber"]);
            var differences = SdBlockOverridePolicy.Compare(replace ? before : null, after, declaration);
            if (setterFailure != null)
            {
                var difference = differences.FirstOrDefault(item => Equals(item["name"], "Number"));
                if (difference == null)
                {
                    difference = new() { ["name"] = "Number", ["before"] = before!["Number"], ["after"] = after["Number"], ["restored"] = false };
                    differences.Add(difference);
                }
                difference["restoreError"] = setterFailure;
            }
            Evidence["propertyDifferences"] = differences;
            if (replace && before != null || differences.Count != 0) Evidence["propertiesPreserved"] = differences.Count == 0;
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
