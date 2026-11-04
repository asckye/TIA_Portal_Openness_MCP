#nullable disable
using System;
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if TIA_ADAPTER_INTERNAL_VERSIONING
using TiaMcp.Adapters.Contracts.Studio;
using TiaMcp.Adapters.Contracts.Studio.Errors;
#else
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
#endif
#if TIA_ADAPTER_INTERNAL_VERSIONING
using IVersionControl = TiaMcp.Adapters.Contracts.IVersionControl;
using ITiaSession = TiaMcp.Adapters.Contracts.IStudioSession;
using IVciReadOnlySession = TiaMcp.Adapters.Contracts.IStudioVciReader;
#else
using TiaOpenness.Core.Abstractions;
#endif
#if !TIA_ADAPTER_INTERNAL_VERSIONING
using TiaOpenness.Core.Inspection;
#endif

namespace TiaOpenness.Openness
{
    /// <summary>
    /// The real <see cref="ITiaSession"/>, backed by Siemens.Engineering.
    ///
    /// Lives only inside the bridge process, which is bound to exactly one Openness version
    /// by <c>OpennessAssemblyResolver</c> before this type is ever touched.
    /// </summary>
    internal sealed partial class OpennessSession : ITiaSession, IVciReadOnlySession
    {
        private readonly string _boundVersion;
        private TiaPortal _portal;
        private Project _project;
        private bool _withUi;
        // True only for a TIA Portal this session started; an attached instance belongs to the user (F11).
        private bool _startedPortal;
#if STUDIO_VCI
        private OpennessVersionControl _versionControl;
#endif
#if STUDIO_VCI
        private Project _versionControlOwner;
#endif

        /// <summary>Device name -> indexed software. Rebuilt whenever the project changes.</summary>
        private readonly Dictionary<string, PlcContext> _plcs =
            new Dictionary<string, PlcContext>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Device name -> indexed HMI screens and tag tables. Cleared with the PLC indexes.</summary>
        private readonly Dictionary<string, HmiContext> _hmis =
            new Dictionary<string, HmiContext>(StringComparer.OrdinalIgnoreCase);

        public OpennessSession(string boundVersion)
        {
            _boundVersion = boundVersion;
        }

        public SessionMode Mode { get { return SessionMode.Openness; } }
        public bool IsConnected { get { return _portal != null; } }
        public bool HasProject { get { return _project != null; } }

        /// <summary>
        /// The project's Version Control Interface, or null when the service is unavailable. Built once per project: the VCI object graph is COM-backed and
        /// dies if the service it came from is collected, so it must not be re-created per call.
        /// </summary>
        public IVersionControl VersionControl
        {
            get
            {
#if STUDIO_VCI
                if (_project == null) return null;

                if (_versionControl == null || !ReferenceEquals(_versionControlOwner, _project))
                {
                    if (!OpennessVersionControl.IsAvailable(_project)) return null;

                    _versionControl = new OpennessVersionControl(() => _project);
                    _versionControlOwner = _project;
                }
                return _versionControl;
#else
                return null;
#endif
            }
        }

        // ---- session -------------------------------------------------------

        public SessionState AttachVciReadOnly(int processId, long startUtcTicks, string projectFile)
        {
            if (_portal != null) throw new InvalidOperationException("VCI reader already attached.");
            using (var process = Process.GetProcessById(processId))
                if (process.StartTime.ToUniversalTime().Ticks != startUtcTicks)
                    throw new InvalidOperationException("VCI process identity changed.");
            var target = TiaPortal.GetProcesses().SingleOrDefault(p => p.Id == processId)
                ?? throw new InvalidOperationException("The shared TIA process is unavailable.");
            _portal = target.Attach();
            try
            {
                _project = _portal.Projects.SafeEnumerate().SingleOrDefault(p =>
                    string.Equals(p.Path.FullName, projectFile, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The shared project identity changed.");
                return GetState();
            }
            catch { Disconnect(); throw; }
        }

        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version)
        {
            if (_portal != null) return GetState();

            _withUi = withUserInterface;

            if (attachToRunning)
            {
                // F10: attach only to an exactly selected process. Several running instances are refused, never guessed;
                // a failed or denied attach is reported and never followed by a silent new instance.
                var processes = TiaPortal.GetProcesses();
                if (processes.Count > 1)
                    throw new InvalidOperationException("Several TIA Portal processes are running ("
                        + string.Join("; ", processes.Select(p => "PID " + p.Id + ": " + (ProcessProject(p) ?? "no project")))
                        + "). Studio attaches only to an exactly selected instance: close the other TIA Portal instances, or use an MCP session "
                        + "(ConnectPortal with the processId) to select one.");
                if (processes.Count == 1)
                {
                    _portal = AttachExactly(processes[0]);
                    _startedPortal = false;
                    Console.Error.WriteLine("[bridge] attached to the running TIA Portal (PID " + processes[0].Id + ")");
                    Console.Error.Flush();

                    // An attached instance keeps its project; bind the primary one (secondary projects are read-only and hidden).
                    _project = PrimaryProject(_portal);
                    if (_project != null) IndexProject();
                    return GetState();
                }
            }

            // Starting TIA Portal is TIA's own cost - a minute is normal - and there is nothing to
            // report while it happens, so say up front what is being waited on. Leaving an already
            // running TIA Portal open is much faster, because the branch above attaches to it.
            Console.Error.WriteLine("[bridge] no running TIA Portal to attach to; starting one, " +
                                    "which usually takes about a minute");
            Console.Error.Flush();

            _portal = new TiaPortal(withUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);
            _startedPortal = true;

            return GetState();
        }

        private static string ProcessProject(TiaPortalProcess process)
        {
            try { return process.ProjectPath?.FullName; }
            catch (Exception) /* swallow(probe-optional): the refusal still lists the PID when the project path is unreadable */ { return null; }
        }

        private static TiaPortal AttachExactly(TiaPortalProcess process)
        {
            try { return process.Attach(); }
            catch (EngineeringSecurityException denied)
            {
                throw new InvalidOperationException("TIA Portal Openness denied access to the bridge (EngineeringSecurityException). Allow it in the TIA " +
                    "Portal "
                    + "Openness access dialog (Yes, or Yes to all), or have an administrator add it to " +
                        "HKLM\\SOFTWARE\\Siemens\\Automation\\Openness\\AllowList; "
                    + "the Windows user must be a member of 'Siemens TIA Openness'. No other TIA Portal was started. TIA: " + denied.Message, denied);
            }
        }

        private static Project PrimaryProject(TiaPortal portal)
        {
            var projects = portal.Projects.SafeEnumerate().ToList();
#if PROJECT_IS_PRIMARY
            var primary = projects.Where(p => p.IsPrimary).ToList();
            return primary.Count == 1 ? primary[0] : null;
#else
            return projects.Count == 1 ? projects[0] : null;
#endif
        }

        public void Disconnect()
        {
            // F11: a TIA Portal this session started closes with Dispose when it has no UI; never discard its project's changes.
            if (_portal != null && _startedPortal && _project != null)
            {
                if (_project.IsModified)
                    throw new InvalidOperationException("The project in the TIA Portal this Studio session started has unsaved changes. Save it, "
                        + "or close it explicitly, before disconnecting; Studio never saves or discards changes on its own.");
                _project.Close();
            }
            _plcs.Clear();
            _hmis.Clear();
#if STUDIO_VCI
            _versionControl = null;
#endif
#if STUDIO_VCI
            _versionControlOwner = null;
#endif
            _project = null;

            if (_portal == null) return;
            try { _portal.Dispose(); }
            catch (Exception) /* swallow(teardown): the Portal may already have exited; disconnect still clears the local session reference */ { /* TIA may already be gone */ }
            _portal = null;
            _startedPortal = false;
        }

        public SessionState GetState()
        {
            return new SessionState
            {
                Connected = _portal != null,
                Mode = SessionMode.Openness,
                OpennessVersion = _boundVersion,
                WithUserInterface = _withUi,
                OpenProject = _project == null ? null : Describe(_project),
            };
        }

        // ---- project -------------------------------------------------------

        public ProjectInfo OpenProject(string path)
        {
            RequireConnected();

            var file = new FileInfo(TiaOpenness.Shared.NativePathSelection.FullPath(path));
            if (!file.Exists)
            {
                throw new FileNotFoundException("Project file not found: " + path, path);
            }

            // Re-opening the same project would throw; reuse what is already open.
            var already = _portal.Projects.SafeEnumerate().FirstOrDefault(p =>
                string.Equals(p.Path?.FullName, file.FullName, StringComparison.OrdinalIgnoreCase));

            _project = already ?? _portal.Projects.Open(file);
            IndexProject();
            return Describe(_project);
        }

        public ProjectInfo GetProjectInfo()
        {
            RequireProject();
            return Describe(_project);
        }

        public void SaveProject()
        {
            RequireProject();
            _project.Save();
        }

        public void CloseProject()
        {
            if (_project == null) return;
            try { _project.Close(); }
            finally
            {
                _project = null;
    #if STUDIO_VCI
            _versionControl = null;
#endif
    #if STUDIO_VCI
            _versionControlOwner = null;
#endif
                _plcs.Clear();
            _hmis.Clear();
            }
        }

        private static ProjectInfo Describe(Project project)
        {
            return new ProjectInfo
            {
                Name = project.Name,
                Path = project.Path?.FullName,
                Author = project.Prop<string>("Author"),
                Comment = (project.Prop<object>("Comment") as MultilingualText).AsText(),
                CreationTime = project.Prop<DateTime>("CreationTime").AsOffset(),
                LastModified = project.Prop<DateTime>("LastModified").AsOffset(),
                IsModified = project.Prop("IsModified", false),
            };
        }

        // ---- devices and blocks --------------------------------------------

        public IReadOnlyList<DeviceInfo> ListDevices()
        {
            RequireProject();
            return PlcNavigator.AllDeviceEntries(_project).Select(PlcNavigator.Describe).ToList();
        }

        // ---- plumbing ------------------------------------------------------

        /// <summary>
        /// Drops the cached indexes. It does not build any.
        ///
        /// Indexing walks every block, data type and tag table of a PLC, and each step is a COM
        /// round trip. Doing that for every PLC the moment a project opened made opening a large
        /// project take as long as reading all of it - before the engineer had even chosen a
        /// device, and for devices they never looked at. Each PLC is now indexed the first time
        /// something actually asks for it.
        /// </summary>
        private void IndexProject()
        {
            _plcs.Clear();
            _hmis.Clear();
        }

        private static void Index(PlcContext context)
        {
            PlcNavigator.Index(context);
        }

        private void RequireConnected()
        {
            if (_portal == null) throw new SessionPreconditionException(SessionFailureReason.NotConnected, "Not connected. Call session.connect first.");
        }

        private void RequireProject()
        {
            RequireConnected();
            if (_project == null) throw new SessionPreconditionException(SessionFailureReason.NoProjectOpen, "No project is open. Call project.open first.");
        }

        /// <summary>
        /// The indexed software of one PLC, built on first use and kept until the project changes.
        /// Throws when the device carries no PLC software; use <see cref="TryGetPlc"/> where an
        /// empty answer is the right one.
        /// </summary>
        private PlcContext RequirePlc(string deviceId)
        {
            PlcContext plc;
            if (TryGetPlc(deviceId, out plc)) return plc;

            throw new KeyNotFoundException("Device '" + deviceId + "' carries no PLC software, " +
                "so this operation does not apply to it.");
        }

        private bool TryGetHmi(string deviceId, out HmiContext hmi)
        {
            RequireProject();

            if (_hmis.TryGetValue(deviceId, out hmi)) return true;

            var device = PlcNavigator.AllDevices(_project).FirstOrDefault(d =>
                string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase));

            var software = device == null ? null : PlcNavigator.FindSoftware(device);
            if (!HmiNavigator.IsHmiTarget(software)) return false;

            hmi = new HmiContext { DeviceId = device.Name, Target = software };
            HmiNavigator.Index(hmi);
            _hmis[device.Name] = hmi;
            return true;
        }

        /// <summary>
        /// The indexed software of one PLC, or false when this device has none.
        ///
        /// A project lists racks, HMIs and drives beside its PLCs, and the app shows all of them.
        /// Asking one of those for its blocks is an ordinary question with an empty answer, not a
        /// fault - reporting it as an error meant clicking a rack in the device list raised one.
        /// An unknown device name is still an error, because that is a caller mistake.
        /// </summary>
        private bool TryGetPlc(string deviceId, out PlcContext plc)
        {
            RequireProject();

            if (_plcs.TryGetValue(deviceId, out plc)) return true;

            var devices = PlcNavigator.AllDevices(_project).ToList();
            var device = devices.FirstOrDefault(d =>
                string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                throw new KeyNotFoundException("No device named '" + deviceId + "'. Devices in this project: " +
                    (devices.Count == 0 ? "(none)" : string.Join(", ", devices.Select(d => d.Name))));
            }

            var software = PlcNavigator.FindPlcSoftware(device);
            if (software == null) return false;

            plc = new PlcContext { DeviceId = device.Name, Software = software };
            Index(plc);
            _plcs[device.Name] = plc;
            return true;
        }

        public void Dispose()
        {
            Disconnect();
        }

    }
}
