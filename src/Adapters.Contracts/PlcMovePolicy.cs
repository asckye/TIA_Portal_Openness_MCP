using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // Move admission and recovery: verify the export before deletion, restore after a failed target import.
    public static class PlcMovePolicy
    {
        public static Dictionary<string, object?> ExportDeleteImportRestore(string recoveryPath, string name, int number, string language,
            Action exportSource, Action deleteSource, Func<bool> importTarget, Action verifyTarget, Action deleteTarget, Action restoreSource,
                Action verifyRestored, bool dryRun = false)
        {
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal) { ["mayHaveChanged"] = false,
                ["deleteStarted"] = false, ["nativeMutationIssued"] = false, ["originalRetained"] = true, ["restored"] = false, ["restoreAttempted"] = false };
            if (dryRun) return evidence;
            string stage = "backup";
            try
            {
                exportSource();
                stage = "verify-export";
                if (!File.Exists(recoveryPath)) throw new IOException("The recovery export is missing.");
                var document = SimaticMlImportWindow.Load(File.ReadAllBytes(recoveryPath));
                var blocks = document.Root?.Elements().Where(element => element.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).ToArray();
                var attributes = blocks?.Length == 1 ? blocks[0].Elements().SingleOrDefault(element => element.Name.LocalName == "AttributeList") : null;
                string? Attribute(string key) => attributes?.Elements().SingleOrDefault(element => element.Name.LocalName == key)?.Value;
                if (document.Root?.Name.LocalName != "Document" || Attribute("Name") != name
                    || Number(Attribute("Number")) != number || Attribute("ProgrammingLanguage") != language)
                    throw new IOException("The recovery export does not match the source block name, number and language.");
                evidence["recoveryExportVerified"] = true;
                stage = "delete-source";
                evidence["recoveryExportPath"] = recoveryPath;
                evidence["mayHaveChanged"] = true; evidence["nativeMutationIssued"] = true; evidence["originalRetained"] = false;
                evidence["deleteStarted"] = true;
                deleteSource();
                evidence["sourceDeleted"] = true;
                stage = "import"; evidence["importedConsistent"] = importTarget();
                stage = "verify"; verifyTarget();
                evidence["importedVerified"] = true;
                return evidence;
            }
            catch (Exception error)
            {
                evidence["stage"] = stage; evidence["error"] = error.Message;
                string message = "Move failed during " + stage + ". Recovery file retained at '" + recoveryPath + "'. " + error.Message;
                if (stage is "import" or "verify")
                {
                    bool canRestore = true;
                    if (stage == "verify")
                    {
                        try
                        {
                            deleteTarget(); evidence["targetCleanup"] = "deleted";
                        }
                        catch (Exception cleanupError)
                        {
                            evidence["targetCleanup"] = "failed"; evidence["targetCleanupError"] = cleanupError.Message;
                            evidence["recoveryOnlyCopy"] = true; canRestore = false;
                            message += " Target cleanup failed: " + cleanupError.Message
                                + ". Restoration was not attempted. The only verified recovery copy is '" + recoveryPath + "'.";
                        }
                    }
                    if (canRestore)
                    {
                        evidence["restoreAttempted"] = true;
                        try
                        {
                            restoreSource(); verifyRestored();
                            evidence["restored"] = true; evidence["originalRetained"] = true;
                            message += " The block was restored and verified in the original group; the project may have changed.";
                        }
                        catch (Exception restoreError)
                        {
                            evidence["restoreError"] = restoreError.Message; evidence["recoveryOnlyCopy"] = true;
                            message += " Automatic restoration failed: " + restoreError.Message + ". The only verified recovery copy is '" + recoveryPath
                                + "'.";
                        }
                    }
                }
                var failure = new IOException(message, error);
                failure.Data["nativeResultEvidence"] = evidence;
                throw failure;
            }
        }

        public static string? Refusal(bool knowHowProtected, bool consistent)
        {
            if (knowHowProtected)
                return "The block is know-how protected: its export holds only the public interface and cannot be imported again, so moving it would lose " +
                    "its code. Nothing was changed; move it in TIA Portal.";
            if (!consistent)
                return "The block is inconsistent: TIA exports only consistent blocks. Compile it first; nothing was changed.";
            return null;
        }

        public static int? Number(object? raw)
            => raw != null && int
                .TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value
                : (int?)null;

        public static string Text(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "(unreadable)";

        // Null when the number is unchanged; otherwise the report text.
        public static string? NumberChange(int? before, int? after)
            => before.HasValue && after.HasValue && before.Value == after.Value ? null
                : "its block number changed or could not be read back (before " + Text(before) + ", after " + Text(after)
                    + "); check references to the old number.";
    }
}
