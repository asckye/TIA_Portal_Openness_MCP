using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Mock
{
    /// <summary>
    /// An in-memory Version Control Interface that behaves like the real one where it matters:
    /// workspaces point at real folders, mapping writes real text files, and status really
    /// compares project content against what is on disk. That is enough to build and exercise
    /// the whole Git round-trip — map, status, sync, commit — without TIA Portal.
    ///
    /// It deliberately reproduces two rules the real API enforces, because code written against
    /// a more permissive fake breaks on contact with V21:
    /// objects whose state is <see cref="VcCompareState.Equal"/> cannot be synchronized, and an
    /// object must be mapped before it has any state at all.
    /// </summary>
    internal sealed class MockVersionControl : IVersionControl
    {
        private const string FileExtension = ".s7dcl";

        private readonly MockTiaSession _session;
        private readonly string _statePath;
        private MockVcState _state = new MockVcState();

        private List<MockWorkspace> _workspaces { get { return _state.Workspaces; } }
        private Dictionary<string, RestoredContent> _restored { get { return _state.Restored; } }


        public MockVersionControl(MockTiaSession session, string projectPath)
        {
            _session = session;
            _statePath = StatePathFor(projectPath);
            Load();
        }

        public IReadOnlyList<WorkspaceInfo> ListWorkspaces()
        {
            return _workspaces.Select(Describe).ToList();
        }

        public WorkspaceInfo CreateWorkspace(string name, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A workspace name is required.");
            }
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("A folder path is required.");
            }

            var directory = new DirectoryInfo(folderPath.Trim());
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    "folderPath does not exist: " + directory.FullName +
                    ". Create the folder, or clone the Git repository, first.");
            }

            if (_workspaces.Any(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A workspace named '" + name + "' already exists.");
            }

            var workspace = new MockWorkspace { Name = name.Trim(), RootPath = directory.FullName };
            _workspaces.Add(workspace);
            Save();
            return Describe(workspace);
        }

        public MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress)
        {
            var workspace = Require(workspaceName);
            var candidates = Candidates(deviceFilter).ToList();

            var result = new MappingResult
            {
                WorkspaceName = workspace.Name,
                RootPath = workspace.RootPath,
                DryRun = dryRun,
                Visited = candidates.Count,
            };

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (progress != null) progress("vc-map", i + 1, candidates.Count, candidate.Label);

                if (workspace.Mappings.ContainsKey(candidate.Key))
                {
                    result.AlreadyMapped++;
                    result.Items.Add(new MappingItem { Target = candidate.Label, Outcome = "already mapped" });
                    continue;
                }

                if (!candidate.Mappable)
                {
                    result.Unsupported++;
                    result.Items.Add(new MappingItem
                    {
                        Target = candidate.Label,
                        Outcome = "unsupported",
                        Error = "VCI offers no file format for this object type.",
                    });
                    continue;
                }

                if (dryRun)
                {
                    result.Mapped++;
                    result.Items.Add(new MappingItem
                    {
                        Target = candidate.Label,
                        Outcome = "would map",
                        FileFormat = "s7dcl",
                        Directory = candidate.RelativeDirectory,
                    });
                    continue;
                }

                try
                {
                    var file = WriteFile(workspace, candidate);
                    workspace.Mappings[candidate.Key] = new MockMapping
                    {
                        Key = candidate.Key,
                        Name = candidate.Name,
                        FilePath = file,
                        FileFormat = "s7dcl",
                    };

                    result.Mapped++;
                    result.Items.Add(new MappingItem
                    {
                        Target = candidate.Label,
                        Outcome = "mapped",
                        FileFormat = "s7dcl",
                        Directory = candidate.RelativeDirectory,
                    });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Items.Add(new MappingItem
                    {
                        Target = candidate.Label,
                        Outcome = "failed",
                        Error = ex.Message,
                    });
                }
            }

            if (!dryRun) Save();
            return result;
        }

        public WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly)
        {
            var workspace = Require(workspaceName);
            var live = Candidates(null).ToDictionary(c => c.Key, c => c, StringComparer.OrdinalIgnoreCase);

            var report = new WorkspaceStatusReport
            {
                WorkspaceName = workspace.Name,
                RootPath = workspace.RootPath,
            };

            foreach (var mapping in workspace.Mappings.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                report.Total++;
                var state = StateOf(workspace, mapping, live);
                if (state != VcCompareState.Equal) report.Differing++;
                if (changedOnly && state == VcCompareState.Equal) continue;

                report.Items.Add(new MappedObjectInfo
                {
                    Name = mapping.Name,
                    FilePath = mapping.FilePath,
                    FileFormat = mapping.FileFormat,
                    CompareState = state,
                });
            }

            return report;
        }

        public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress)
        {
            var workspace = Require(workspaceName);
            var live = Candidates(null).ToDictionary(c => c.Key, c => c, StringComparer.OrdinalIgnoreCase);

            var result = new SyncResult
            {
                WorkspaceName = workspace.Name,
                RootPath = workspace.RootPath,
                Direction = direction,
                DryRun = dryRun,
            };

            // TIA refuses to synchronize a mapping whose state is Equal, so "sync everything"
            // is not an option that exists; equal objects are always skipped.
            var targets = new List<MockMapping>();
            foreach (var mapping in workspace.Mappings.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (StateOf(workspace, mapping, live) == VcCompareState.Equal) { result.SkippedEqual++; continue; }
                targets.Add(mapping);
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var mapping = targets[i];
                if (progress != null) progress("vc-sync", i + 1, targets.Count, mapping.Name);

                if (dryRun)
                {
                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = mapping.Name, Outcome = "would sync " + direction });
                    continue;
                }

                try
                {
                    if (direction == SyncDirection.ProjectToWorkspace)
                    {
                        MockCandidate candidate;
                        if (!live.TryGetValue(mapping.Key, out candidate))
                        {
                            throw new InvalidOperationException(
                                "The object no longer exists in the project. Remove the mapping.");
                        }
                        File.WriteAllText(mapping.FilePath, candidate.Content, new UTF8Encoding(false));
                    }
                    else
                    {
                        if (!File.Exists(mapping.FilePath))
                        {
                            throw new FileNotFoundException("Workspace file is missing.", mapping.FilePath);
                        }
                        // Reading a version back marks the block as changed and not yet compiled,
                        // which is what the real API leaves behind too.
                        _restored[mapping.Key] = new RestoredContent
                        {
                            Content = File.ReadAllText(mapping.FilePath),
                            RecordedAt = DateTimeOffset.UtcNow,
                        };
                        _session.NoteRestoredFromWorkspace(mapping.Key);
                    }

                    result.Synchronized++;
                    result.Items.Add(new SyncItem { Name = mapping.Name, Outcome = "synchronized" });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Items.Add(new SyncItem { Name = mapping.Name, Outcome = "failed", Error = ex.Message });
                }
            }

            if (!dryRun) Save();
            return result;
        }

        /// <summary>
        /// The text a WorkspaceToProject restore put into the project, when it still stands.
        ///
        /// After a restore the project genuinely holds the file's text, so comparing it against
        /// the freshly rendered block would report the object as differing from the very file it
        /// was just restored from. The override lapses as soon as the block is modified through
        /// any other path, which its <see cref="BlockInfo.ModifiedDate"/> reveals.
        /// </summary>
        private string RestoredOr(string key, BlockInfo block, Func<string> fallback)
        {
            RestoredContent restored;
            if (!_restored.TryGetValue(key, out restored)) return fallback();

            if (block.ModifiedDate.HasValue && block.ModifiedDate.Value > restored.RecordedAt)
            {
                _restored.Remove(key);
                return fallback();
            }
            return restored.Content;
        }

        // ---- persistence ---------------------------------------------------

        /// <summary>
        /// Real VCI configuration lives inside the TIA project, so it survives closing and
        /// reopening. The mock keeps a sidecar file per project path for the same reason: without
        /// it every CLI invocation would start with no workspaces, and the multi-step Git workflow
        /// this feature exists for could not be exercised at all.
        /// </summary>
        private static string StatePathFor(string projectPath)
        {
            var root = Path.Combine(
                System.Environment.GetEnvironmentVariable("TIA_STUDIO_MOCK_STATE_ROOT") ?? Path.GetTempPath(),
                "TiaOpenness", "mock-vci");

            var key = string.IsNullOrWhiteSpace(projectPath) ? "unnamed" : projectPath;
            var name = new string(key.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray());

            // Path length is bounded, so a long project path is folded into a stable hash.
            if (name.Length > 80)
            {
                unchecked
                {
                    var hash = 17;
                    foreach (var c in key) hash = hash * 31 + c;
                    name = name.Substring(0, 60) + "_" + hash.ToString("X8");
                }
            }
            return Path.Combine(root, name + ".json");
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                var json = File.ReadAllText(_statePath);
                var loaded = JsonConvert.DeserializeObject<MockVcState>(json);
                if (loaded != null) _state = loaded;
            }
            catch (Exception) /* swallow(parse-fallback): an unreadable or corrupt mock JSON sidecar starts the mock session with empty state */
            {
                // A corrupt sidecar must not break the session; start from empty.
                _state = new MockVcState();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
                File.WriteAllText(_statePath, JsonConvert.SerializeObject(_state, Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch (Exception) /* swallow(fail-open-guard): failure to persist the mock sidecar must not invalidate the completed in-memory mock operation */
            {
                // Losing mock state is not worth failing an operation over.
            }
        }

        // ---- internals -----------------------------------------------------

        private VcCompareState StateOf(MockWorkspace workspace, MockMapping mapping,
            IDictionary<string, MockCandidate> live)
        {
            if (!File.Exists(mapping.FilePath)) return VcCompareState.WorkspaceFileMissing;

            MockCandidate candidate;
            if (!live.TryGetValue(mapping.Key, out candidate)) return VcCompareState.Unknown;

            string onDisk;
            try { onDisk = File.ReadAllText(mapping.FilePath); }
            catch (Exception) /* swallow(parse-fallback): unreadable workspace text yields Unknown rather than an invented mock comparison result */ { return VcCompareState.Unknown; }

            return string.Equals(onDisk, candidate.Content, StringComparison.Ordinal)
                ? VcCompareState.Equal
                : VcCompareState.Unequal;
        }

        private string WriteFile(MockWorkspace workspace, MockCandidate candidate)
        {
            var directory = string.IsNullOrEmpty(candidate.RelativeDirectory)
                ? workspace.RootPath
                : Path.Combine(workspace.RootPath, candidate.RelativeDirectory);

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, Sanitize(candidate.FileName) + FileExtension);
            File.WriteAllText(file, candidate.Content, new UTF8Encoding(false));
            return file;
        }

        private IEnumerable<MockCandidate> Candidates(string deviceFilter)
        {
            foreach (var device in _session.EnumerateDevices())
            {
                if (!string.IsNullOrWhiteSpace(deviceFilter) &&
                    !string.Equals(device.Info.Id, deviceFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var block in device.Blocks)
                {
                    var key = device.Info.Id + "|" + block.Path;

                    yield return new MockCandidate
                    {
                        Key = key,
                        Name = device.Info.Id + "/" + block.Path,
                        Label = device.Info.Id + "/" + block.Path,
                        FileName = block.Path.Replace('/', '_'),
                        RelativeDirectory = device.Info.Id,
                        // Know-how protected blocks have no exportable text, so VCI cannot map them.
                        Mappable = !block.IsKnowHowProtected,
                        Content = RestoredOr(key, block, () => RenderBlock(device.Info.Id, block)),
                    };
                }

                foreach (var table in device.TagTables)
                {
                    yield return new MockCandidate
                    {
                        Key = device.Info.Id + "|tags|" + table.Key,
                        Name = device.Info.Id + "/Tags/" + table.Key,
                        Label = device.Info.Id + "/Tags/" + table.Key,
                        FileName = "Tags_" + table.Key.Replace('/', '_'),
                        RelativeDirectory = device.Info.Id,
                        Mappable = true,
                        Content = RenderTagTable(table.Key, table.Value),
                    };
                }
            }
        }

        /// <summary>
        /// Deterministic text for a block. It includes the modification timestamp, so any edit the
        /// mock makes to a block shows up as <see cref="VcCompareState.Unequal"/> without extra
        /// bookkeeping - the same way real content changes do.
        /// </summary>
        private static string RenderBlock(string deviceId, BlockInfo block)
        {
            var text = new StringBuilder();
            text.AppendLine("// VCI text export (mock)");
            text.AppendLine("DEVICE " + deviceId);
            text.AppendLine("OBJECT " + block.Path);
            text.AppendLine("KIND " + block.Kind);
            text.AppendLine("NUMBER " + (block.Number.HasValue ? block.Number.Value.ToString() : "-"));
            text.AppendLine("LANGUAGE " + (block.ProgrammingLanguage ?? "-"));
            text.AppendLine("AUTHOR " + (block.HeaderAuthor ?? "-"));
            text.AppendLine("VERSION " + (block.HeaderVersion ?? "-"));
            text.AppendLine("MODIFIED " + (block.ModifiedDate.HasValue
                ? block.ModifiedDate.Value.UtcDateTime.ToString("O")
                : "-"));
            return text.ToString();
        }

        private static string RenderTagTable(string name, IEnumerable<TagInfo> tags)
        {
            var text = new StringBuilder();
            text.AppendLine("// VCI text export (mock)");
            text.AppendLine("TAG_TABLE " + name);
            foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                text.AppendLine(string.Join("\t", tag.Name, tag.DataType, tag.LogicalAddress, tag.Comment ?? string.Empty));
            }
            return text.ToString();
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var buffer = name.ToCharArray();
            for (var i = 0; i < buffer.Length; i++)
            {
                if (Array.IndexOf(invalid, buffer[i]) >= 0) buffer[i] = '_';
            }
            return new string(buffer);
        }

        private MockWorkspace Require(string workspaceName)
        {
            if (_workspaces.Count == 0)
            {
                throw new InvalidOperationException(
                    "This project has no version control workspace yet. Create one first, then map " +
                    "the project's objects into it.");
            }
            if (string.IsNullOrWhiteSpace(workspaceName)) return _workspaces[0];

            var match = _workspaces.FirstOrDefault(w =>
                string.Equals(w.Name, workspaceName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                throw new KeyNotFoundException("No workspace named '" + workspaceName + "'. Available: " +
                    string.Join(", ", _workspaces.Select(w => w.Name)));
            }
            return match;
        }

        private static WorkspaceInfo Describe(MockWorkspace workspace)
        {
            return new WorkspaceInfo
            {
                Name = workspace.Name,
                RootPath = workspace.RootPath,
                Language = "en-US",
                MappedObjectCount = workspace.Mappings.Count,
            };
        }

        private sealed class MockWorkspace
        {
            public string Name;
            public string RootPath;
            public Dictionary<string, MockMapping> Mappings =
                new Dictionary<string, MockMapping>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MockMapping
        {
            public string Key;
            public string Name;
            public string FilePath;
            public string FileFormat;
        }

        /// <summary>Everything the mock persists per project: the workspaces and any restore overrides.</summary>
        private sealed class MockVcState
        {
            public List<MockWorkspace> Workspaces = new List<MockWorkspace>();
            public Dictionary<string, RestoredContent> Restored =
                new Dictionary<string, RestoredContent>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Text a restore wrote into the project, and when.</summary>
        private sealed class RestoredContent
        {
            public string Content;
            public DateTimeOffset RecordedAt;
        }

        private sealed class MockCandidate
        {
            public string Key;
            public string Name;
            public string Label;
            public string FileName;
            public string RelativeDirectory;
            public bool Mappable;
            public string Content;
        }
    }
}
