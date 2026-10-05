using static TiaMcpServer.Siemens.Portal;
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
#if TIA_SHARED_ADAPTER_PATHS
using Hardware = TiaMcp.Adapters.Hardware.HardwarePrimitives;
#else
using Hardware = TiaMcpServer.Siemens.LocalHardware.HardwarePrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DevicesService
    {
        private readonly IEngineeringSession _session;

        public DevicesService(IEngineeringSession session)
        {
            _session = session;
        }


        public Device AddDevice(string orderNumber, string version, string deviceName)
        {
            _session.Logger?.LogInformation($"Adding device: {deviceName}, OrderNumber={orderNumber}, Version={version}");
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            string? lastVariantError = null;
            try
            {
                // Openness CreateWithItem expects a TypeIdentifier, not split order/version.
                // Example: OrderNumber:6ES7 513-1AM03-0AB0/V3.0
                var project = (HardwareProject as Project);
                if (project == null) throw new PortalException(PortalErrorCode.InvalidState, "Current project is not a local Project instance");

                var orderRaw = orderNumber ?? "";
                var verRaw = version ?? "";
                GuardUnifiedPanelVersion(orderRaw, verRaw);

                var orderVariants = new List<string>
                {
                    orderRaw,
                    NormalizeOrderNumber(orderRaw),
                    TryFormatMlfbWithSpaces(orderRaw),
                    TryFormatMlfbWithSpaces(NormalizeOrderNumber(orderRaw))
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var v = verRaw.Trim();
                var vNoV = v.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;
                var versionVariants = new List<string>
                {
                    v,
                    "V" + vNoV,
                    vNoV,
                    "" // let TIA pick default/latest if supported
                };

                // append common ".0" expansions (V3.0 -> V3.0.0 etc.)
                foreach (var baseV in new[] { v, "V" + vNoV, vNoV })
                {
                    if (string.IsNullOrWhiteSpace(baseV)) continue;
                    if (!baseV.Contains('.')) continue;

                    versionVariants.Add(baseV + ".0");
                    versionVariants.Add(baseV + ".0.0");
                }

                // Heuristic: if user provides TIA20.* for Unified panels, try bump to 21.* as well.
                // (Common when copying order/version from older screenshots.)
                try
                {
                    var parts = vNoV.Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var maj) && maj == 20)
                    {
                        versionVariants.Add("21.0.0.0");
                        versionVariants.Add("21.0.0.1");
                        versionVariants.Add("V21.0.0.0");
                    }
                }
                catch /* swallow(probe-optional): Version expansion is optional; retain the explicit variants when it cannot be computed. */ { }

                versionVariants = versionVariants
                    .Where(x => x != null) // keep empty-string variant
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var typeIdentifierVariants = new List<string>();
                foreach (var o in orderVariants)
                {
                    foreach (var ver in versionVariants)
                    {
                        if (o.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
                        {
                            typeIdentifierVariants.Add(o);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(ver))
                            typeIdentifierVariants.Add($"OrderNumber:{o}/{ver}");
                    }
                }

                typeIdentifierVariants = typeIdentifierVariants
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Keep one line per variant - the TIA V21 project (2026-09-20; docs/reference/real-machine-ledger.md, TP700 Comfort, catalog TypeIdentifier
                // "OrderNumber:6AV2 124-0GC01-0AX0/14.0.1.0") only ever showed the LAST variant's error (".../V14.0.1.0.0.0"), so the
                // reason the exact catalog identifier was refused stayed invisible.
                var attempts = new List<string>();
                foreach (var typeIdentifier in typeIdentifierVariants)
                {
                    foreach (var itemName in new[] { deviceName, "Device_1", "Station_1" }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var dev = Hardware.CreateWithItem(Hardware.Devices(project), typeIdentifier, itemName, deviceName);
                            if (dev is Device d) return d;
                            attempts.Add($"{typeIdentifier} [{itemName}] -> CreateWithItem returned null");
                        }
                        catch (Exception exTry)
                        {
                            // try next variant
                            lastVariantError = _session.FormatExceptionDetail(exTry);
                            attempts.Add($"{typeIdentifier} [{itemName}] -> {ShortExceptionMessage(exTry)}");
                        }
                    }
                }

                var shown = attempts.Take(24).ToList();
                var more = attempts.Count > shown.Count ? $"\n... {attempts.Count - shown.Count} more" : "";
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CreateDevice failed: no device created for OrderNumber={orderNumber} Version={version}; {attempts.Count} CreateWithItem attempt(s):\n"
                    + string.Join("\n", shown) + more
                    + "\nHint: SearchHardwareCatalog / CreateHardwareCatalogDevice report the catalog's exact TypeIdentifier (HMI panels carry the version without a 'V', e.g. '.../14.0.1.0'); pass that as orderNumber (a value starting with 'OrderNumber:' is used verbatim)."
                    + (lastVariantError != null ? "\nLast error detail: " + lastVariantError : ""));
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError, _session.FormatExceptionDetail(ex), null, ex);
            }
        }

        public (Device? Device, string? MlfbUsed, string? VersionUsed, List<string> Attempts, string? Error) AddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family)
        {
            var attempts = new List<string>();
            string? lastError = null;

            // Minimal built-in list (keep small; user can pass preferred MLFB first)
            var known = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["S7-1500"] = new List<string>
                {
                    "6ES7513-1AM03-0AB0",
                    "6ES7516-3AN03-0AB0",
                    "6ES7515-2AM02-0AB0",
                    "6ES7513-1AL03-0AB0",
                    "6ES7512-1AK02-0AB0",
                },
                ["WinCCUnifiedPC"] = new List<string>
                {
                    // Unified PC Runtime (actual availability depends on installed packages/HSP)
                    "6AV2123-3GB32-0AW0",
                    "6AV2154-0BS01-0AA0",
                    "6AV2154-0BP01-0AA0",
                },
                ["S7-1200"] = new List<string>
                {
                    // CPU 1211C AC/DC/Rly
                    "6ES7211-1BE40-0XB0",
                }
            };

            var mlfbs = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredMlfb)) mlfbs.Add(preferredMlfb);
            if (known.TryGetValue(family ?? "", out var list)) mlfbs.AddRange(list);
            mlfbs = mlfbs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredVersion)) versions.Add(preferredVersion);
            if (string.Equals(family, "S7-1500", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V3.0");
                versions.Add("V3.1");
                versions.Add("V2.9");
            }
            if (string.Equals(family, "WinCCUnifiedPC", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("20.0.0.0");
                versions.Add("21.0.0.0");
            }
            if (string.Equals(family, "S7-1200", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V4.7");
                versions.Add("4.7");
                versions.Add("V4.6");
                versions.Add("4.6");
                versions.Add("V4.5");
                versions.Add("4.5");
            }
            versions.Add("21.0.0.0");
            versions.Add("V21.0.0.0");
            versions.Add("");
            versions = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var mlfb in mlfbs)
            {
                foreach (var ver in versions)
                {
                    try
                    {
                        var d = AddDevice(mlfb, ver, deviceName);
                        attempts.Add($"{mlfb} {ver} -> OK");
                        return (d, mlfb, ver, attempts, null);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        attempts.Add($"{mlfb} {ver} -> FAIL: {ex.Message}");
                    }
                }
            }

            return (null, null, null, attempts, lastError ?? "All attempts failed");
        }

        public List<GsdDeviceCandidate> SearchInstalledGsdDevices(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<GsdDeviceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _session.CurrentPortal == null ? null : TryGetPropertyValue(_session.CurrentPortal, "HardwareCatalog");
                if (catalog != null)
                {
                    foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                    {
                        foreach (var entry in _session.FindHardwareCatalogEntries(catalog, filter))
                        {
                            var candidate = CatalogEntryToCandidate(entry, normalizedKeyword);
                            if (candidate == null) continue;

                            var key = candidate.TypeIdentifierNormalized
                                      ?? candidate.TypeIdentifier
                                      ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                            if (!seen.Add(key)) continue;
                            results.Add(candidate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _session.Logger?.LogWarning(ex, "HardwareCatalog search failed during GSD device search");
            }

            try
            {
                foreach (var candidate in SearchGsdmlFiles(normalizedKeyword))
                {
                    var key = $"GSDML|{candidate.GsdmlPath}|{candidate.DapId}|{candidate.DapName}";
                    if (!seen.Add(key)) continue;
                    results.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                _session.Logger?.LogWarning(ex, "GSDML scan failed during GSD device search");
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, normalizedKeyword, null);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.Source)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public List<HardwareCatalogCandidate> SearchHardwareCatalog(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<HardwareCatalogCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _session.CurrentPortal == null ? null : TryGetPropertyValue(_session.CurrentPortal, "HardwareCatalog");
                if (catalog == null)
                    throw new PortalException(PortalErrorCode.InvalidState, "TIA Portal HardwareCatalog is not available. Connect to TIA Portal first.");

                foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                {
                    foreach (var entry in _session.FindHardwareCatalogEntries(catalog, filter))
                    {
                        var candidate = CatalogEntryToHardwareCandidate(entry, normalizedKeyword);
                        if (candidate == null) continue;

                        var key = candidate.TypeIdentifierNormalized
                                  ?? candidate.TypeIdentifier
                                  ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                        if (!seen.Add(key)) continue;
                        results.Add(candidate);
                    }
                }
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _session.Logger?.LogWarning(ex, "HardwareCatalog search failed");
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, normalizedKeyword);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.ArticleNumber)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public (Device? Device, HardwareCatalogCandidate? Candidate, List<HardwareCatalogCandidate> Candidates, List<string> Attempts, string? Error)
            AddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "")
        {
            var attempts = new List<string>();
            var candidates = SearchHardwareCatalog(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, keyword, preferredText);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No hardware catalog candidates found for '{keyword}'");
            }

            if (_session.IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = HardwareProject as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    GuardUnifiedPanelVersion(typeIdentifier, "");        // The Unified panel version guard also applies to catalog candidates.
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = Hardware.CreateWithItem(Hardware.Devices(project), typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (PortalException pex) when (pex.Code == PortalErrorCode.InvalidParams)
                {
                    lastError = pex.Message;
                    attempts.Add($"{typeIdentifier} -> SKIPPED: {pex.Message}");
                }
                catch (Exception ex)
                {
                    lastError = _session.FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => c.Insertable == true))
                lastError = "Hardware catalog returned no insertable TypeIdentifier candidates.";

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        public (Device? Device, GsdDeviceCandidate? Candidate, List<GsdDeviceCandidate> Candidates, List<string> Attempts, string? Error)
            AddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "")
        {
            var attempts = new List<string>();
            var candidates = SearchInstalledGsdDevices(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, keyword, preferredDap);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No installed GSD/catalog candidates found for '{keyword}'");
            }

            if (_session.IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = HardwareProject as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = Hardware.CreateWithItem(Hardware.Devices(project), typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = _session.FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                lastError = "Only GSDML file metadata was found; HardwareCatalog did not return an insertable TypeIdentifier.";
            }

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        private static IEnumerable<string> BuildHardwareCatalogFilters(string keyword)
        {
            var raw = (keyword ?? string.Empty).Trim();
            var tokens = raw.Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return new[] { raw }
                .Concat(tokens.Where(t => t.Length >= 3))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static GsdDeviceCandidate? CatalogEntryToCandidate(object entry, string keyword)
        {
            var c = new GsdDeviceCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                Version = TryGetPropertyValue(entry, "Version")?.ToString()
            };

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static HardwareCatalogCandidate? CatalogEntryToHardwareCandidate(object entry, string keyword)
        {
            // The official HardwareCatalog.Find rows are typed CatalogEntry objects; the reflective read stays as fallback.
            var c = entry is global::Siemens.Engineering.HW.HardwareCatalog.CatalogEntry typed
                ? new HardwareCatalogCandidate
                {
                    Source = "HardwareCatalog", Keyword = keyword, ArticleNumber = Hardware.ArticleNumber(typed), CatalogPath = Hardware.CatalogPath(typed), Description = Hardware.Description(typed),
                    TypeIdentifier = Hardware.TypeIdentifier(typed), TypeIdentifierNormalized = Hardware.TypeIdentifierNormalized(typed), TypeName = Hardware.TypeName(typed), Version = Hardware.Version(typed)
                }
                : new HardwareCatalogCandidate
                {
                    Source = "HardwareCatalog",
                    Keyword = keyword,
                    ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                    CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                    Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                    TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                    TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                    TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                    Version = TryGetPropertyValue(entry, "Version")?.ToString()
                };
            c.Insertable = !string.IsNullOrWhiteSpace(c.TypeIdentifier);

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static IEnumerable<GsdDeviceCandidate> SearchGsdmlFiles(string keyword)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Siemens",
                "Automation",
                $"Portal V{Engineering.TiaMajorVersion}",
                "data",
                "xdd",
                "gsd");

            if (!Directory.Exists(root)) yield break;

            foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                string textPrefix = fileName;
                if (!ContainsAnyKeywordToken(textPrefix, keyword))
                {
                    try
                    {
                        var raw = File.ReadAllText(path, Encoding.UTF8);
                        if (!ContainsAnyKeywordToken(raw, keyword)) continue;
                    }
                    catch /* swallow(parse-fallback): Unreadable or malformed GSDML files cannot provide catalog candidates; continue with the remaining files. */
                    {
                        continue;
                    }
                }

                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch /* swallow(parse-fallback): Unreadable or malformed GSDML files cannot provide catalog candidates; continue with the remaining files. */ { continue; }

                var vendor = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProfileHeader")?.Attribute("VendorName")?.Value;
                var family = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Family");
                var mainFamily = family?.Attribute("MainFamily")?.Value;
                var productFamily = family?.Attribute("ProductFamily")?.Value;
                var orderNumber = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OrderNumber")?.Attribute("Value")?.Value;

                foreach (var dap in doc.Descendants().Where(e => e.Name.LocalName == "DeviceAccessPointItem"))
                {
                    var dapId = dap.Attribute("ID")?.Value;
                    var dapName = dap.Attribute("DNS_CompatibleName")?.Value ?? dap.Attribute("Name")?.Value;
                    var textId = dap.Attribute("TextId")?.Value;
                    var infoText = FindGsdText(doc, textId) ?? dapName ?? fileName;
                    var haystack = string.Join(" ", new[] { vendor, mainFamily, productFamily, orderNumber, dapId, dapName, infoText, fileName });
                    if (!ContainsAnyKeywordToken(haystack, keyword)) continue;

                    yield return new GsdDeviceCandidate
                    {
                        Source = "GSDML",
                        Keyword = keyword,
                        Vendor = vendor,
                        MainFamily = mainFamily,
                        ProductFamily = productFamily,
                        DapId = dapId,
                        DapName = dapName,
                        ArticleNumber = orderNumber,
                        Description = infoText,
                        GsdmlPath = path,
                        Score = ScoreGsdCandidateText(haystack, keyword, null)
                    };
                }
            }
        }

        private static string? FindGsdText(XDocument doc, string? textId)
        {
            if (string.IsNullOrWhiteSpace(textId)) return null;
            var text = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text" && string.Equals(e.Attribute("TextId")?.Value, textId, StringComparison.OrdinalIgnoreCase));
            return text?.Attribute("Value")?.Value;
        }

        private static int ScoreGsdCandidate(GsdDeviceCandidate c, string keyword, string? preferredDap)
        {
            var text = string.Join(" ", new[]
            {
                c.Vendor, c.ProductFamily, c.MainFamily, c.DapId, c.DapName, c.ArticleNumber, c.CatalogPath,
                c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version, c.GsdmlPath
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, preferredDap)
                   + (string.Equals(c.Source, "HardwareCatalog", StringComparison.OrdinalIgnoreCase) ? 30 : 0)
                   + (!string.IsNullOrWhiteSpace(c.TypeIdentifier) ? 50 : 0);
        }

        private static int ScoreHardwareCatalogCandidate(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var preferred = preferredText ?? string.Empty;
            var score = ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferred) ? 0 : ScoreGsdCandidateText(text, preferred, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var normalizedPreferred = NormalizeCatalogSearchText(preferred);
                var normalizedArticle = NormalizeCatalogSearchText(c.ArticleNumber ?? "");
                var normalizedTypeIdentifier = NormalizeCatalogSearchText(c.TypeIdentifier ?? "");
                if (!string.IsNullOrWhiteSpace(normalizedArticle) && normalizedPreferred.Contains(normalizedArticle))
                    score += 100;
                if (!string.IsNullOrWhiteSpace(normalizedTypeIdentifier) && normalizedTypeIdentifier.Contains(normalizedPreferred))
                    score += 50;
            }

            var asksSiplus = ContainsAnyKeywordToken("SIPLUS", keyword + " " + preferred);
            var asksPortrait = ContainsAnyKeywordToken("Portrait 立式", keyword + " " + preferred);
            if (!asksSiplus && ContainsAnyKeywordToken(text, "SIPLUS"))
                score -= 40;
            if (!asksPortrait && ContainsAnyKeywordToken(text, "Portrait 立式"))
                score -= 25;

            return score;
        }

        private static string NormalizeCatalogSearchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        private static int ScoreHardwareCatalogCandidateLegacy(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferredText) ? 0 : ScoreGsdCandidateText(text, preferredText!, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);
        }

        private static int ScoreGsdCandidateText(string text, string keyword, string? preferredDap)
        {
            var score = 0;
            var haystack = text ?? string.Empty;
            foreach (var token in SplitSearchTokens(keyword))
            {
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) score += token.Length >= 5 ? 20 : 10;
            }

            if (!string.IsNullOrWhiteSpace(preferredDap) &&
                haystack.IndexOf(preferredDap!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 40;
            }

            if (haystack.IndexOf(keyword ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
            return score;
        }

        private static bool ContainsAnyKeywordToken(string text, string keyword)
        {
            return SplitSearchTokens(keyword).Any(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ContainsAllKeywordTokens(string text, string keyword)
        {
            var tokens = SplitSearchTokens(keyword).ToList();
            return tokens.Count > 0 && tokens.All(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> SplitSearchTokens(string keyword)
        {
            return (keyword ?? string.Empty)
                .Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeEngineeringName(string value)
        {
            var name = Regex.Replace(value ?? "Device_1", @"[^\w]", "_");
            if (string.IsNullOrWhiteSpace(name)) name = "Device_1";
            if (char.IsDigit(name[0])) name = "Device_" + name;
            return name;
        }

        private static string ShortExceptionMessage(Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            var text = (inner.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length > 220 ? text.Substring(0, 220) + "..." : text;
        }
        // TIA V21 project (2026-09-20; docs/reference/real-machine-ledger.md, crash ⑧): Devices.CreateWithItem("OrderNumber:6AV2 128-3GB06-0AXx/20.0.0.0", ...) - a WinCC Unified
        // Comfort panel of the PREVIOUS device version - made TIA Portal V21 exit; the same panel with /21.0.0.0 was created normally.
        // The device version of a Unified panel has to match the Portal major version.
        private static void GuardUnifiedPanelVersion(string orderNumber, string version)
        {
            var text = (orderNumber ?? "") + "/" + (version ?? "");
            if (NormalizeOrderNumber(text).IndexOf("6AV2128-", StringComparison.OrdinalIgnoreCase) < 0) return;
            var match = Regex.Match(text, @"/V?(\d+)\.(\d+)");
            if (!match.Success) return;
            if (int.Parse(match.Groups[1].Value) != PortalMajorVersion)
                throw new PortalException(PortalErrorCode.InvalidParams, "WinCC Unified panel version " + match.Value.TrimStart('/') + " does not match TIA Portal V" + PortalMajorVersion
                    + ": on the real machine a Unified Comfort panel created with a /20.0.0.0 identifier made TIA Portal V21 exit (NonRecoverable). Use the catalog entry ending in /" + PortalMajorVersion + ".0.0.0 (SearchHardwareCatalog).");
        }

        internal static string NormalizeOrderNumber(string s)
        {
            return (s ?? string.Empty).Replace(" ", "").Trim();
        }

        internal static string TryFormatMlfbWithSpaces(string value)
        {
            var n = NormalizeOrderNumber(value);
            if (n.Length < 8) return n;

            if (n.StartsWith("6ES7", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            if (n.StartsWith("6AV2", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            return n;
        }

        public ResponseMessage SetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
        {
            // envelope: legacy-multiple-dynamic-fields
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["attributeName"] = attributeName,
                ["mayHaveChanged"] = false
            };

            try
            {
                if (_session.IsProjectNull())
                {
                    meta["error"] = "Project is null";
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var di = _session.GetDeviceItemByPath(deviceItemPath);
                if (di == null)
                {
                    meta["error"] = "Device item not found";
                    return new ResponseMessage { Message = "Device item not found", Meta = meta };
                }

                var info = di.GetAttributeInfos().FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    meta["error"] = "Attribute not found";
                    meta["availableAttributes"] = _session.ToJsonArray(di.GetAttributeInfos().Select(x => x.Name ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return new ResponseMessage { Message = "Attribute not found", Meta = meta };
                }

                object? oldValue = null;
                try { oldValue = di.GetAttribute(info.Name); } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["oldValue"] = oldValue?.ToString() ?? string.Empty;
                meta["attributeDataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty;
                meta["attributeWritable"] = _session.IsAttributeWritable(info);

                object typedValue = _session.CoerceAttributeValue(value, oldValue, info);
                meta["mayHaveChanged"] = true;
                meta["writeOutcomeUnknown"] = true;
                di.SetAttribute(info.Name, typedValue);

                object? newValue = null;
                try { newValue = di.GetAttribute(info.Name); meta["writeOutcomeUnknown"] = false; } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["newValue"] = newValue?.ToString() ?? string.Empty;
                // envelope: legacy-single-verdict
                meta["success"] = true;
                return new ResponseMessage { Message = $"Device item attribute '{attributeName}' set", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = _session.FormatExceptionDetail(ex);
                return new ResponseMessage { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        public ResponseMessage SetCpuCommonSettings(string cpuPath, string settingsJson)
        {
            var meta = ResponseMeta.Basic(DateTime.Now, false, ("cpuPath", cpuPath), ("mayHaveChanged", false));

            try
            {
                if (_session.IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                var di = _session.GetDeviceItemByPath(cpuPath);
                if (di == null)
                {
                    meta["error"] = "CPU device item not found";
                    return new ResponseMessage { Message = "CPU device item not found", Meta = meta };
                }

                JsonObject? root;
                try
                {
                    root = JsonNode.Parse(settingsJson) as JsonObject;
                }
                catch (Exception ex)
                {
                    meta["error"] = "Invalid JSON: " + ex.Message;
                    return new ResponseMessage { Message = "Invalid settings JSON", Meta = meta };
                }

                var exact = root?["exactAttributes"] as JsonObject;
                if (exact == null || exact.Count == 0)
                {
                    meta["error"] = "settingsJson must contain exactAttributes. Attribute names must come from GetDeviceItemInfo/GetDeviceItemNetworkInfo readback.";
                    return new ResponseMessage { Message = "No exact attributes supplied", Meta = meta };
                }

                var infos = di.GetAttributeInfos().ToList();
                var applied = new JsonArray();
                var rejected = new JsonArray();
                meta["applied"] = applied; meta["rejected"] = rejected;

                foreach (var kv in exact)
                {
                    var name = kv.Key;
                    var value = kv.Value?.ToString() ?? string.Empty;
                    var info = infos.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = name,
                            ["reason"] = "Attribute not found on CPU device item. Read exact names first."
                        });
                        continue;
                    }

                    if (_session.IsAttributeWritable(info) != true)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = "Attribute is not writable according to TIA attribute metadata."
                        });
                        continue;
                    }

                    bool writeIssued = false;
                    bool readbackComplete = false;
                    try
                    {
                        object? oldValue = null;
                        try { oldValue = di.GetAttribute(info.Name); } catch /* swallow(probe-optional): Optional attribute readback must not interrupt the per-attribute settings report. */ { }
                        var typedValue = _session.CoerceAttributeValue(value, oldValue, info);
                        writeIssued = true; meta["mayHaveChanged"] = true;
                        di.SetAttribute(info.Name, typedValue);
                        object? newValue = null;
                        try { newValue = di.GetAttribute(info.Name); readbackComplete = true; } catch /* swallow(probe-optional): Optional attribute readback must not interrupt the per-attribute settings report. */ { }
                        if (!readbackComplete) { meta["writeOutcomeUnknown"] = true; meta["readbackComplete"] = false; }
                        applied.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["oldValue"] = oldValue?.ToString() ?? string.Empty,
                            ["newValue"] = newValue?.ToString() ?? string.Empty,
                            ["readbackComplete"] = readbackComplete,
                            ["dataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty
                        });
                    }
                    catch (Exception ex)
                    {
                        if (writeIssued) meta["writeOutcomeUnknown"] = true;
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = ex.GetType().Name,
                            ["writeIssued"] = writeIssued
                        });
                    }
                }

                meta["applied"] = applied;
                meta["rejected"] = rejected;
                meta["readback"] = _session.BuildDeviceItemNetworkReadbackJson(cpuPath);
                meta["success"] = applied.Count > 0 && rejected.Count == 0;
                return new ResponseMessage
                {
                    Message = meta["success"]?.GetValue<bool>() == true
                        ? "CPU common settings applied and read back"
                        : "CPU common settings completed with rejected attributes",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = _session.FormatExceptionDetail(ex);
                return new ResponseMessage { Message = "Failed setting CPU common settings", Meta = meta };
            }
        }

        public string GetProjectTree() => _session.GetProjectTree();
        public List<Device> GetDevices(string regexName = "") => _session.GetDevices(regexName);
        public Device? GetDevice(string devicePath) => _session.GetDevice(devicePath);
        public DeviceItem? GetDeviceItem(string deviceItemPath) => _session.GetDeviceItem(deviceItemPath);
        public string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4) => _session.GetDeviceItemTree(deviceItemPath, maxDepth);
        public string[] GetPlcSoftwareNamesForDesktop() => _session.GetPlcSoftwareNamesForDesktop();
        public ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
            => _session.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);

        // Read-only inventory of every Openness attribute exposed on a device's DeviceItems (CPU, modules,
        // interfaces, ports...). For each attribute: name, access mode (read-only vs read/write), current value,
        // value type. Use this ONCE to learn what a given CPU/firmware actually exposes, then drive subsequent
        // hardware reads/writes from that ground truth instead of guessing attribute names.
        // NOTE: GetAttributeInfos() does not enumerate EVERY gettable attribute on all CPUs (e.g. the PUT/GET
        // flag on some S7-1200), so absence here is "not enumerated", not a hard guarantee of "no interface".
        public JsonObject DumpDeviceAttributes(string devicePath, string? nameFilter = null, int maxItems = 500)
        {
            if (_session.IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = _session.GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            // Match alternatives separated by '|' or ',' independently instead of treating them as one substring.
            var filters = (nameFilter ?? string.Empty).Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(_session.NormalizeAttrName).Where(f => f.Length > 0).ToArray();
            var hasFilter = filters.Length > 0;

            var itemsArr = new JsonArray();
            int itemCount = 0;
            int totalAttrs = 0, writableAttrs = 0;

            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in _session.TraverseDeviceItems(root, root.Name))
                {
                    if (itemCount >= maxItems) break;
                    var it = tup.Item1;

                    System.Collections.Generic.IList<EngineeringAttributeInfo>? infos = null;
                    try { infos = it.GetAttributeInfos(); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ }
                    if (infos == null || infos.Count == 0) continue;

                    var attrsArr = new JsonArray();
                    foreach (var info in infos)
                    {
                        var name = info?.Name ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (hasFilter && !filters.Any(f => _session.NormalizeAttrName(name).Contains(f))) continue;

                        var access = TryGetAttributeInfoAccess(info!);
                        object? val = null; bool readErr = false;
                        try { val = it.GetAttribute(name); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ readErr = true; }

                        var isWritable = access?.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                         || access?.IndexOf("readwrite", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isWritable) writableAttrs++;
                        totalAttrs++;

                        attrsArr.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["access"] = access ?? string.Empty,
                            ["value"] = val?.ToString() ?? (readErr ? "<read-error>" : string.Empty),
                            ["valueType"] = val?.GetType().Name ?? string.Empty
                        });
                    }

                    if (attrsArr.Count == 0) continue;
                    itemsArr.Add(new JsonObject
                    {
                        ["item"] = it.Name,
                        ["path"] = tup.Item2,
                        ["attributeCount"] = attrsArr.Count,
                        ["attributes"] = attrsArr
                    });
                    itemCount++;
                }
            }

            return new JsonObject
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["nameFilter"] = nameFilter ?? string.Empty,
                ["itemCount"] = itemsArr.Count,
                ["totalAttributes"] = totalAttrs,
                ["writableAttributes"] = writableAttrs,
                ["items"] = itemsArr
            };
        }

        // EngineeringAttributeInfo exposes its access mode under SDK-version-dependent property names;
        // reflect defensively so we don't hard-depend on one.
        private static string? TryGetAttributeInfoAccess(object info)
        {
            foreach (var pn in new[] { "AccessMode", "Access", "ReadOnly", "IsReadOnly" })
            {
                try
                {
                    var p = info.GetType().GetProperty(pn);
                    if (p != null)
                    {
                        var v = p.GetValue(info);
                        if (v != null) return pn + "=" + v;
                    }
                }
                catch { /* swallow(probe-optional): Access metadata varies by SDK; continue with the next supported property name. */ }
            }
            return null;
        }

#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.HardwareSurface? _hardware;
        private ProjectBase? HardwareProject => (_hardware ?? (_hardware =
            TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).Hardware)).CurrentProject;
#else
        private ProjectBase? HardwareProject => _session.CurrentProject;
#endif
        internal bool HasProject => _session.CurrentProject is object;
    }
}
