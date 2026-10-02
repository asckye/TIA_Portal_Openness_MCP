using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;

namespace LibraryRenameProbe
{
    internal sealed class Marker
    {
        public string Case { get; set; } = "";
        public string Source { get; set; } = "";
        public string Project { get; set; } = "";
        public string TypeGuid { get; set; } = "";
        public string? MinimumTargetDeviceVersion { get; set; }
        public string[] VersionIdentities { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> ScriptHashes { get; set; } = new Dictionary<string, string>();
    }

    internal static class Native
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Run(Options o)
        {
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.MTA)
                throw new InvalidOperationException("One MTA thread required");
            // Revalidate immediately before any native work; there is no Attach, foreign project open, or arbitrary invocation.
            Options.Parse(new[] { "--stage", o.Stage, "--case", o.Case, "--output", o.Output, "--source", o.Source });
            Marker? baseline = null;
            if (o.Stage == "prepare")
            {
                Directory.CreateDirectory(o.Output);
                Directory.CreateDirectory(o.Workspace);
                using (var owner = new StreamWriter(new FileStream(Path.Combine(o.Workspace, ".probe-owner"), FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
                    owner.Write(o.Output);
                Write(Path.Combine(o.Output, "workspace-location.json"), new { workspace = o.Workspace, project = o.ProjectPath,
                    importControlProject = o.ImportControlPath, cleanup = "Retained for diagnosis; no automatic deletion" });
            }
            else
            {
                baseline = Json.Deserialize<Marker>(File.ReadAllText(o.Marker));
                if (baseline.Case != o.Case || baseline.Source != o.Source || baseline.Project != o.ProjectPath || baseline.TypeGuid != Options.SourceGuid)
                    throw new InvalidOperationException("Diagnostic owner marker mismatch");
                using (File.Open(Path.Combine(o.Output, "run-started"), FileMode.CreateNew, FileAccess.Write)) { }
            }
            using (var log = new Journal(Path.Combine(o.Output, o.Stage + "-events.jsonl")))
            {
                TiaPortal? portal = null; Project? project = null;
                Exception? failure = null; string outcome = "NOT_COMPLETED"; int pid = 0; string? start = null;
                bool faulted = false, cleaned = false;
                try
                {
                    portal = log.Call("TiaPortal.new.WithoutUserInterface", () => new TiaPortal(TiaPortalMode.WithoutUserInterface));
                    pid = log.Call("TiaPortal.GetCurrentProcess", () => portal.GetCurrentProcess().Id);
                    start = Process.GetProcessById(pid).StartTime.ToUniversalTime().ToString("o");
                    Write(Path.Combine(o.Output, o.Stage + "-owned-process.json"), new { pid, startUtc = start, owned = true });
                    log.Event("OWNED_PROCESS", new { pid, startUtc = start });
                    if (o.Stage == "prepare")
                    {
                        project = log.Call("Projects.Create", () => portal.Projects.Create(Options.NativeDirectory(o.Workspace), "Probe"));
                        var library = log.Call("GlobalLibraries.Open.ReadOnly", () => portal.GlobalLibraries.Open(new FileInfo(o.Source), OpenMode.ReadOnly));
                        var source = Find(library.TypeFolder, log);
                        ValidateSource(source, log);
                        log.Call("LibraryType.UpdateLibrary", () => source.UpdateLibrary(project.ProjectLibrary,
                            DeleteUnusedVersionsMode.DoNotDelete, StructureConflictResolutionMode.RetainStructure,
                            ForceUpdateMode.SetOnlyHigherUpdatedVersionAsDefault));
                        log.Call("GlobalLibrary.Close", () => library.Close());
                        var type = Find(project.ProjectLibrary.TypeFolder, log);
                        ValidateSource(type, log);
                        baseline = new Marker { Case = o.Case, Source = o.Source, Project = o.ProjectPath, TypeGuid = Options.SourceGuid,
                            VersionIdentities = Identities(type, log), MinimumTargetDeviceVersion = log.Call("Source.MinimumTargetDeviceVersion", () => type.MinimumTargetDeviceVersion?.ToString()) };
                        Require(baseline.VersionIdentities.SequenceEqual(new[] {
                            "5.0.0/1dcd9356-4571-481b-8e81-cb011bd6e1ff/Committed",
                            "5.1.1/e74fcb6f-b658-4ac6-9ea7-e5647251dc72/Committed" }), "Diagnostic input versions differ from recorded sample");
                        ExportScripts(type, Path.Combine(o.Workspace, "before"), log, baseline.ScriptHashes);
                        log.Call("Project.Save.baseline", () => project.Save());
                        outcome = "PREPARED";
                    }
                    else
                    {
                        project = log.Call("Projects.Open.owned", () => portal.Projects.Open(new FileInfo(o.ProjectPath)));
                        var type = Find(project.ProjectLibrary.TypeFolder, log);
                        ValidateSource(type, log);
                        LibraryType affected = type;
                        LibraryTypeVersion? newVersion = null;
                        bool document = o.Case.StartsWith("document-", StringComparison.Ordinal);
                        bool rename = o.Case.EndsWith("rename", StringComparison.Ordinal);
                        bool clone = o.Case == "document-create-rename";
                        if (o.Case.StartsWith("edit-", StringComparison.Ordinal))
                            newVersion = log.Call("LibraryTypeVersion.Edit", () => Version(type, "5.1.1", log).Edit());
                        if (o.Case.EndsWith("property", StringComparison.Ordinal))
                            log.Call("DIRECT.LibraryType.Name.set", () => type.Name = Options.TargetName);
                        else if (o.Case.EndsWith("attributes", StringComparison.Ordinal))
                            log.Call("DIRECT.IEngineeringObject.SetAttributes.Name", () => ((IEngineeringObject)type).SetAttributes(
                                new[] { new KeyValuePair<string, object>("Name", Options.TargetName) }));
                        else if (document)
                        {
                            var docs = Options.NativeDirectory(Path.Combine(o.Workspace, "import"), true);
                            log.Event("DOCUMENT_DIRECTORY", new { fullName = docs.FullName, displayPath = docs.ToString(), fileNameWithoutExtension = "Type" });
                            var exported = log.Call("LibraryTypeVersion.ExportAsDocuments.None", () => Version(type, "5.1.1", log)
                                .ExportAsDocuments(docs, "Type", "SimaticSD", LibraryExportOptions.None));
                            Require(log.Call("Export.TransferResultState", () => exported.TransferResultState) == TransferResultState.Success, "Export not successful");
                            // Real V21 export observed 2026-10-01: Type.def.hmi.yml + Type.def.hmi.js.
                            string yaml = Path.Combine(docs.FullName, "Type.def.hmi.yml");
                            Require(File.Exists(yaml) && File.Exists(Path.Combine(docs.FullName, "Type.def.hmi.js")), "Expected native document pair missing");
                            if (rename)
                            {
                                string content = File.ReadAllText(yaml);
                                string needle = "  " + Options.SourceName + ":";
                                Require(content.Split(new[] { needle }, StringSplitOptions.None).Length == 2, "Expected exactly one source module key");
                                File.WriteAllText(yaml, content.Replace(needle, "  " + Options.TargetName + ":"), new UTF8Encoding(false));
                            }
                            if (o.Case.StartsWith("document-create-", StringComparison.Ordinal))
                            {
                                // Control creation uses a fresh empty second project; no existing type is deleted.
                                if (!rename)
                                {
                                    log.Call("Project.Close.control-copy", () => project.Close());
                                    project = log.Call("Projects.Create.import-control", () => portal.Projects.Create(Options.NativeDirectory(o.Workspace), "ImportControl"));
                                }
                                var result = log.Call("LibraryTypeComposition.CreateFromDocuments", () => project.ProjectLibrary.TypeFolder.Types.CreateFromDocuments(docs, "Type.def.hmi", LibraryImportOptions.None));
                                Require(log.Call("TypeCreate.TransferResultState", () => result.TransferResultState) == TransferResultState.Success, "Create transfer not successful");
                                affected = log.Call("TypeCreate.CreatedType", () => result.CreatedType);
                                newVersion = log.Call("CreatedType.Version", () => affected.Versions.Single());
                            }
                            else
                            {
                                var result = log.Call("LibraryTypeVersionComposition.CreateFromDocuments", () => type.Versions.CreateFromDocuments(docs, "Type.def.hmi", CreateOptions.None, LibraryImportOptions.None));
                                Require(log.Call("VersionCreate.TransferResultState", () => result.TransferResultState) == TransferResultState.Success, "Version transfer not successful");
                                newVersion = log.Call("VersionCreate.CreatedVersion", () => result.CreatedVersion);
                            }
                        }
                        string actualName = log.Call("LibraryType.Name.readback", () => affected.Name);
                        string actualGuid = log.Call("LibraryType.Guid.readback", () => affected.Guid.ToString());
                        log.Event("READBACK", new { actualName, actualGuid, caseName = o.Case });
                        bool sameIdentity = actualGuid == Options.SourceGuid;
                        bool expectedRename = o.Case != "control" && !o.Case.EndsWith("control", StringComparison.Ordinal);
                        // A document update can return success without renaming the library type. Report that as insufficient.
                        Require(actualName == (expectedRename ? Options.TargetName : Options.SourceName), "API returned but library type name did not match requested outcome");
                        if (!o.Case.StartsWith("document-create-", StringComparison.Ordinal))
                        {
                            Require(sameIdentity, "Original type GUID changed");
                            Require(baseline!.VersionIdentities.All(v => Identities(affected, log).Contains(v)), "An original version identity changed");
                        }
                        else if (clone) Require(!sameIdentity, "Clone unexpectedly reused original GUID");
                        if (newVersion != null)
                            log.Call("LibraryTypeVersion.Release", () => newVersion.Release(CreateOrReleaseDependenciesMode.DoNotAutomaticallyCreateOrReleaseDependencies,
                                new Version(5, 1, 2), "MCP diagnostic", "Isolated script rename diagnostic"));
                        string savedPath = o.Case == "document-create-control" ? o.ImportControlPath : o.ProjectPath;
                        log.Call("Project.Save.after", () => project.Save());
                        log.Call("Project.Close.after", () => project.Close()); project = null;
                        project = log.Call("Projects.Open.persistence", () => portal.Projects.Open(new FileInfo(savedPath)));
                        affected = o.Case.StartsWith("document-create-", StringComparison.Ordinal)
                            ? log.Call("CreatedType.Find.persistence", () => project.ProjectLibrary.TypeFolder.Types.Find(actualName))
                            : Find(project.ProjectLibrary.TypeFolder, log, actualName);
                        Require(log.Call("Type.Guid.persistence", () => affected.Guid.ToString()) == actualGuid, "GUID not persisted");
                        Require(log.Call("Type.Name.persistence", () => affected.Name) == actualName, "Name not persisted");
                        string? minimumVersion = log.Call("Type.MinimumTargetDeviceVersion.persistence", () => affected.MinimumTargetDeviceVersion?.ToString());
                        log.Event("PERSISTED_TYPE", new { actualName, actualGuid, minimumTargetDeviceVersion = minimumVersion,
                            originalMinimumTargetDeviceVersion = baseline!.MinimumTargetDeviceVersion, identityPreserved = sameIdentity });
                        if (!o.Case.StartsWith("document-create-", StringComparison.Ordinal))
                        {
                            var persisted = Identities(affected, log);
                            Require(baseline!.VersionIdentities.All(persisted.Contains), "Original version identities not persisted");
                            Require(persisted.Length == baseline.VersionIdentities.Length + (newVersion == null ? 0 : 1), "Unexpected version count");
                            if (expectedRename)
                            {
                                var folder = log.Call("Renamed.Type.Parent", () => affected.Parent as LibraryTypeFolder);
                                Require(folder != null && log.Call("OldName.Absence", () => folder.Types.Find(Options.SourceName)) == null, "Old name remains");
                            }
                        }
                        var hashes = new Dictionary<string, string>();
                        ExportScripts(affected, Path.Combine(o.Workspace, "after"), log, hashes);
                        if (!o.Case.StartsWith("document-create-", StringComparison.Ordinal))
                            foreach (var pair in baseline!.ScriptHashes) Require(hashes.TryGetValue(pair.Key, out var hash) && hash == pair.Value, "Original script content changed: " + pair.Key);
                        if (newVersion != null)
                            Require(hashes.TryGetValue("5.1.2/Type.def.hmi.js", out var newHash) && newHash == baseline!.ScriptHashes["5.1.1/Type.def.hmi.js"], "New version script body mismatch");
                        if (clone)
                        {
                            var original = Find(project.ProjectLibrary.TypeFolder, log);
                            Require(Identities(original, log).SequenceEqual(baseline!.VersionIdentities), "Clone changed original versions");
                        }
                        outcome = clone ? "CLONE_RENAMED_PERSISTED" : expectedRename ? "RENAME_PERSISTED" : "CONTROL_PASSED";
                    }
                }
                catch (Exception ex) { failure = ex; faulted = IsFatal(ex); log.Event("CASE_FAILED", new { error = ex.ToString(), nativeChannelFaulted = faulted }); }
                finally
                {
                    // Never send further Openness requests through a nonrecoverable channel.
                    if (!faulted)
                    {
                        try { if (project != null) log.Call("Project.Close.teardown", () => project.Close()); }
                        catch (Exception ex) { failure = failure ?? ex; faulted = IsFatal(ex); }
                        try { if (portal != null && !faulted) { log.Call("TiaPortal.Dispose.owned", () => portal.Dispose()); cleaned = true; } }
                        catch (Exception ex) { failure = failure ?? ex; }
                    }
                    bool? alive = null;
                    if (pid != 0) { try { using (var process = Process.GetProcessById(pid)) alive = process.StartTime.ToUniversalTime().ToString("o") == start && !process.HasExited; } catch (ArgumentException) { alive = false; } catch { } }
                    try
                    {
                        // Copy only diagnostic exports back beside the launcher. Native projects stay
                        // under the short workspace, including after a failed/crashed run.
                        foreach (string name in new[] { "before", "after", "import" })
                            CopyEvidence(Path.Combine(o.Workspace, name), Path.Combine(o.Output, name));
                    }
                    catch (Exception ex) { failure = failure ?? ex; log.Event("EVIDENCE_COPY_FAILED", new { error = ex.ToString() }); }
                    if (o.Stage == "prepare" && failure == null && cleaned && baseline != null) Write(o.Marker, baseline);
                    Write(Path.Combine(o.Output, o.Stage + "-result.json"), new { schemaVersion = 1, stage = o.Stage, caseName = o.Case,
                        status = failure == null ? outcome : "FAILED", nativeExecuted = portal != null, pid, startUtc = start, workspace = o.Workspace,
                        ownedPortalAlive = alive, cleanupReturned = cleaned, nativeChannelFaulted = faulted,
                        error = failure?.ToString(), utc = DateTime.UtcNow.ToString("o") });
                }
                return failure == null ? 0 : 1;
            }
        }

        static LibraryType Find(LibraryTypeFolder root, Journal log, string name = Options.SourceName)
        {
            var folder = root;
            foreach (string part in new[] { "LGen", "Types_HMI", "Scripts" })
                folder = log.Call("Library.Folder.Find." + part, () => folder.Folders.Find(part));
            return log.Call("Library.Types.Find." + name, () => folder.Types.Find(name));
        }
        static void ValidateSource(LibraryType type, Journal log)
        {
            if (type == null || type.GetType().FullName != "Siemens.Engineering.HmiUnified.Library.ScriptModuleType")
                throw new InvalidOperationException("Expected Unified script type");
            Require(log.Call("Source.Guid", () => type.Guid.ToString()) == Options.SourceGuid, "Sample GUID mismatch");
            Require(log.Call("Source.Name", () => type.Name) == Options.SourceName, "Sample name mismatch");
        }
        static LibraryTypeVersion Version(LibraryType type, string version, Journal log) => log.Call("Versions.Find." + version, () => type.Versions.Single(v => v.VersionNumber.ToString() == version));
        static string[] Identities(LibraryType type, Journal log) => log.Call("Versions.Identities", () => type.Versions.Select(v => v.VersionNumber + "/" + v.Guid + "/" + v.State).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        static void ExportScripts(LibraryType type, string output, Journal log, Dictionary<string, string> hashes)
        {
            foreach (var version in log.Call("Versions.Enumerate", () => type.Versions.ToArray()))
            {
                string number = log.Call("Version.Number", () => version.VersionNumber.ToString());
                var dir = Options.NativeDirectory(Path.Combine(output, number), true);
                log.Event("EXPORT_DIRECTORY", new { version = number, fullName = dir.FullName, displayPath = dir.ToString(), fileNameWithoutExtension = "Type" });
                var result = log.Call("Version.ExportAsDocuments." + number, () => version.ExportAsDocuments(dir, "Type", "SimaticSD", LibraryExportOptions.None));
                Require(log.Call("Export.State." + number, () => result.TransferResultState) == TransferResultState.Success, "Export failed");
                var scripts = Directory.GetFiles(dir.FullName, "*.js");
                Require(scripts.Length > 0, "No script body exported");
                foreach (var file in scripts) using (var sha = SHA256.Create()) hashes.Add(number + "/" + Path.GetFileName(file), BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").ToLowerInvariant());
            }
        }
        static bool IsFatal(Exception ex) => ex is NonRecoverableException || (ex.InnerException != null && IsFatal(ex.InnerException));
        static void CopyEvidence(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            Options.Absolute(source); Options.Absolute(destination);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                Options.Absolute(file);
                string target = Options.Absolute(Path.Combine(destination, Path.GetFileName(file)));
                File.Copy(file, target, true);
            }
            foreach (string directory in Directory.GetDirectories(source))
                CopyEvidence(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Write(string path, object value) => File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false));
    }

    internal sealed class Journal : IDisposable
    {
        readonly FileStream stream;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        int sequence;
        internal Journal(string path) { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        internal void Event(string state, object detail)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(new { sequence = ++sequence, utc = DateTime.UtcNow.ToString("o"), state, detail }) + "\n");
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        internal T Call<T>(string api, Func<T> call)
        {
            Event("BEFORE", new { api });
            try { T value = call(); Event("RETURNED", new { api }); return value; }
            catch (Exception ex) { Event("THREW", new { api, error = ex.ToString() }); throw; }
        }
        internal void Call(string api, Action call) => Call(api, () => { call(); return true; });
        public void Dispose() => stream.Dispose();
    }
}
