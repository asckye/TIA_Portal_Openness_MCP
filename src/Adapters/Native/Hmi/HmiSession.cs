using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed partial class HmiSession : IHmiInspectionSession
    {
        private readonly PlcFoundationEngine engine;
        private readonly IPlcOrganisationSession session;
        private readonly HmiRequest request;
        internal bool MayHaveChanged { get; private set; }
        internal HmiSession(PlcFoundationEngine engine, IPlcOrganisationSession session, HmiRequest request)
        { this.engine = engine; this.session = session; this.request = request; }
        internal void MarkHmiChanged() => MayHaveChanged = true;
        internal bool IsProjectNull() => session.IsProjectNull();
        internal TiaPortal? CurrentPortal => session.CurrentPortal;
        internal SoftwareContainer? GetSoftwareContainer(string path) => session.GetSoftwareContainer(path);
        internal IDisposable AcquireHmiEditAccess() => session.AcquireHmiEditAccess();
        internal object? GetBindingIdentity() => engine.HmiBindingIdentity != null ? engine.HmiBindingIdentity() : session.BindingIdentity();
        public object ResolveHmiSoftwareOrThrow(string path) => engine.ResolveEngineeringHmiSoftware != null ? engine.ResolveEngineeringHmiSoftware(path)
            : PlcFoundationEngine.DefaultHmiSoftware(session, path);
        internal HmiTarget ExactClassicHmi(string path)
        {
            var software = session.ResolveSoftwareContainerUncached(path)?.Software ?? throw new PlcSoftwareException("NotFound", "Exact HMI software not found: " + path);
            return software as HmiTarget ?? throw new NotSupportedException("Selected software is not a classic WinCC HmiTarget (" + software.GetType().FullName + "); Unified targets use the Unified tools.");
        }
        internal VBScriptFolder ExactVbScriptFolder(HmiTarget hmi, IEnumerable<string> path) => HmiAccessHelpers.ExactVbScriptFolder(hmi, path);
        internal Siemens.Engineering.Hmi.Globalization.MultiLingualGraphicComposition ClassicGraphics()
        {
            var project = session.CurrentProject ?? throw new PlcSoftwareException("InvalidState", "Project is null.");
#if HMI_GRAPHICS_PROVIDER
            var provider = project.GetService<Siemens.Engineering.Hmi.Globalization.GraphicsProvider>()
                ?? throw new NotSupportedException("Project GraphicsProvider unavailable.");
            return provider.Graphics;
#else
            return project.Graphics;
#endif
        }
        internal object ExactOpenEngineeringLibrary(string name) => engine.ExactOpenEngineeringLibrary(name);
        internal object EngineeringLibraryFolder(object library, string path, string root) => engine.HmiLibraryFolder(library, path, root);
        internal IEnumerable<OpennessMember> DescribeMembers(object value, int limit)
            => new ReflectionSession((_, __, ___, ____) => null, () => null).DescribeMembers(value, limit);
        internal string? TryGetName(object? value) => HmiAccessHelpers.TryGetName(value);
        internal object TryGetHmiTagRoot(object value) => HmiAccessHelpers.TryGetHmiTagRoot(value);
        internal object? TryGetHmiTagTablesCollection(object value) => HmiAccessHelpers.TryGetHmiTagTablesCollection(value);
        internal object? TryFindHmiTagTable(object value, string name) => HmiAccessHelpers.TryFindHmiTagTable(value, name);
        internal IEnumerable<object> EnumerateHmiTagTablesRecursive(object value) => HmiAccessHelpers.EnumerateHmiTagTablesRecursive(value);
        internal List<string> TryListNamesFromCollection(object value, string[] hints, string final) => HmiAccessHelpers.TryListNamesFromCollection(value, hints, final);
        internal object? FindExistingByName(object value, string name) => HmiAccessHelpers.FindExistingByName(value, name);
        internal object? TryResolveChildGroupByPath(object value, string path) => HmiAccessHelpers.TryResolveChildGroupByPath(value, path);
        internal bool TryImportEngineeringObjectIntoCollection(object value, string path, out string? name, out string? error, bool overwrite = false)
            => HmiAccessHelpers.TryImportEngineeringObjectIntoCollection(value, path, out name, out error, () => MayHaveChanged = true, overwrite);
        internal IEnumerable<object> FindHardwareCatalogEntries(object value, string filter) => HmiAccessHelpers.FindHardwareCatalogEntries(value, filter);
        internal string MakeSafeFileName(string name) => HmiAccessHelpers.MakeSafeFileName(name);
        internal HmiObject Attributes(string raw, bool required)
        {
            if (raw == null || raw.Length > 65536) throw new ArgumentException("attributesJson exceeds 64 KiB.");
            if (request.AttributesError != null) throw new ArgumentException(request.AttributesError);
            var values = new HmiObject(HardwareScalarEvidence.Requested(request.Attributes)) { ScalarInputs = request.Attributes };
            if (values.Count > 50) throw new ArgumentException("At most 50 attributes per request.");
            foreach (var pair in values) if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Attribute names must be nonempty.");
            if (required && values.Count == 0) throw new ArgumentException("Nonempty attributesJson required.");
            if (!required && values.Count != 0) throw new ArgumentException("attributesJson is not accepted by this action.");
            return values;
        }
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var reply = engine.RunHmiStep(tool, values => {
                var meta = new HmiObject(values);
                try { return action(meta); }
                finally
                {
                    meta["mayHaveChanged"] = MayHaveChanged || meta["mayHaveChanged"] is true;
                    MayHaveChanged |= meta["mayHaveChanged"] is true;
                    foreach (var pair in meta) values[pair.Key] = pair.Value;
                }
            }, requiresProject);
            return new HmiMessage { Message = reply.Message, Meta = reply.Meta };
        }
        public void RecordHmiReadFault(HmiObject meta) => engine.RecordHmiFault(meta);
        internal string Capture(HmiObject meta, Func<HmiObject, string> action)
        {
            meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
            try { return action(meta); }
            catch (Exception error)
            {
                var cause = error.GetBaseException();
                meta["mayHaveChanged"] = MayHaveChanged || meta["mayHaveChanged"] is true;
                meta["v4FailureCode"] = cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                    : cause is ArgumentException or TiaMcp.Adapters.Contracts.AdapterPreconditionException ? "INVALID_ARGUMENT"
                    : cause is System.IO.FileNotFoundException || cause is System.IO.DirectoryNotFoundException ? "NOT_FOUND"
                    : cause is PlcSoftwareException portal ? portal.Code == "InvalidState" ? "PRECONDITION_FAILED" : portal.Code
                    : cause is InvalidOperationException ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED";
                throw;
            }
        }
    }
}
