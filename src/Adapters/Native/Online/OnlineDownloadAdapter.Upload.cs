using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Reflection;
using System.Threading;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
#if ONLINE_LEGITIMATION
using Siemens.Engineering.Online.Configurations;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_DOWNLOAD
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;
#endif
namespace TiaMcp.Adapters.Native.Online
{
    // Historical native observations lack a recorded PLCSIM version/date; see docs/reference/real-machine-ledger.md.
    internal sealed partial class OnlineDownloadAdapter
    {
#if ONLINE_DOWNLOAD
        // The preview only reads the route tree. A station TIA has not listed needs the live DCP scan and the route address creation;
        // both run only after approval (dryRun=false, confirmUpload=true), because the host runs this preview before asking.
        public HmiMessage UploadStationFromPlc(OnlineRequest request)
            => _session.RunHmiStepTool("UploadStationFromPlc", meta => {
                string targetIpAddress = request.TargetIpAddress, pgPcInterface = request.PgPcInterface, password = request.Password;
                bool confirmUpload = request.ConfirmUpload, dryRun = request.DryRun;
                if (string.IsNullOrWhiteSpace(targetIpAddress)) throw new ArgumentException("targetIpAddress is required (exact IP address of the station).");
                var policy = BuildDownloadPromptPolicy(request);
                var provider = _session.CurrentProject!.GetService<StationUploadProvider>()
                    ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version.");
                var picks = EnumeratePcInterfaces(provider.Configuration);
                var pick = PickPcInterface(picks, pgPcInterface);
                var seen = new List<string>();
                var address = FindTargetAddress(pick, targetIpAddress, seen);
                meta["pgPcInterface"] = new HmiObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                meta["knownTargetAddresses"] = string.Join(", ", seen.Distinct());
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["trustDeviceCertificate"] = request.TrustDeviceCertificate;
                meta["devicesBefore"] = _session.CurrentProject.Devices.Count;
                // Historical native observation: ConfigurationAddressComposition.Create takes IP addresses
                // only ("'02-C0-A8-00-C8-00' does not specify a valid address"); the official page creates "192.68.0.1". A MAC from the scan is
                // therefore never a valid target, and a virtual PLC that has not been downloaded to (IP 0.0.0.0) cannot be uploaded from.
                if (address == null && LooksLikeMacAddress(targetIpAddress))
                    throw new PortalException(PortalErrorCode.NotFound, "'" + targetIpAddress
                            + "' is a MAC address: ConfigurationAddressComposition.Create accepts IP addresses only, so pass the device's IP as listed by " +
                            "ScanAccessibleDevices (a PLC that shows only a MAC, e.g. a PLCSIM Advanced instance before its first download, has no IP yet - " +
                            "assign one by downloading first).");
                if (address != null) { meta["targetAddressSource"] = "route tree"; meta["targetAddress"] = AddressText(address); }
                if (dryRun)
                {
                    if (address != null)
                        return "Station upload preview: provider, PG/PC interface and target address resolved from the route tree; no PLC contact, no " +
                        "project change.";
#if ONLINE_SCAN
                    meta["targetAddressSource"] = "not in the route tree"; meta["targetAddress"] = targetIpAddress;
                    meta["executionEffects"] =
                        new HmiArray("DCP network scan (ConfigurationPcInterface.GetAccessibleDevices)",
                            "route address creation (ConfigurationAddressComposition.Create)");
                    return "Station upload preview: '" + targetIpAddress
                        + "' is not in the route tree of the selected PG/PC interface. Execution (after approval) runs a DCP network scan on it and creates " +
                        "the route address before uploading; this preview sent nothing to the network and changed nothing.";
#else
                    throw new PortalException(PortalErrorCode.NotFound,
                            "Target address not present on the selected PG/PC interface. This release has no accessible-device scan (V19+), so only an " +
                            "address already listed in its route tree, subnets or gateways can be uploaded from.");
#endif
                }
                if (!confirmUpload)
                    throw new InvalidOperationException("Station upload adds a new device to the project from the live PLC; set confirmUpload=true to " +
                    "execute.");
                using var access = _session.AcquireHmiEditAccess();
                if (address == null)
                {
                    meta["mayHaveChanged"] = true;   // the scan and the address creation may change the route configuration
                    address = CreateScannedAddress(pick, targetIpAddress, out var addressSource);
                    meta["addressCreation"] = addressSource;
                    if (address == null)
                        throw new PortalException(PortalErrorCode.NotFound,
                                "Target address not present on the selected PG/PC interface and not creatable from the network scan (" + addressSource + "). "
                            + "Run ScanAccessibleDevices on the same interface first; only the exact IP address listed there is accepted.");
                    meta["targetAddressSource"] = "network scan"; meta["targetAddress"] = AddressText(address);
                }
                using var legitimationScope =
                    AttachOnlineLegitimationHandler(provider.Configuration, password, meta, request.TrustDeviceCertificate, request.AllowNonTlsLogin);
                meta["mayHaveChanged"] = true;
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                var result = provider.StationUpload(address, handler);
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["uploadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new HmiArray(errors.Select(e => (object)e).ToArray()); meta["warnings"] =
                    new HmiArray(warnings.Select(w => (object)w).ToArray());
                var station = result?.UploadedStation;
                PlcB5Result
                    .Record(meta, result?.State, true, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));
                meta["uploadedStation"] = station?.Name; meta["devicesAfter"] = _session.CurrentProject.Devices.Count;
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State) && station != null && _session.CurrentProject
                    .Devices.Any(d => d.Name == station.Name);
                meta["operationSuccess"] = ok; meta["dataComplete"] = false;
                if (!ok)
                    throw new InvalidOperationException("Station upload did not yield a verified device: " + (result?.State.ToString() ?? "no result") + policy
                        .UnansweredSummary());
                return $"Station uploaded as device '{station!.Name}' and verified in the project; not saved, compiled or downloaded." + policy
                    .UnansweredSummary();
            });

#if ONLINE_ADDRESSES
        private static string AddressText(ConfigurationAddress address) => address.Address;
#else
        private static string AddressText(ConfigurationAddress address) => OnlineRoutePrimitives.Address(address);
#endif
#endif

        private static bool LooksLikeMacAddress(string value)
            => System.Text.RegularExpressions.Regex.IsMatch((value ?? "").Trim(), "^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$");

#if ONLINE_PARAMETERS
        // ParameterUploadProvider exists only on SINAMICS StartDrive head modules (DeviceItemClassifications.HM; a CPU returns null).
        // The preview selects the route without applying it; execution applies it after approval. An empty targetIpAddress passes a
        // null ConfigurationAddress, the official ParameterUpload(configuration, null, delegate) pattern.
        public HmiMessage UploadDeviceParameters(OnlineRequest request)
            => _session.RunHmiStepTool("UploadDeviceParameters", meta => {
                string targetIpAddress = request.TargetIpAddress ?? "", pgPcInterface = request.PgPcInterface, password = request.Password;
                bool confirmUpload = request.ConfirmUpload, dryRun = request.DryRun;
                bool addressRequested = !string.IsNullOrWhiteSpace(targetIpAddress);
                var policy = BuildDownloadPromptPolicy(request);
                var owner = _session.ExactEngineeringHardware("", "");
                // ParameterUploadProvider 只在 V21 PublicAPI 中存在；按名解析并经泛型 GetService 反射取得，V20 明确 NotSupported。
                var providerType = typeof(StationUploadProvider).Assembly.GetType("Siemens.Engineering.Upload.ParameterUploadProvider")
                    ?? throw new NotSupportedException("ParameterUploadProvider is not part of this TIA version's PublicAPI (V21 only).");
                if (owner is not IEngineeringServiceProvider serviceProvider) throw new NotSupportedException("Hardware object is not a service provider.");
                var getService = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(providerType);
                var provider = getService.Invoke(serviceProvider, null)
                    ?? throw new NotSupportedException("ParameterUploadProvider service unavailable on this hardware object: it exists only on SINAMICS " +
                    "StartDrive head modules (HM), not on CPUs or other device items.");
                var uploadMethod = providerType
                    .GetMethod("ParameterUpload", new[] { typeof(IConfiguration), typeof(ConfigurationAddress), typeof(UploadConfigurationDelegate) })
                    ?? throw new NotSupportedException("ParameterUpload(IConfiguration, ConfigurationAddress, delegate) unavailable.");
                var providerConfiguration = providerType.GetProperty("Configuration")?.GetValue(provider);
                (DownloadRouteSelection Route, ConfigurationAddress? Address) Resolve(bool apply)
                {
                    var selected = SelectDownloadRoute(providerConfiguration, EmptyToNull(pgPcInterface), EmptyToNull(targetIpAddress), apply);
                    if (selected.Error != null) throw new PortalException(PortalErrorCode.NotFound, selected.Error);
                    if (selected.Configuration == null)
                        throw new PortalException(PortalErrorCode.NotFound, "No PG/PC route to the device; run ScanAccessibleDevices first.");
                    ConfigurationAddress? exact = selected.Address;   // target interface, subnet / gateway or created address
                    if (addressRequested)
                        foreach (var candidate in _session.EnumerateReflectedProperty(selected.Target ?? selected.Configuration, "Addresses"))
                            if (exact == null && string
                                    .Equals(_session.ReadReflectedString(candidate, "Address"), targetIpAddress, StringComparison.OrdinalIgnoreCase)
                                    && candidate is ConfigurationAddress typed)
                                exact = typed;
                    if (addressRequested && exact == null && (apply || selected.AddressSource != "not-created"))
                        throw new PortalException(PortalErrorCode.NotFound, "Exact target address not found on the selected route: " + selected.Description);
                    return (selected, exact);
                }
                var preview = Resolve(false);
                meta["route"] = preview.Route.Description; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["passwordProvided"] = !string
                    .IsNullOrEmpty(password);
                meta["targetAddress"] = preview.Address != null ? AddressText(preview.Address) : addressRequested ? targetIpAddress : null;
                meta["addressMode"] = addressRequested ? (preview.Route.AddressSource.Length == 0 ? "targetInterface" : preview.Route.AddressSource)
                    : "null address (provider default)";
                meta["trustDeviceCertificate"] = request.TrustDeviceCertificate;
                meta["before"] = HmiScalar.Read(owner);
                if (dryRun)
                    return "Parameter upload preview: provider, route and address resolved without applying the route (ApplyConfiguration not called); no " +
                    "PLC contact, no project change.";
                if (!confirmUpload)
                    throw new InvalidOperationException("Parameter upload overwrites offline hardware parameters from the live device; set " +
                    "confirmUpload=true to execute.");
                using var access = _session.AcquireHmiEditAccess();
                meta["mayHaveChanged"] = true;
                var (route, address) = Resolve(true);
                meta["route"] = route.Description; meta["routeApplied"] = route.Applied;
                if (address != null) meta["targetAddress"] = AddressText(address);
                UploadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                if ((route.Target ?? route.Configuration) is not IConfiguration configuration)
                    throw new NotSupportedException("Selected route is not an IConfiguration.");
                using var legitimationScope =
                    AttachOnlineLegitimationHandler(providerConfiguration, password, meta, request.TrustDeviceCertificate, request.AllowNonTlsLogin);
                try { uploadMethod.Invoke(provider, new object?[] { configuration, address, handler }); }
                catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                meta["after"] = HmiScalar.Read(owner); meta["dataComplete"] = false;
                return "Parameter upload completed; compare before/after scalars for changes. Not saved, compiled or downloaded." + policy.UnansweredSummary();
            });
#endif

#if ONLINE_FOLDER
        public HmiMessage DownloadPlcToFolder(OnlineRequest request)
            => _session.RunHmiStepTool("DownloadPlcToFolder", meta => {
                string softwarePath = request.SoftwarePath, destinationDirectory = request.DestinationDirectory, targetForSoftware = request.TargetForSoftware;
                bool overwriteOnMemoryCard = request.OverwriteOnMemoryCard, confirmDownload = request.ConfirmDownload, dryRun = request.DryRun;
                if (!new[] { "CPU", "PlcSimulationAdvanced" }.Contains(targetForSoftware))
                    throw new ArgumentException("targetForSoftware must be CPU or PlcSimulationAdvanced.");
                if (!Path.IsPathRooted(destinationDirectory)) throw new ArgumentException("Absolute destinationDirectory required.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(destinationDirectory));
                if (directory.Exists && directory.EnumerateFileSystemInfos().Any())
                    throw new IOException("destinationDirectory must be new or empty; merging into existing content is refused.");
                // TargetForSoftware and OverwriteOnMemoryCard are answered from this request's own parameters.
                var policy = BuildDownloadPromptPolicy(request);
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                if (provider.GetType().GetMethod("Download", new[] { typeof(DirectoryInfo), typeof(DownloadConfigurationDelegate) }) == null)
                    throw new NotSupportedException("Download(DirectoryInfo, delegate) unavailable on this version.");
                meta["dryRun"] = dryRun; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName; meta["targetForSoftware"] =
                    targetForSoftware; meta["overwriteOnMemoryCard"] = overwriteOnMemoryCard;
                if (dryRun) return "Folder download preview: PLC, provider and destination validated; no files written.";
                if (!confirmDownload)
                    throw new InvalidOperationException("Writing a memory-card image is an explicit action; set confirmDownload=true to execute.");
                if (!directory.Exists) directory.Create();
                meta["mayHaveWrittenFiles"] = true;
                DownloadConfigurationDelegate handler = config => ApplyDownloadPrompt(config, policy);
                DownloadResult result;
                try { result = provider.Download(directory, handler); }
                catch (Exception ex) when (policy.Refused.Count > 0 || policy.ReinitializationSummary().Length > 0)
                {
                    foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                    throw new InvalidOperationException("Folder download stopped." + policy.RefusedSummary() + policy.ReinitializationSummary(), ex);
                }
                meta["apiCallSuccess"] = true;
                foreach (var kv in policy.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                var errors = new List<string>(); var warnings = new List<string>();
                CollectDownloadMessages(result?.Messages, errors, warnings);
                meta["downloadState"] = result?.State.ToString(); meta["errorCount"] = result?.ErrorCount; meta["warningCount"] = result?.WarningCount;
                meta["errors"] = new HmiArray(errors.Select(e => (object)e).ToArray()); meta["warnings"] =
                    new HmiArray(warnings.Select(w => (object)w).ToArray());
                directory.Refresh();
                var files = directory.Exists ? directory.GetFiles("*", SearchOption.AllDirectories) : Array.Empty<FileInfo>();
                meta["filesWritten"] = files.Length; meta["bytesWritten"] = files.Sum(f => f.Length); meta["dataComplete"] = false;
                PlcB5Result
                    .Record(meta, result?.State, false, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));
                meta["targetFiles"] = new HmiArray(files.Select(f => (object)PlcB5Result.FileRow(f.FullName)).ToArray());
                bool ok = result != null && TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(result.State) && files.Length > 0;
                meta["operationSuccess"] = ok;
                if (!ok)
                    throw new InvalidOperationException("Folder download did not produce a verified image: " + (result?.State.ToString() ?? "no result")
                        + policy.ReinitializationSummary() + policy.UnansweredSummary());
                return $"Memory-card image written: {files.Length} file(s) in {directory.FullName}. Content semantics not verified; no PLC contacted." + policy
                    .UnansweredSummary();
            });
#endif

    }
}
