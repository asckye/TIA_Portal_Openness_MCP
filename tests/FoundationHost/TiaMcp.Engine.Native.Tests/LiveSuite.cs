using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;

namespace NativeTests
{
    internal static class LiveSuite
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Run(Options options)
        {
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.MTA)
                throw new InvalidOperationException("Native tests require one MTA thread.");
            Safety.ValidateOutput(options.Output);
            Directory.CreateDirectory(options.Output);
            string scratch = Path.Combine(options.Output, "Scratch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            string marker = Path.Combine(scratch, ".test-owner");
            string owner = Guid.NewGuid().ToString("N");
            File.WriteAllText(marker, owner);
            using (var journal = new Journal(Path.Combine(options.Output, "native-events.jsonl")))
            {
                bool passed = false;
                Exception? failure = null;
                try
                {
                    for (int i = 0; i < options.Iterations; i++) Cycle(scratch, i, journal);
                    passed = true;
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    // Failed native calls or teardown retain evidence; never recurse into an arbitrary/project path.
                    if (passed)
                    {
                        try
                        {
                            journal.Call("scratch.cleanup", () =>
                            {
                                if (!Safety.IsWithin(scratch, options.Output) || File.ReadAllText(marker) != owner)
                                    throw new IOException("Scratch ownership mismatch");
                                Safety.RejectReparseAncestors(scratch);
                                ValidateTree(scratch);
                                Directory.Delete(scratch, true);
                            });
                        }
                        catch (Exception ex) { passed = false; failure = ex; }
                    }
                    var report = new { schemaVersion = 1, status = passed ? "PASSED" : "FAILED", tiaMajor = Program.Major,
                        iterations = options.Iterations, completedCycles = journal.CompletedCycles, assertions = journal.Assertions,
                        nativeExecuted = true, scratchRetained = Directory.Exists(scratch), scratch,
                        failure = failure == null ? null : failure.GetType().FullName + ": " + failure.Message,
                        utc = DateTime.UtcNow.ToString("o") };
                    File.WriteAllText(Path.Combine(options.Output, "native-result.json"), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
                }
                return passed ? 0 : 1;
            }
        }

        private static void ValidateTree(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing scratch cleanup through a reparse point");
                if ((attributes & FileAttributes.Directory) != 0) ValidateTree(path);
            }
        }

        private static void Cycle(string scratch, int index, Journal log)
        {
            log.Cycle = index;
            TiaPortal? portal = null;
            Project? project = null;
            Exception? failure = null;
            string name = "NativeSmoke_" + index;
            try
            {
                portal = log.Call("portal.create", () => new TiaPortal(TiaPortalMode.WithoutUserInterface));
                int pid = log.Call("portal.pid", () => portal.GetCurrentProcess().Id);
                log.Record("OWNED_PORTAL", "portal", new { pid, cycle = index });
                project = log.Call("project.create", () => portal.Projects.Create(new DirectoryInfo(scratch), name));
                log.Call("project.name", () => log.Assert(project.Name == name, "Created project name"));
                log.Call("transaction.commit", () =>
                {
                    using (var access = portal.ExclusiveAccess("Native smoke commit"))
                    using (var tx = access.Transaction(project, "Create committed group"))
                    {
                        project.DeviceGroups.Create("Committed");
                        tx.CommitOnDispose();
                    }
                });
                log.Call("transaction.rollback", () =>
                {
                    using (var access = portal.ExclusiveAccess("Native smoke rollback"))
                    using (var tx = access.Transaction(project, "Rollback group"))
                        project.DeviceGroups.Create("RolledBack");
                });
                log.Call("transaction.readback", () =>
                {
                    log.Assert(project.DeviceGroups.Find("Committed") != null, "Committed group exists");
                    log.Assert(project.DeviceGroups.Find("RolledBack") == null, "Uncommitted group rolled back");
                });
                log.Call("project.save", () => project.Save());
                log.Call("project.close", () => project.Close());
                project = null;
                // Derive the reopen target only from this run's unique owned scratch directory.
                var projectFile = Directory.GetFiles(scratch, name + ".ap" + Program.Major, SearchOption.AllDirectories).Single();
                log.Assert(Safety.IsWithin(projectFile, scratch), "Reopen path belongs to this test");
                project = log.Call("project.reopen", () => portal.Projects.Open(new FileInfo(projectFile)));
                log.Call("project.persistence", () =>
                {
                    log.Assert(project.Name == name, "Reopened project name");
                    log.Assert(project.DeviceGroups.Find("Committed") != null, "Saved group persisted");
                    log.Assert(project.DeviceGroups.Find("RolledBack") == null, "Rolled-back group absent after reopen");
                });
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // These references can only originate from new/create/open above, never Attach or a user project.
                try { if (project != null) log.Call("teardown.project.close", () => project.Close()); }
                catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
                try { if (portal != null) log.Call("teardown.portal.dispose", () => portal.Dispose()); }
                catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            }
            if (failure != null) throw failure;
            log.CompletedCycles++;
        }
    }

    internal sealed class Journal : IDisposable
    {
        private readonly FileStream stream;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private int sequence;
        internal int Assertions;
        internal int CompletedCycles;
        internal int Cycle;
        internal Journal(string path) { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        internal void Record(string phase, string stage, object? detail = null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(json.Serialize(new { utc = DateTime.UtcNow.ToString("o"), cycle = Cycle, phase, stage, detail }) + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        internal T Call<T>(string stage, Func<T> call)
        {
            int id = ++sequence;
            Record("BEFORE", stage, new { id });
            try { T value = call(); Record("RETURNED", stage, new { id }); return value; }
            catch (Exception ex) { Record("THREW", stage, new { id, error = ex.GetType().FullName, message = ex.Message }); throw; }
        }
        internal void Call(string stage, Action call) => Call(stage, () => { call(); return true; });
        internal void Assert(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("Assertion failed: " + description);
            Assertions++;
        }
        public void Dispose() { stream.Dispose(); }
    }
}
