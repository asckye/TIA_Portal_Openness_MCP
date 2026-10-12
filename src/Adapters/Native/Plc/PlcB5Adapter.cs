using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
        private readonly IPlcOrganisationSession _session;
        private readonly PlcFoundationEngine engine;
        private readonly PlcB5Request request;
        internal bool MayHaveChanged { get; private set; }
        private int Version => _session.ReleaseKey == "14sp1" ? 14 : _session.ReleaseKey == "15.1" ? 15 : int.Parse(_session.ReleaseKey, System.Globalization.CultureInfo.InvariantCulture);
        internal PlcB5Adapter(IPlcOrganisationSession session, PlcFoundationEngine engine, PlcB5Request request)
        { _session = session; this.engine = engine; this.request = request; }
        private PlcB5Reply RunTool(string tool, Func<Dictionary<string, object?>, string> action)
        {
            var reply = _session.RunHmiStepTool(tool, meta => {
                bool classic = tool is "ManagePlcSupervision" or "ExportPlcProDiagInfo";
                if (classic) { meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; }
                try { return action(meta); }
                catch (Exception error)
                {
                    if (classic)
                    {
                        var cause = error.GetBaseException();
                        meta["v4FailureCode"] = cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                            : cause is ArgumentException or TiaMcp.Adapters.Contracts.AdapterPreconditionException ? "INVALID_ARGUMENT"
                            : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                            : cause is PlcSoftwareException portal ? portal.Code == "InvalidState" ? "PRECONDITION_FAILED" : portal.Code
                            : cause is InvalidOperationException ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED";
                    }
                    throw;
                }
                finally { MayHaveChanged |= meta.TryGetValue("mayHaveChanged", out var changed) && changed is true; }
            });
            return new PlcB5Reply { Message = reply.Message, Meta = reply.Meta, MayHaveChanged = reply.MayHaveChanged, RequiresSessionReset = reply.RequiresSessionReset };
        }
        private void ValidateRequest() { if (request.ValidationError != null) throw new ArgumentException(request.ValidationError); }
        private bool ValidatedWriting() { ValidateRequest(); return request.ValidatedWriting; }
        private PlcB5Request XlsxRequest() { ValidateRequest(); return request; }
        private CultureInfo[] CultureNames(string raw) { ValidateRequest(); return request.Cultures.Select(CultureInfo.GetCultureInfo).ToArray(); }
        private PlcB5Object? ObjectInput(string raw)
        {
            var value = request.Inputs[raw];
            if (value.Error != null) throw new ArgumentException(value.Error);
            return value.Kind == "object" ? new PlcB5Object(HardwareScalarEvidence.Requested(value.Values)) : null;
        }
        private PlcB5Object Attributes(string raw, bool required)
        {
            if (raw == null || raw.Length > 65536) throw new ArgumentException("attributesJson exceeds 64 KiB.");
            var values = ObjectInput(raw) ?? throw new ArgumentException("attributesJson must be a JSON object.");
            if (values.Count > 50) throw new ArgumentException("At most 50 attributes per request.");
            foreach (var pair in values) if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Attribute names must be nonempty.");
            if (required && values.Count == 0) throw new ArgumentException("Nonempty attributesJson required.");
            if (!required && values.Count != 0) throw new ArgumentException("attributesJson is not accepted by this action.");
            return values;
        }
        private static string? UnitName(object? unit)
        {
#if PLC_SAFETY_UNITS
            return unit == null ? null : ((PlcUnitBase)unit).Name;
#elif PLC_UNITS
            return unit == null ? null : ((PlcUnit)unit).Name;
#else
            return null;
#endif
        }
        private static void ValidateUnitKind(string kind) { if (kind != "unit" && kind != "safety") throw new ArgumentException("unitKind must be unit or safety."); }
        private static void RequireOneOf(string value, string[] allowed, string parameter)
        { if (!allowed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException(parameter + " must be one of: " + string.Join("/", allowed) + " (case-sensitive)."); }
        private static void RequireAction(string action, string[] allowed)
        { if (!allowed.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", allowed) + "."); }
        private static void RequireName(string value, string parameter)
        { if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact " + parameter + " (single nonempty segment) required."); }
        private static void RequireExactName(string value, string parameter)
        { if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Trim() != value) throw new ArgumentException(parameter + " must be an exact nonempty name without surrounding whitespace."); }
        private static void ValidatePagination(int offset, int limit)
        { if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required."); }
        private static void RequirePagination(int offset, int limit)
        { if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and limit 1..500 required."); }
        private static void RequireConfirmation(bool confirmed, string parameter, bool dryRun)
        { if (!dryRun && !confirmed) throw new ArgumentException(parameter + "=true is required for real execution; preview does not need it."); }
        private static PlcB5Object PageMeta(int total, int offset, int limit, int returned)
            => new PlcB5Object { ["expectedCount"] = total, ["actualCount"] = returned, ["offset"] = offset, ["limit"] = limit, ["nextOffset"] = offset + returned < total ? offset + returned : (int?)null, ["truncated"] = offset + returned < total };
        private static (int Skip, int Take) Paginate(Dictionary<string, object?> meta, int total, int offset, int limit)
        {
            ValidatePagination(offset, limit); int actual = Math.Max(0, Math.Min(limit, total - offset));
            foreach (var pair in PageMeta(total, offset, limit, actual)) meta[pair.Key] = pair.Value;
            meta["nextOffset"] = offset + limit < total ? offset + limit : (int?)null;
            meta["truncated"] = offset + limit < total; meta["dataComplete"] = offset == 0 && offset + limit >= total;
            return (offset, limit);
        }
        private static void Page(object[] rows, int offset, int limit, Dictionary<string, object?> meta)
        {
            var page = rows.Skip(offset).Take(limit).ToArray(); meta["records"] = page;
            foreach (var pair in PageMeta(rows.Length, offset, limit, page.Length)) meta[pair.Key] = pair.Value;
            meta["dataComplete"] = offset == 0 && page.Length == rows.Length;
        }
        private static FileInfo RequireExistingInputFile(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException(parameter + " must be an absolute file path.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new FileNotFoundException(parameter + " does not exist or is empty.", path);
            return file;
        }
        private static HashSet<string> SnapshotDirectory(string path) => new HashSet<string>(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly), StringComparer.OrdinalIgnoreCase);
        private static PlcB5Array NewFilesSince(string path, HashSet<string> before)
            => new PlcB5Array(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Where(f => !before.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (object)PlcSourceFileOutput.Row(new FileInfo(f))).ToArray());
        private static string? ProDiagRefusal(string language, bool consistent)
            => language != "ProDiag" ? "ExportProDIAGInfo needs a ProDiag FB; the block language is " + language + "." : consistent ? null : "The ProDiag FB is inconsistent; compile first.";
        private static bool ExactNamesPresent(IEnumerable<string> imported, IEnumerable<string> current)
        { var names = imported.ToArray(); var present = new HashSet<string>(current, StringComparer.Ordinal); return names.Length > 0 && names.All(present.Contains); }
        private static IEnumerable<(string Name, string Kind, object? Unit)> PlcScopes(PlcSoftware plc)
        {
            yield return ("", "root", null);
#if PLC_UNITS
            var provider = TiaMcpServer.ModelContextProtocol.InvocationJournal.Native("PlcUnitProvider.GetService", () => plc.GetService<PlcUnitProvider>());
            if (provider == null) yield break;
            foreach (PlcUnit unit in PlcGroupOperations.Items(provider.UnitGroup.Units)) yield return (unit.Name, "unit", unit);
#if PLC_SAFETY_UNITS
            foreach (PlcSafetyUnit unit in PlcGroupOperations.Items(provider.UnitGroup.SafetyUnits)) yield return (unit.Name, "safety", unit);
#endif
#endif
        }
        private static IEnumerable<(string Path, object Value)> ScopedObjects(object group, string collection, string prefix = "", int depth = 0)
        {
            if (depth > 64) throw new InvalidOperationException("Group depth exceeds 64; result is incomplete.");
            foreach (var value in PlcGroupOperations.Items(PlcGroupOperations.Get(group, collection))) yield return (prefix + PlcGroupOperations.Get(value, "Name"), value);
            foreach (var sub in PlcGroupOperations.Items(PlcGroupOperations.Get(group, "Groups")))
                foreach (var item in ScopedObjects(sub, collection, prefix + PlcGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
            var system = group.GetType().GetProperty("SystemBlockGroups");
            if (system != null && collection == "Blocks") foreach (var sub in PlcGroupOperations.Items(system.GetValue(group)!))
                foreach (var item in ScopedObjects(sub, collection, prefix + PlcGroupOperations.Get(sub, "Name") + "/", depth + 1)) yield return item;
        }
#if PLC_DOCUMENT_EXPORT
        private static PlcB5Array DocumentMessages(DocumentResultMessageComposition? messages)
            => messages == null ? new PlcB5Array() : new PlcB5Array(PlcGroupOperations.Items(messages).Cast<DocumentResultMessage>().Select(m => (object)m.Message).ToArray());
        private static PlcB5Array Names(object values) => new PlcB5Array(PlcGroupOperations.Items(values).Select(v => (object?)PlcGroupOperations.Get(v, "Name").ToString()).ToArray());
        private static PlcB5Object DocumentExportRow(DocumentExportResult result)
            => new PlcB5Object { ["state"] = result.State.ToString(), ["exportedDocuments"] = new PlcB5Array((result.ExportedDocuments ?? Enumerable.Empty<FileInfo>()).Select(f => (object)PlcSourceFileOutput.Row(f)).ToArray()), ["messages"] = DocumentMessages(result.Messages) };
        private static PlcB5Object DocumentImportRow(DocumentImportResult result, PlcB5Array names)
            => new PlcB5Object { ["state"] = result.State.ToString(), ["imported"] = names, ["messages"] = DocumentMessages(result.Messages) };
        private object DocumentPreflight(string file) => (engine.PlcB5DocumentPreflight ?? throw new InvalidOperationException("Missing document preflight."))(File.ReadAllText(file));
        private LibraryType ExactLibraryType(object library, string path)
        {
            var parts = PlcGroupOperations.Parts(path); var folder = PlcGroupOperations.Get(library, "TypeFolder");
            foreach (var part in parts.Take(parts.Length - 1)) folder = PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Folders"), part) ?? throw new PlcSoftwareException("NotFound", "Library folder not found.");
            return (LibraryType)(PlcGroupOperations.Find(PlcGroupOperations.Get(folder, "Types"), parts.Last()) ?? throw new PlcSoftwareException("NotFound", "Exact library type not found: " + path));
        }
        private static LibraryTypeVersion ExactTypeVersion(LibraryType type, string version)
        {
            var matches = PlcGroupOperations.Items(type.Versions).Cast<LibraryTypeVersion>().Where(v => v.VersionNumber?.ToString() == version).Take(2).ToArray();
            if (matches.Length != 1) throw new PlcSoftwareException("NotFound", "Expected exactly one version '" + version + "' on type '" + type.Name + "', found " + matches.Length + ".");
            return matches[0];
        }
#endif
        private const string SupervisionProvider = "Siemens.Engineering.SW.Supervision.SupervisionProvider";
        private const string SupervisionSettingsProvider = "Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider";
        private static readonly string[] SupervisionActions = { "read", "readComposition", "createEntry", "deleteEntry", "setAttributes", "exportSettings", "importSettings" };
        private static readonly string[] OpcUaActions = { "createRole", "addStandardRole", "deleteRole", "setProjectRole", "setPermission", "setRestriction" };
        private static readonly string[] OpcUaPermissionNames = { "Browse", "Read", "Write", "Call", "ReceiveEvents", "ReadRolePermissions" };
        private static object? OptionalOfficialService(object owner, string type, out string state) => PlcOrganisationAdapter.ResolveMotionOfficialService(owner, type, out state);
        private static object RequireOfficialService(object owner, string type, string? assembly = null) => PlcOrganisationAdapter.RequireMotionOfficialService(owner, type);
        private static void AttachResult(Dictionary<string, object?> meta, object? result)
        => HardwareOfficialService.AttachResult(meta, result);
        private static void RecordOptionalFailure(Dictionary<string, object?> meta, Exception error)
        {
            var cause = error.GetBaseException(); meta["failureType"] = cause.GetType().Name;
            if (new[] { "mayHaveChanged", "mayHaveWrittenFiles", "mayHaveExternalEffects", "operationStarted" }.Any(k => meta.TryGetValue(k, out var flag) && flag is true)) return;
            meta["operationNotStarted"] = true;
            string? code = cause is PlcSoftwareException portal ? portal.Code switch {
                "InvalidParams" => "INVALID_ARGUMENT", "NotFound" => "NOT_FOUND", "NotSupportedOnVersion" => "UNSUPPORTED_CAPABILITY", "InvalidState" => "PRECONDITION_FAILED", _ => null }
                : cause is ArgumentException or TiaMcp.Adapters.Contracts.AdapterPreconditionException ? "INVALID_ARGUMENT" : cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                : cause is IOException ? "IO_FAILED" : cause is InvalidOperationException ? "PRECONDITION_FAILED" : null;
            if (code != null) meta["rejectionCode"] = code;
        }
    }
}
