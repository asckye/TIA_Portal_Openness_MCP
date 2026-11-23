#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace TiaMcp.Updater
{
    public static partial class UpdaterEngine
    {
        internal const string SwitchSuffix = ".workbench-update";
        internal static string SwitchDirectory(string root) => NormalizeForCompare(root) + SwitchSuffix;
        internal static bool HasPendingSwitch(string root) => DirectoryExists(SwitchDirectory(root));

        [DataContract]
        private sealed class SwitchPlan
        {
            [DataMember] public string Root = string.Empty;
            [DataMember] public string[] PreviousEntries = Array.Empty<string>();
            [DataMember] public string[] NextEntries = Array.Empty<string>();
            [DataMember] public string[] Retained = Array.Empty<string>();
            [DataMember] public string? HistoryName;
        }

        private static void Publish(string root, string stage, Dictionary<string, string> expected, IEnumerable<KeyValuePair<string, string>> retired,
            Action<string> say, string? history = null, string? historyName = null)
        {
            var remove = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in retired)
            {
                var path = OwnedPath(root, file.Key);
                if (!ExistsFile(path)) continue;
                if (string.Equals(HashFile(path), file.Value, StringComparison.OrdinalIgnoreCase)) remove.Add(file.Key, file.Value);
                else say(UpdaterText.Bilingual("PreserveModified", "preserving modified file") + ": " + file.Key);
            }
            foreach (var file in expected.Keys) OwnedPath(root, file);
            var image = typeof(UpdaterEngine).Assembly.Location;
            PrepareSwitch(root, stage, new HashSet<string>(remove.Keys, StringComparer.OrdinalIgnoreCase), history, historyName,
                Path.GetExtension(image).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? image : null);
            PublishPrepared(root, expected, remove, say);
        }

        private static void VerifyStaged(string directory, Dictionary<string, string> expected, Action<string>? beforeFile = null)
        {
            foreach (var file in expected)
            {
                if (beforeFile != null) RetryLocked(() => beforeFile(file.Key));
                if (HashFile(OwnedPath(directory, file.Key)) != file.Value)
                    throw new InvalidDataException("Staged file verification failed: " + file.Key);
            }
        }

        internal static void PublishPrepared(string root, Dictionary<string, string> expected, Dictionary<string, string> retired, Action<string> say)
        {
            try
            {
                ActivateSwitch(root);
                VerifyStaged(root, expected);
                // A file created or edited during staging must not disappear with the displaced tree.
                string old = Combine(SwitchDirectory(root), "old");
                var replaced = new HashSet<string>(expected.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var file in GetFilesRecursive(old))
                {
                    string relative = RelativeTo(old, file);
                    if (!replaced.Contains(relative) && (!retired.TryGetValue(relative, out var digest)
                        || !string.Equals(HashFile(file), digest, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("The installed file changed during the update: " + relative);
                }
                FinishSwitch(root, say);
            }
            catch
            {
                RecoverSwitch(root, say);
                say(UpdaterText.Bilingual("InstallRestored", "previous install restored") + ": " + root);
                throw;
            }
        }

        // A prepared sibling contains a complete plan before its fixed recovery name becomes visible.
        internal static void PrepareSwitch(string root, string stage, HashSet<string> remove, string? history = null, string? historyName = null,
            string? recoveryImage = null)
        {
            string slot = SwitchDirectory(root), draft = slot + "." + Guid.NewGuid().ToString("N");
            CreateDirectory(draft);
            try
            {
                var retained = new List<string>();
                var affectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in remove)
                    for (int slash = file.LastIndexOf('/'); slash >= 0; slash = file.LastIndexOf('/', slash - 1))
                        affectedDirectories.Add(file.Substring(0, slash));
                CollectRetained(root, root, stage, remove, affectedDirectories, retained);
                var kept = new HashSet<string>(retained, StringComparer.OrdinalIgnoreCase);
                var plan = new SwitchPlan
                {
                    Root = NormalizeForCompare(root),
                    PreviousEntries = GetEntries(root).Select(path => Path.GetFileName(path)!).Where(name => !kept.Contains(name)).ToArray(),
                    NextEntries = GetEntries(stage).Select(path => Path.GetFileName(path)!).ToArray(),
                    Retained = retained.ToArray(),
                    HistoryName = historyName
                };
                ValidatePlan(root, plan);
                MoveEntry(stage, Combine(draft, "next"));
                if (history != null)
                {
                    MoveEntry(history, Combine(draft, "history"));
                    MoveEntry(history + ".update-receipt.json", Combine(draft, "receipt"));
                }
                using (var stream = new FileStream(Prefix(Combine(draft, "plan.json")), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer(typeof(SwitchPlan)).WriteObject(stream, plan);
                    stream.Flush(true);
                }
                // The installed launcher can be absent between renames. Keep this updater version outside both trees.
                if (recoveryImage != null) UpdaterFileStaging.CopyTo(recoveryImage, Combine(draft, "recover"));
                CreateMarker(draft, "step-0");
                MoveEntry(draft, slot);
            }
            finally { if (DirectoryExists(draft)) RemoveTree(draft); }
        }

        private static void CollectRetained(string root, string directory, string stage, HashSet<string> remove,
            HashSet<string> affectedDirectories, List<string> retained)
        {
            foreach (var entry in GetEntries(directory))
            {
                string relative = RelativeTo(root, entry), target = Combine(stage, relative);
                if (remove.Contains(relative)) continue;
                if (!ExistsAny(target) && !affectedDirectories.Contains(relative))
                {
                    retained.Add(relative);
                    CreateDirectory(Path.GetDirectoryName(target)!);
                }
                else if (DirectoryExists(entry) && (File.GetAttributes(Prefix(entry)) & FileAttributes.ReparsePoint) == 0)
                {
                    if (ExistsFile(target)) throw new InvalidDataException("Package file conflicts with installed directory: " + relative);
                    CollectRetained(root, entry, stage, remove, affectedDirectories, retained);
                }
                else if (DirectoryExists(target)) throw new InvalidDataException("Package directory conflicts with installed file: " + relative);
            }
        }

        private static bool SwitchPath(string path) => !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path)
            && path.Split('/').All(part => part.Length > 0 && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !part.EndsWith(".", StringComparison.Ordinal) && !part.EndsWith(" ", StringComparison.Ordinal));
        private static bool TopName(string name) => IsSafeRelativePath(name) && SwitchPath(name) && name.IndexOf('/') < 0 && !IsProtectedRoot(name);
        private static void ValidatePlan(string root, SwitchPlan plan)
        {
            if (plan == null || !string.Equals(NormalizeForCompare(root), plan.Root, StringComparison.OrdinalIgnoreCase)
                || plan.PreviousEntries == null || plan.NextEntries == null || plan.Retained == null
                || !plan.PreviousEntries.All(TopName) || !plan.NextEntries.All(TopName)
                || !plan.Retained.All(SwitchPath) || (plan.HistoryName != null && !TopName(plan.HistoryName)))
                throw new InvalidDataException("Invalid update recovery plan: " + root);
        }

        private static void MoveEntry(string from, string to)
        {
            RetryLocked(() =>
            {
                if (DirectoryExists(from)) Directory.Move(Prefix(from), Prefix(to));
                else File.Move(Prefix(from), Prefix(to));
                return true;
            }, retryAccessDenied: true);
        }

        private static void CreateMarker(string slot, string name)
        {
            using (var file = new FileStream(Prefix(Combine(slot, name)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Flush(true);
        }

        private static List<(string From, string To)> Moves(string root, SwitchPlan plan, bool parts)
        {
            string slot = SwitchDirectory(root), old = Combine(slot, "old"), next = Combine(slot, "next");
            var moves = new List<(string, string)>();
            if (parts)
            {
                foreach (var entry in plan.PreviousEntries) moves.Add((Combine(root, entry), Combine(old, entry)));
                foreach (var entry in plan.NextEntries) moves.Add((Combine(next, entry), Combine(root, entry)));
            }
            else { moves.Add((root, old)); moves.Add((next, root)); }
            foreach (var path in plan.Retained)
                if (!parts || path.Contains("/")) moves.Add((Combine(old, path), Combine(root, path)));
            if (plan.HistoryName != null)
            {
                var archive = Combine(Combine(root, ".previous"), plan.HistoryName);
                moves.Add((Combine(slot, "history"), archive));
                moves.Add((Combine(slot, "receipt"), archive + ".update-receipt.json"));
            }
            return moves;
        }

        internal static void ActivateSwitch(string root, Action<int, bool>? checkpoint = null)
        {
            string slot = SwitchDirectory(root);
            var plan = ReadPlan(root);
            var moves = Moves(root, plan, false);
            for (int i = 0; i < moves.Count; i++)
            {
                CheckMoveParents(root, slot, moves[i]);
                SetStep(slot, i, i + 1);
                checkpoint?.Invoke(i + 1, false);
                try { MoveEntry(moves[i].From, moves[i].To); }
                catch (Exception error) when (i == 0 && moves[i].From == root && DirectoryExists(root)
                    && !DirectoryExists(Combine(slot, "old")) && IsDirectoryInUse(error))
                {
                    // Windows can pin a parent while a data file is open. Keep that parent and its user entries in place.
                    SetStep(slot, 1, 0);
                    CreateMarker(slot, "parts");
                    CreateDirectory(Combine(slot, "old"));
                    moves = Moves(root, plan, true);
                    i = -1;
                    continue;
                }
                checkpoint?.Invoke(i + 1, true);
            }
        }

        private static bool IsDirectoryInUse(Exception error) => error is UnauthorizedAccessException
            || (error is IOException && ((error.HResult & 0xffff) == 5 || (error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33));

        private static void CheckMoveParents(string root, string slot, (string From, string To) move)
        {
            foreach (var path in new[] { move.From, move.To })
            {
                string boundary = IsWithin(path, root) ? root : slot;
                if (!IsWithin(path, boundary)) throw new InvalidDataException("Update path outside the install and staging directories: " + path);
                for (string? parent = path == boundary ? path : Path.GetDirectoryName(path); parent != null && IsWithin(parent, boundary);
                    parent = Path.GetDirectoryName(parent))
                {
                    if (ExistsAny(parent) && (File.GetAttributes(Prefix(parent)) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Reparse point in update recovery path: " + parent);
                    if (string.Equals(parent, boundary, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }

        private static SwitchPlan ReadPlan(string root)
        {
            string slot = SwitchDirectory(root);
            if ((File.GetAttributes(Prefix(slot)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update recovery directory is a reparse point: " + slot);
            var plan = ReadJson<SwitchPlan>(Combine(slot, "plan.json"));
            ValidatePlan(root, plan);
            return plan;
        }

        // Advance the cursor before the rename. On restart the two endpoint names distinguish an attempted move from a completed one.
        private static void SetStep(string slot, int from, int to) => MoveEntry(Combine(slot, "step-" + from.ToString(CultureInfo.InvariantCulture)),
            Combine(slot, "step-" + to.ToString(CultureInfo.InvariantCulture)));

        internal static void RecoverSwitch(string root, Action<string> say)
        {
            if (!HasPendingSwitch(root)) return;
            string slot = SwitchDirectory(root);
            var moves = Moves(root, ReadPlan(root), ExistsFile(Combine(slot, "parts")));
            string marker = GetFiles(slot).Select(path => Path.GetFileName(path)!).Single(name => name.StartsWith("step-", StringComparison.Ordinal));
            if (!int.TryParse(marker.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out int step) || step > moves.Count)
                throw new InvalidDataException("Invalid update recovery step: " + marker);
            for (; step > 0; step--)
            {
                var move = moves[step - 1];
                CheckMoveParents(root, slot, move);
                if (ExistsAny(move.To))
                {
                    if (ExistsAny(move.From))
                    {
                        string old = Combine(slot, "old");
                        if (IsWithin(move.To, old)) ParkRecreatedEntry(root, move.From);
                        else if (IsWithin(move.From, old)) ParkRecreatedEntry(root, move.To);
                    }
                    if (!ExistsAny(move.From))
                    {
                        CreateDirectory(Path.GetDirectoryName(move.From)!);
                        MoveEntry(move.To, move.From);
                    }
                }
                SetStep(slot, step, step - 1);
            }
            RequireDelivery(root);
            FinishSwitch(root, say);
        }

        private static void ParkRecreatedEntry(string root, string path)
        {
            // Preserve entries another process created during the gap, including a newly opened data directory.
            string parked = root + ".incomplete-" + Guid.NewGuid().ToString("N");
            string target = path == root ? parked : Combine(parked, RelativeTo(root, path));
            CreateDirectory(Path.GetDirectoryName(target)!);
            MoveEntry(path, target);
        }

        // Retiring the recovery name commits the switch. Cleanup can be retried without undoing an accepted install.
        internal static void FinishSwitch(string root, Action<string> say)
        {
            string slot = SwitchDirectory(root), discarded = slot + ".done-" + Guid.NewGuid().ToString("N");
            MoveEntry(slot, discarded);
            try { RemoveTree(discarded); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                say(UpdaterText.Bilingual("CleanupPending", "cleanup pending") + ": " + discarded + " (" + error.Message + ")");
            }
        }
    }
}
