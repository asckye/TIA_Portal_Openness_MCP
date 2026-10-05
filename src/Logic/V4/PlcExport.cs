using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public interface IPlcExportFiles
    {
        CandidateFile Observe(string path);
        string Stage(string destination, bool documents);
        void Publish(string staging, string destination, bool documents, bool overwrite);
    }
    public sealed class PlcExportFiles : IPlcExportFiles
    {
        public CandidateFile Observe(string path) => CandidateExportFiles.Observe(path);
        public string Stage(string destination, bool documents)
        {
            CandidateExportFiles.SafePath(destination);
            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent); CandidateExportFiles.SafePath(parent);
            string stage = Path.Combine(parent, ".tia-export-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(stage) || Directory.Exists(stage)) throw new IOException("Staging path already exists.");
            Directory.CreateDirectory(stage); CandidateExportFiles.SafePath(stage);
            return documents ? stage : Path.Combine(stage, Path.GetFileName(destination));
        }
        public void Publish(string staging, string destination, bool documents, bool overwrite)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Export publication requires Windows non-replacing rename.");
            CandidateExportFiles.SafePath(staging); CandidateExportFiles.SafePath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); CandidateExportFiles.SafePath(destination);
            if (documents) Directory.Move(staging, destination);
            else if (overwrite && File.Exists(destination)) File.Replace(staging, destination, null);
            else File.Move(staging, destination);
        }
    }

    // Native owners serialize this host session. A plan authorizes one attempt only.
    public sealed class PlcExportSession
    {
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public bool RequiresSessionReset { get; private set; }
        public Envelope Run(IPlcExportAdapter adapter, string release, string tool, string id, PlcExportRequest request,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "", IPlcExportFiles? files = null)
        {
            files ??= new PlcExportFiles();
            var children = new List<BatchItem>(); var objects = Array.Empty<PlcExportObject>();
            JsonObject? data = null; PlcExportAttempt? attempt = null; string? stage = null; string? destination = null;
            int index = 0; bool issued = false, publishing = false, publishAttempted = false, published = false;
            CandidateFile? expectedDestination = null;
            try
            {
                if (RequiresSessionReset) Refuse(new SessionResetRequiredDetails("plc-export-unknown"));
                if (!PlcExportContract.Entries.Contains(tool, StringComparer.Ordinal)) Invalid("tool");
                if (mode != "preview" && mode != "apply") Invalid("mode");
                if (request.OnError != "stop") Unsupported(release, "onError");
                if (request.MaxItems < 1 || request.MaxItems > (PlcExportContract.Batch(tool) ? 256 : 1)) Invalid("maxItems");
                if (string.IsNullOrWhiteSpace(request.SoftwarePath)) Invalid("softwarePath");
                bool docs = PlcExportContract.Documents(tool);
                if (docs && release != "20" && release != "21") Unsupported(release, "documents");
                if (request.PreservePath && docs && release != "20" && release != "21") Unsupported(release, "preservePath");
                if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(IsHash(expectedPlanHash) ? expectedPlanHash : null));
                if (mode == "apply" && !IsHash(expectedPlanHash)) Invalid("expectedPlanHash");
                var identity = CandidateHostMapping.Plan(adapter.ReadIdentity());
                if (mode == "apply")
                {
                    if (DeviceCreationSession.CanonicalProject(expectedProjectFile) != DeviceCreationSession.CanonicalProject(identity.ProjectFile!)) Refuse(new IdentityMismatchDetails("project-binding", null, null));
                    if (!plans.TryGetValue(expectedPlanHash, out var reviewed) || consumed.Contains(expectedPlanHash)) Refuse(new PlanStaleDetails(expectedPlanHash, "missing-or-consumed-plan"));
                    if (Binding(identity) != Binding(reviewed!.Identity)) Refuse(new IdentityMismatchDetails("project-binding", null, null));
                }
                string root = Resolve(request.OutputPath, request.WorkspaceRoot);
                objects = adapter.ReadObjects(tool, request).ToArray();
                if (objects.Length < 1 || objects.Length > request.MaxItems || objects.Any(o => o == null || string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.Path)
                    || string.IsNullOrEmpty(o.Name) || !o.Consistent || o.ContentHash != null && !IsHash(o.ContentHash))
                    || objects.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != objects.Length
                    || !objects.Select(o => o.Path).SequenceEqual(objects.Select(o => o.Path).OrderBy(p => p, StringComparer.Ordinal)))
                    Refuse(new PreconditionFailedDetails("complete-export-inventory", null));
                bool overwrite = adapter.SupportsOverwrite(tool);
                if (request.Overwrite && (!overwrite || docs)) Unsupported(release, "overwrite");
                var destinations = objects.Select(o => Destination(tool, request, root, o)).ToArray();
                if (!overwrite && request.PreservePath && destinations.Any(d => !Directory.Exists(Path.GetDirectoryName(d)!))) Unsupported(release, "preservePath-parent");
                if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length) Invalid("duplicate-output-paths");
                var targets = destinations.Select(files.Observe).ToArray();
                var documentFiles = docs ? destinations.SelectMany(d => new[] { Path.Combine(d, objects[Array.IndexOf(destinations, d)].Name + ".s7dcl"), Path.Combine(d, objects[Array.IndexOf(destinations, d)].Name + ".s7res") }).Select(files.Observe) : Enumerable.Empty<CandidateFile>();
                var observations = new[] { files.Observe(root) }.Concat(targets).Concat(documentFiles).GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                var planFiles = observations.Select(f => new PlanFile(f.Path, f.Exists, f.ByteLength, f.Sha256)).ToArray();
                var planIdentity = new PlanIdentity(identity.ProcessId, identity.ProcessStartUtc, identity.ProjectFile, identity.BindingEpoch, null, planFiles);
                var argumentsHash = DeviceCreationSession.Hash(new { request.SoftwarePath, request.ObjectPath, request.GroupPath, outputPath = root,
                    workspaceRoot = request.WorkspaceRoot == "" ? "" : Resolve(request.WorkspaceRoot, ""), request.RegexName, request.PreservePath, request.Recursive, request.Overwrite, request.OnError, request.MaxItems });
                var inventoryHash = DeviceCreationSession.Hash(new { objects, overwrite });
                var inputHashes = objects.Where(o => o.ContentHash != null).ToDictionary(o => o.Path, o => o.ContentHash!, StringComparer.Ordinal);
                var operations = objects.Select((o, i) => new PlanOperation(tool, destinations[i], V4Json.Data(new { objectIdentity = o, destination = targets[i], documents = docs })!.Value)).ToArray();
                var warnings = Array.Empty<Warning>();
                var hash = DeviceCreationSession.Hash(new { releaseKey = release, tool, argumentsHash, identity = planIdentity, inputHashes, inventoryHash, operations, warnings });
                var plan = new Plan(hash, release, tool, argumentsHash, planIdentity, inputHashes, inventoryHash, operations, warnings);
                data = new JsonObject { ["plan"] = JsonNode.Parse(V4Json.Serialize(plan)), ["objects"] = JsonNode.Parse(V4Json.Serialize(objects)), ["destinations"] = JsonNode.Parse(V4Json.Serialize(planFiles)),
                    ["contentObservation"] = "Only readable content is hashed; unavailable content is explicit in each object.", ["overwriteSupported"] = overwrite };
                if (mode == "preview")
                {
                    if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));
                    plans[hash] = plan; return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.ReadOnly);
                }
                if (hash != expectedPlanHash) Refuse(new PlanStaleDetails(expectedPlanHash, "objects-destination-or-arguments-changed"));
                if (!request.Overwrite && targets.Any(f => f.Exists)) Refuse(new AlreadyExistsDetails(targets.First(f => f.Exists).Path));
                consumed.Add(hash);
                for (index = 0; index < objects.Length; index++)
                {
                    destination = destinations[index]; expectedDestination = targets[index]; issued = publishing = publishAttempted = published = false; attempt = null; stage = null;
                    stage = files.Stage(destination, docs);
                    var check = new PlcExportCheck { Release = release, Tool = tool, Request = request, Identity = CandidateHostMapping.Identity(identity), Objects = objects, Index = index,
                        Destination = targets[index], StagingPath = stage, Documents = docs, OverwriteSupported = overwrite };
                    check.Digest = CandidateExportFiles.Digest(check);
                    attempt = adapter is IExportCandidateBoundary boundary ? boundary.Execute(check) : CandidateExecution.Export(adapter, check);
                    issued = attempt.Issued;
                    if (attempt.Fault != null) throw new CandidateObservationException(attempt.Fault);
                    VerifyStaged(check, attempt);
                    // The destination may have appeared while the native exporter ran.
                    if (CandidateDigest.Files(new[] { targets[index] }) != CandidateDigest.Files(new[] { files.Observe(destination) }))
                    { publishing = true; throw new IOException("Destination changed before publish."); }
                    publishing = publishAttempted = true; files.Publish(stage, destination, docs, request.Overwrite); published = true;
                    var readback = docs ? attempt.Staged.Select(f => files.Observe(Path.Combine(destination, Path.GetFileName(f.Path)))).ToArray() : new[] { files.Observe(destination) };
                    if (readback.Length != attempt.Staged.Length || readback.Where((f, i) => !f.Exists || f.ByteLength != attempt.Staged[i].ByteLength || f.Sha256 != attempt.Staged[i].Sha256).Any())
                        throw new InvalidDataException("Published content/path readback differs from staging.");
                    children.Add(new BatchItem(index, destination, Result(release, tool, id,
                        new JsonObject { ["files"] = JsonNode.Parse(V4Json.Serialize(readback)), ["stagingPath"] = stage, ["nativeExportCalls"] = 1, ["contentVerified"] = true, ["pathVerified"] = true }, null, Outcome.Succeeded, Execution.Completed)));
                    issued = false;
                }
                data["items"] = JsonNode.Parse(V4Json.Serialize(new BatchData(children)))!["items"]!.DeepClone();
                return Result(release, tool, id, data, null, Outcome.Succeeded, Execution.Completed);
            }
            catch (Exception ex)
            {
                bool unknown = issued && (!publishing || published);
                if (publishAttempted && !published && expectedDestination != null)
                {
                    try { unknown |= CandidateDigest.Files(new[] { expectedDestination }) != CandidateDigest.Files(new[] { files.Observe(expectedDestination.Path) }); }
                    catch (Exception) /* swallow(privacy): a failed publish with unavailable destination state is uncertain */ { unknown = true; }
                }
                RequiresSessionReset |= unknown;
                ErrorDetails details = ex is PlcExportRejection refused ? refused.Error.Details : ex is DeviceCreationRejection bindingRejected ? bindingRejected.Error.Details : ex is CandidateObservationException observed ? Fault(observed.Fault, expectedPlanHash, release)
                    : ex is IOException || ex is UnauthorizedAccessException ? (ErrorDetails)new IoFailedDetails(publishing ? "publish" : "export-preflight", destination) : new PreconditionFailedDetails("export-preflight", null);
                var residue = new JsonObject { ["stagingPath"] = stage, ["destinationPath"] = destination, ["status"] = "unavailable", ["nativeExportIssued"] = issued, ["published"] = published };
                try
                {
                    residue["staging"] = stage == null ? null : JsonNode.Parse(V4Json.Serialize(files.Observe(PlcExportContract.Documents(tool) ? stage : Path.GetDirectoryName(stage)!)));
                    residue["destination"] = destination == null ? null : JsonNode.Parse(V4Json.Serialize(files.Observe(destination)));
                    residue["status"] = "checked";
                }
                catch (Exception) /* swallow(privacy): preserve unavailable destination/staging residue after failed observation */ { }
                data ??= new JsonObject(); data["residue"] = residue;
                var error = unknown ? new Error("Export outcome is unknown; inspect staging and destination and rebuild the session. Never replay.", new OutcomeUnknownDetails("native-export-or-readback", new Dictionary<string, JsonElement> { ["residue"] = V4Json.Data(residue)!.Value }))
                    : new Error("Export stopped; retained staging and original destination evidence are available.", details);
                if (mode == "apply" && objects.Length > 0)
                {
                    children.Add(new BatchItem(index, destination, Result(release, tool, id, new JsonObject { ["residue"] = residue.DeepClone(), ["nativeExportCalls"] = issued ? 1 : 0 }, error,
                        unknown ? Outcome.Unknown : publishing ? Outcome.Failed : Outcome.RejectedBeforeOperation, unknown ? Execution.Unknown : publishing ? Execution.Completed : Execution.NotStarted)));
                    for (int i = index + 1; i < objects.Length; i++) children.Add(new BatchItem(i, objects[i].Path, Result(release, tool, id, null,
                        new Error("A previous export stopped this batch.", new NotExecutedDetails(index)), Outcome.RejectedBeforeOperation, Execution.NotStarted)));
                    data["items"] = JsonNode.Parse(V4Json.Serialize(new BatchData(children)))!["items"]!.DeepClone();
                    if (!unknown && children.Any(c => c.Result.Ok)) error = new Error("Verified exports succeeded before the batch stopped.", new PartialFailureDetails(children.Count(c => c.Result.Ok), 1, Math.Max(0, objects.Length - index - 1)));
                }
                return Result(release, tool, id, data, error, unknown ? Outcome.Unknown : children.Any(c => c.Result.Ok) ? Outcome.Partial : publishing ? Outcome.Failed : Outcome.RejectedBeforeOperation,
                    unknown ? Execution.Unknown : children.Any(c => c.Result.Ok) ? Execution.Partial : publishing ? Execution.Completed : Execution.NotStarted);
            }
        }
        public static void VerifyStaged(PlcExportCheck check, PlcExportAttempt attempt)
        {
            if (!attempt.Issued || attempt.RequiresSessionReset || attempt.Staged.Length < 1 || attempt.Staged.Length > (check.Documents ? 2 : 1)
                || attempt.Staged.Any(f => !f.Exists || f.ByteLength < 1 || f.Sha256 == null || !IsHash(f.Sha256)
                    || !string.Equals(f.Path, check.Documents ? Path.Combine(check.StagingPath, Path.GetFileName(f.Path)) : check.StagingPath, StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid staged export reply.");
            if (CandidateDigest.Files(attempt.Staged) != CandidateDigest.Files(CandidateExportFiles.Staged(check)))
                throw new InvalidDataException("Staged files differ from the native observation.");
        }
        public static string Resolve(string output, string workspace)
        {
            if (string.IsNullOrWhiteSpace(output)) Invalid("exportPath");
            if (Path.IsPathRooted(output) && (output.Length < 3 || output[1] != ":"[0] || output[2] != Path.DirectorySeparatorChar && output[2] != Path.AltDirectorySeparatorChar)) Invalid("exportPath");
            if (!Path.IsPathRooted(output))
            {
                if (!Path.IsPathRooted(workspace)) Invalid("workspaceRoot");
                string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                output = Path.GetFullPath(Path.Combine(root, output));
                if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Invalid("exportPath");
            }
            output = Path.GetFullPath(output); CandidateExportFiles.SafePath(output); return output;
        }
        private static string Destination(string tool, PlcExportRequest request, string root, PlcExportObject item)
        {
            if (item.Name == "." || item.Name == ".." || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) Invalid("objectName");
            if (tool == "ExportPlcTagTable" || tool == "ExportPlcBlockDocuments") return root;
            string relative = request.PreservePath ? item.Path.Replace('/', Path.DirectorySeparatorChar) : item.Name;
            string path = PlcExportContract.Documents(tool) ? Path.Combine(root, request.PreservePath ? relative : "block-" + DeviceCreationSession.Hash(item.Path)) : Path.Combine(root, relative + ".xml");
            path = Path.GetFullPath(path);
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Invalid("objectPath");
            return path;
        }
        private static string Binding(PlanIdentity i) => DeviceCreationSession.Hash(new { i.ProcessId, i.ProcessStartUtc, projectFile = DeviceCreationSession.CanonicalProject(i.ProjectFile!), i.BindingEpoch });
        private static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => "0123456789abcdef".Contains(c));
        private static ErrorDetails Fault(CandidateFault f, string hash, string release) => f.Kind == "identity" ? (ErrorDetails)new IdentityMismatchDetails("project-binding", null, null)
            : f.Kind == "stale" ? new PlanStaleDetails(IsHash(hash) ? hash : null, f.Subject) : f.Kind == "unsupported" ? new UnsupportedCapabilityDetails(release, "P6-EXPORT", f.Subject)
            : f.Kind == "invalid" ? new InvalidArgumentDetails(f.Subject, Array.Empty<string>()) : f.Kind == "io" ? new IoFailedDetails("export-preflight", null) : new PreconditionFailedDetails("export-preflight", null);
        public static Envelope Result(string release, string tool, string id, JsonObject? data, Error? error, Outcome outcome, Execution execution)
            => PlcImportSession.Result(release, tool, id, data, error, outcome, execution);
        public static void Unsupported(string release, string action) => Refuse(new UnsupportedCapabilityDetails(release, "P6-EXPORT", action));
        private static void Invalid(string parameter) => Refuse(new InvalidArgumentDetails(parameter, Array.Empty<string>()));
        public static void IdentityMismatch() => Refuse(new IdentityMismatchDetails("project-binding", null, null));
        public static void Refuse(string message, ErrorDetails details) => throw new PlcExportRejection(new Error(message, details));
        private static void Refuse(ErrorDetails details) => throw new PlcExportRejection(new Error("Export request does not satisfy the reviewed capability or precondition.", details));
    }
    public sealed class PlcExportRejection : Exception
    {
        public Error Error { get; }
        public PlcExportRejection(Error error) : base(error.Message) { Error = error; }
    }
}
