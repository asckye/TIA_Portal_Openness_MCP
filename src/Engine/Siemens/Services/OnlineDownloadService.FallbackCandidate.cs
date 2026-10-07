using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class OnlineDownloadService
    {
        internal void FallbackUncertain() => ((Portal)_session).FallbackUncertain();
        private readonly List<KeyValuePair<object, string>> fallbackIds = new List<KeyValuePair<object, string>>();
        private readonly string fallbackScope = Guid.NewGuid().ToString("N");
        private string FallbackId(object value)
        {
            foreach (var pair in fallbackIds) if (object.Equals(pair.Key, value)) return pair.Value;
            if (fallbackIds.Count >= 16384) throw new InvalidOperationException("Fallback target identity budget exceeded.");
            string id = "transfer-" + fallbackScope + "-" + fallbackIds.Count.ToString(CultureInfo.InvariantCulture);
            fallbackIds.Add(new KeyValuePair<object, string>(value, id)); return id;
        }
        private sealed class ExactRoute
        {
            internal string Id = "";
            internal IConfiguration Configuration = null!;
            internal string TargetId = "";
        }
        private List<ExactRoute> FallbackRoutes(ConnectionConfiguration c)
        {
            var rows = new List<ExactRoute>();
            foreach (var mode in c.Modes)
                foreach (var pc in mode.PcInterfaces)
                    foreach (var target in pc.TargetInterfaces)
                    {
                        string root = "download/" + Uri.EscapeDataString(mode.Name) + "/" + Uri.EscapeDataString(pc.Name) + "/" + pc.Number.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(target.Name);
                        rows.Add(new ExactRoute { Id = root + "/target", Configuration = target, TargetId = FallbackId(target) });
                        foreach (var address in target.Addresses) rows.Add(new ExactRoute { Id = root + "/address/" + Uri.EscapeDataString(address.Address), Configuration = address, TargetId = FallbackId(address) });
                        foreach (var subnet in pc.Subnets)
                        {
                            string prefix = root + "/subnet/" + Uri.EscapeDataString(subnet.Name);
                            foreach (var address in subnet.Addresses) rows.Add(new ExactRoute { Id = prefix + "/address/" + Uri.EscapeDataString(address.Address), Configuration = address, TargetId = FallbackId(address) });
                            foreach (var gateway in subnet.Gateways)
                                foreach (var address in gateway.Addresses) rows.Add(new ExactRoute { Id = prefix + "/gateway/" + Uri.EscapeDataString(gateway.Name) + "/address/" + Uri.EscapeDataString(address.Address), Configuration = address, TargetId = FallbackId(address) });
                        }
                    }
            return rows;
        }
        internal string FallbackOfflineState(string software)
        {
            _session.ExactPlcForEngineering(software, false);
            return FallbackOffline(out _);
        }
        private string FallbackOffline(out string[] targets)
        {
            bool missing = false; int count = 0; var online = new List<string>();
            void Items(IEnumerable<DeviceItem> items, string path, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Offline inventory depth exceeded.");
                foreach (var item in items)
                {
                    if (++count > 16384) throw new InvalidOperationException("Offline inventory budget exceeded.");
                    string name = path + "/" + item.Name;
                    var provider = ((IEngineeringServiceProvider)item).GetService<OnlineProvider>();
                    if (provider != null) { if (provider.State != OnlineState.Offline) online.Add(name); }
                    else if (((IEngineeringServiceProvider)item).GetService<SoftwareContainer>()?.Software is PlcSoftware) missing = true;
                    Items(item.DeviceItems, name, depth + 1);
                }
            }
            void Devices(IEnumerable<Device> devices, string path) { foreach (var device in devices) Items(device.DeviceItems, path + "/" + device.Name, 0); }
            void Groups(IEnumerable<DeviceUserGroup> groups, string path, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Offline group depth exceeded.");
                foreach (var group in groups) { Devices(group.Devices, path + "/" + group.Name); Groups(group.Groups, path + "/" + group.Name, depth + 1); }
            }
            var project = _session.CurrentProject ?? throw new InvalidOperationException("No project is bound.");
            Devices(project.Devices, ""); Groups(project.DeviceGroups, "", 0); Devices(project.UngroupedDevicesGroup.Devices, project.UngroupedDevicesGroup.Name);
            targets = online.OrderBy(p => p, StringComparer.Ordinal).ToArray(); return targets.Length > 0 ? "online" : missing || count == 0 ? "unavailable" : "offline";
        }
        internal IFallbackAdapter FallbackAdapter(FallbackRequest request, string password, Portal portal, DocumentsService documents)
        {
            bool online = request.Entry == "DownloadPlc", folder = request.Entry == "DownloadPlcToFolder";
            ConnectionConfiguration? configuration = null; DownloadProvider? provider = null; RHDownloadProvider? rh = null;
            Dictionary<string, ExactRoute> routes = new Dictionary<string, ExactRoute>(StringComparer.Ordinal);
            PlcSoftware? plc = null;
            bool folderCompleted = false;
            string FileInventory()
            {
                var rows = new List<string>();
                if (folder)
                {
                    string path = request.Parameters["destinationDirectory"];
                    if (!Path.IsPathRooted(path)) CandidatePrimitives.Invalid("destinationDirectory");
                    if (!folderCompleted && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) CandidatePrimitives.Fail("precondition", "new-or-empty-card-directory");
                    rows.Add(Path.GetFullPath(path) + "|" + Directory.Exists(path));
                    if (folderCompleted) foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)) rows.Add(f + "|" + CandidatePrimitives.ByteHash(File.ReadAllBytes(f)));
                }
                if (request.Entry == "ImportPlcBlockDocuments")
                {
                    string path = request.Parameters["importPath"], name = request.Parameters["fileNameWithoutExtension"];
                    if (!Path.IsPathRooted(path) || EngineeringGroupOperations.Parts(name).Length != 1 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) CandidatePrimitives.Invalid("document-path");
                    foreach (var f in Directory.GetFiles(path, name + ".*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal))
                        rows.Add(Path.GetFullPath(f) + "|" + CandidatePrimitives.ByteHash(File.ReadAllBytes(f)));
                    if (rows.Count == 0) CandidatePrimitives.Fail("precondition", "document-inputs");
                }
                if (request.Entry == "ExportPlcBlockDocuments")
                {
                    string path = request.Parameters["exportPath"];
                    if (!Path.IsPathRooted(path)) CandidatePrimitives.Invalid("exportPath");
                    rows.Add(Path.GetFullPath(path));
                }
                return string.Join("\n", rows);
            }
            var adapter = new FallbackDelegateAdapter { Writes = true, NeedsConfiguration = online, RequiresOffline = !online,
                Before = () => portal.FallbackBinding(), Uncertain = portal.FallbackUncertain };
            adapter.Read = () =>
            {
                var binding = portal.FallbackBinding(); plc = _session.ExactPlcForEngineering(request.SoftwarePath, false);
                var discovered = new List<FallbackRoute>(); string inventory = FallbackId(plc) + "|" + FileInventory();
                if (online)
                {
                    string rhTarget = request.Parameters["rhTarget"];
                    if (rhTarget != "" && rhTarget != "primary" && rhTarget != "backup") CandidatePrimitives.Invalid("rhTarget");
                    provider = _session.ResolvePlcService<DownloadProvider>(request.SoftwarePath, plc) ?? throw new NotSupportedException("DownloadProvider unavailable.");
                    configuration = provider.Configuration; if (configuration == null) throw new NotSupportedException("ConnectionConfiguration unavailable.");
                    if (rhTarget.Length != 0) rh = _session.ResolvePlcService<RHDownloadProvider>(request.SoftwarePath, plc) ?? throw new NotSupportedException("RHDownloadProvider unavailable.");
                    routes = FallbackRoutes(configuration).ToDictionary(r => r.Id, StringComparer.Ordinal);
                    foreach (var r in routes.Values) discovered.Add(new FallbackRoute { Id = r.Id, TargetId = r.TargetId,
                        NativeCalls = new[] { "ConnectionConfiguration.ApplyConfiguration", rhTarget == "primary" ? "RHDownloadProvider.DownloadToPrimary" : rhTarget == "backup" ? "RHDownloadProvider.DownloadToBackup" : "DownloadProvider.Download" } });
                }
                else
                {
                    string native = folder ? "DownloadProvider.Download(DirectoryInfo)" : request.Entry == "CompilePlcSoftware" ? "ICompilable.Compile"
                        : request.Entry == "ExportPlcBlockDocuments" ? "ExportAsDocuments" : "ImportFromDocuments";
                    discovered.Add(new FallbackRoute { Id = "offline/" + request.Entry + "/" + Uri.EscapeDataString(request.SoftwarePath), TargetId = FallbackId(plc), NativeCalls = new[] { native } });
                    if (folder) provider = _session.ResolvePlcService<DownloadProvider>(request.SoftwarePath, plc) ?? throw new NotSupportedException("DownloadProvider unavailable.");
                }
                string offline = "not-applicable";
                var onlineTargets = Array.Empty<string>(); if (!online) offline = FallbackOffline(out onlineTargets);
                return new FallbackObservation { Binding = binding, TargetId = FallbackId(plc), OfflineState = offline, OfflineTargets = onlineTargets,
                    Routes = discovered.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), InventoryHash = CandidatePrimitives.ByteHash(Encoding.UTF8.GetBytes(inventory)) };
            };
            adapter.Configure = route =>
            {
                var selected = routes[route];
                if (selected.Configuration is ConfigurationAddress address) return configuration!.ApplyConfiguration(address);
                return configuration!.ApplyConfiguration((ConfigurationTargetInterface)selected.Configuration);
            };
            adapter.Operation = (r, evidence) =>
            {
                portal.FallbackBinding();
                if (online || folder)
                {
                    bool Flag(string key) => r.Parameters.TryGetValue(key, out var value) && value == "true";
                    var policy = BuildDownloadPromptPolicy(folder || Flag("consistentBlocksOnly"), folder || Flag("keepActualValues"), !folder && Flag("startAfterDownload"), !folder && Flag("stopBeforeDownload"), "keep", "{}", password, null, null);
                    DownloadConfigurationDelegate pre = c => ApplyDownloadPrompt(c, policy), post = c => ApplyDownloadPrompt(c, policy);
                    DownloadResult result;
                    void Returned(DownloadResult raw)
                    {
                        var observed = new FallbackNativeResult { State = "returned" }; evidence.NativeResult = observed;
                        observed.State = raw.State.ToString(); observed.Success = raw.State != DownloadResultState.Error && raw.ErrorCount == 0;
                        observed.Items = new[] { new Dictionary<string, string> { ["errorCount"] = raw.ErrorCount.ToString(CultureInfo.InvariantCulture),
                            ["warningCount"] = raw.WarningCount.ToString(CultureInfo.InvariantCulture), ["unansweredPrompts"] = policy.UnansweredSummary() } };
                    }
                    if (folder)
                    {
                        string target = r.Parameters["targetForSoftware"]; if (target != "CPU" && target != "PlcSimulationAdvanced") CandidatePrimitives.Invalid("targetForSoftware");
                        policy.Explicit["TargetForSoftware"] = target; policy.Explicit["OverwriteOnMemoryCard"] = "NoAction";
                        var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(r.Parameters["destinationDirectory"]));
                        if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) CandidatePrimitives.Fail("stale", "card-directory-changed");
                        evidence.WriteIssued = true; if (!directory.Exists) directory.Create();
                        evidence.OperationIssued = true; result = provider!.Download(directory, pre); Returned(result); folderCompleted = true;
                    }
                    else
                    {
                        var selected = routes[r.Route]; string target = r.Parameters["rhTarget"];
                        using var legitimation = AttachOnlineLegitimationHandler(configuration, password, new System.Text.Json.Nodes.JsonObject(), Flag("trustDeviceCertificate"));
                        evidence.OperationIssued = true; evidence.WriteIssued = true;
                        result = target == "primary" ? rh!.DownloadToPrimary(selected.Configuration, pre, post, DownloadOptions.Software)
                            : target == "backup" ? rh!.DownloadToBackup(selected.Configuration, pre, post, DownloadOptions.Software)
                            : provider!.Download(selected.Configuration, pre, post, DownloadOptions.Software);
                        Returned(result);
                    }
                    var errors = new List<string>(); var warnings = new List<string>(); CollectDownloadMessages(result.Messages, errors, warnings);
                    evidence.NativeResult!.Messages = errors.Concat(warnings).ToArray(); return evidence.NativeResult;
                }
                return OnlineToolPolicy.WithOfflineRequirement(() =>
                {
                    evidence.OperationIssued = true; evidence.WriteIssued = true;
                    if (r.Entry == "CompilePlcSoftware")
                    {
                        var result = _session.CompileSoftware(r.SoftwarePath, password);
                        return new FallbackNativeResult { Success = result.ErrorCount == 0 && result.State != global::Siemens.Engineering.Compiler.CompilerResultState.Error, State = result.State.ToString(),
                            Items = new[] { new Dictionary<string, string> { ["errorCount"] = result.ErrorCount.ToString(CultureInfo.InvariantCulture), ["warningCount"] = result.WarningCount.ToString(CultureInfo.InvariantCulture) } } };
                    }
                    bool ok = r.Entry == "ExportPlcBlockDocuments" ? documents.ExportAsDocuments(r.SoftwarePath, r.Parameters["blockPath"], r.Parameters["exportPath"], r.Parameters["preservePath"] == "true")
                        : documents.ImportFromDocuments(r.SoftwarePath, r.Parameters["groupPath"], r.Parameters["importPath"], r.Parameters["fileNameWithoutExtension"], ImportDocumentOptions.None);
                    return new FallbackNativeResult { Success = ok, State = ok ? "completed" : "failed" };
                }, () => FallbackOfflineState(r.SoftwarePath));
            };
            return adapter;
        }
    }
}
