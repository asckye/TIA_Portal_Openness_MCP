using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<string, object?>? ResolveEngineeringItem { get; set; }
        private DeviceItem? HardwareResolveItem(string path) => ResolveEngineeringItem != null ? ResolveEngineeringItem(path) as DeviceItem : HardwareLegacyItem(path);
        private Device? HardwareResolveDevice(string path) => ResolveEngineeringDevice != null ? ResolveEngineeringDevice(path) as Device : HardwareLegacyDevice(path);
        private static string HardwareNormalizeAttrName(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        private static List<object?> HardwareToArray(IEnumerable<string> values) => values.Cast<object?>().ToList();
        private static object? HardwareTryProperty(object? owner, params string[] names)
        {
            if (owner == null) return null;
            foreach (string name in names) { try { var prop = owner.GetType().GetProperty(name); if (prop != null) return prop.GetValue(owner); } catch { /* swallow(probe-optional): catalog metadata varies by SDK. */ } }
            return null;
        }
        private void HardwareLegacyRequireCatalogBinding() { Check(); HardwareCatalogPolicy.RequireBound(portal != null && project != null, false); }
        private void RequireHardwareDeviceName(string deviceName)
            => HardwareCatalogPolicy.AvailableName(HardwareReadDevicesRaw().Concat(project!.UngroupedDevicesGroup.Devices).Select(device => device.Name), deviceName);
        private Device HardwareLegacyAddDevice(string orderNumber, string version, string deviceName)
        {
            HardwareLegacyRequireCatalogBinding();
            RequireHardwareDeviceName(deviceName);

            if (HardwareProjectMissing()) throw new HardwareAddressingException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            string? lastVariantError = null;
            try
            {
                // Openness CreateWithItem expects a TypeIdentifier, not split order/version.
                // Example: OrderNumber:6ES7 513-1AM03-0AB0/V3.0
                var project = (this.project as Project);
                if (project == null) throw new HardwareAddressingException("InvalidState", "Current project is not a local Project instance");

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
                            var dev = TiaMcp.Adapters.Hardware.HardwarePrimitives.CreateWithItem(TiaMcp.Adapters.Hardware.HardwarePrimitives.Devices(project), typeIdentifier, itemName, deviceName);
                            if (dev is Device d) return d;
                            attempts.Add($"{typeIdentifier} [{itemName}] -> CreateWithItem returned null");
                        }
                        catch (Exception exTry) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                        {
                            // try next variant
                            lastVariantError = HardwareExceptionDetail(exTry);
                            attempts.Add($"{typeIdentifier} [{itemName}] -> {ShortExceptionMessage(exTry)}");
                        }
                    }
                }

                var shown = attempts.Take(24).ToList();
                var more = attempts.Count > shown.Count ? $"\n... {attempts.Count - shown.Count} more" : "";
                throw new HardwareAddressingException("OpennessError",
                    $"CreateDevice failed: no device created for OrderNumber={orderNumber} Version={version}; {attempts.Count} CreateWithItem attempt(s):\n"
                    + string.Join("\n", shown) + more
                    + "\nHint: SearchHardwareCatalog / CreateHardwareCatalogDevice report the catalog's exact TypeIdentifier (HMI panels carry the version without a 'V', e.g. '.../14.0.1.0'); pass that as orderNumber (a value starting with 'OrderNumber:' is used verbatim)."
                    + (lastVariantError != null ? "\nLast error detail: " + lastVariantError : ""));
            }
            catch (HardwareAddressingException)
            {
                throw;
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                throw new HardwareAddressingException("OpennessError", HardwareExceptionDetail(ex), null, ex);
            }
        }

        private (Device? Device, string? MlfbUsed, string? VersionUsed, List<string> Attempts, string? Error) HardwareLegacyAddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family)
        {
            HardwareLegacyRequireCatalogBinding();
            RequireHardwareDeviceName(deviceName);
            if (!string.IsNullOrWhiteSpace(preferredMlfb))
                HardwareCatalogPolicy.ExactRow(HardwareLegacySearchHardwareCatalog(preferredMlfb, 100).Select(row =>
                    new TiaMcp.Adapters.Contracts.Candidates.DeviceCatalogEntry { TypeIdentifier = row.TypeIdentifier ?? "", ArticleNumber = row.ArticleNumber ?? "", Version = row.Version ?? "" }), preferredMlfb, preferredVersion);
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
                        var d = HardwareLegacyAddDevice(mlfb, ver, deviceName);
                        attempts.Add($"{mlfb} {ver} -> OK");
                        return (d, mlfb, ver, attempts, null);
                    }
                    catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                    {
                        lastError = ex.Message;
                        attempts.Add($"{mlfb} {ver} -> FAIL: {ex.Message}");
                    }
                }
            }

            return (null, null, null, attempts, lastError ?? "All attempts failed");
        }

        public List<HardwareGsdCandidate> HardwareLegacySearchInstalledGsdDevices(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<HardwareGsdCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new HardwareAddressingException("InvalidParams", "Keyword is empty");

            try
            {
                var catalog = portal == null ? null : HardwareTryProperty(portal, "HardwareCatalog");
                if (catalog != null)
                {
                    foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                    {
                        foreach (var entry in HardwareFindCatalogEntries(catalog, filter))
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
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {

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
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {

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

        public List<HardwareCatalogEntry> HardwareLegacySearchHardwareCatalog(string keyword, int limit = 50)
        {
            HardwareLegacyRequireCatalogBinding();
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<HardwareCatalogEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new HardwareAddressingException("InvalidParams", "Keyword is empty");

            try
            {
                var catalog = portal == null ? null : HardwareTryProperty(portal, "HardwareCatalog");
                if (catalog == null)
                    throw new HardwareAddressingException("InvalidState", "TIA Portal HardwareCatalog is not available. Connect to TIA Portal first.");

                foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                {
                    foreach (var entry in HardwareFindCatalogEntries(catalog, filter))
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
            catch (HardwareAddressingException)
            {
                throw;
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {

            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogEntry(c, normalizedKeyword);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.ArticleNumber)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private (Device? Device, HardwareCatalogEntry? Candidate, List<HardwareCatalogEntry> Candidates, List<string> Attempts, string? Error)
            HardwareLegacyAddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "")
        {
            var attempts = new List<string>();
            var candidates = HardwareLegacySearchHardwareCatalog(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogEntry(c, keyword, preferredText);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No hardware catalog candidates found for '{keyword}'");
            }

            if (HardwareProjectMissing())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = this.project as Project;
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
                    var dev = TiaMcp.Adapters.Hardware.HardwarePrimitives.CreateWithItem(TiaMcp.Adapters.Hardware.HardwarePrimitives.Devices(project), typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (HardwareAddressingException pex) when (pex.Status == "InvalidParams")
                {
                    lastError = pex.Message;
                    attempts.Add($"{typeIdentifier} -> SKIPPED: {pex.Message}");
                }
                catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                {
                    lastError = HardwareExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => c.Insertable == true))
                lastError = "Hardware catalog returned no insertable TypeIdentifier candidates.";

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        private (Device? Device, HardwareGsdCandidate? Candidate, List<HardwareGsdCandidate> Candidates, List<string> Attempts, string? Error)
            HardwareLegacyAddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "")
        {
            var attempts = new List<string>();
            var candidates = HardwareLegacySearchInstalledGsdDevices(keyword, 100)
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

            if (HardwareProjectMissing())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = this.project as Project;
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
                    var dev = TiaMcp.Adapters.Hardware.HardwarePrimitives.CreateWithItem(TiaMcp.Adapters.Hardware.HardwarePrimitives.Devices(project), typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                {
                    lastError = HardwareExceptionDetail(ex);
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

        private static HardwareGsdCandidate? CatalogEntryToCandidate(object entry, string keyword)
        {
            var c = new HardwareGsdCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = HardwareTryProperty(entry, "ArticleNumber")?.ToString(),
                CatalogPath = HardwareTryProperty(entry, "CatalogPath")?.ToString(),
                Description = HardwareTryProperty(entry, "Description")?.ToString(),
                TypeIdentifier = HardwareTryProperty(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = HardwareTryProperty(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = HardwareTryProperty(entry, "TypeName")?.ToString(),
                Version = HardwareTryProperty(entry, "Version")?.ToString()
            };

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static HardwareCatalogEntry? CatalogEntryToHardwareCandidate(object entry, string keyword)
        {
            // The official HardwareCatalog.Find rows are typed CatalogEntry objects; the reflective read stays as fallback.
            var c =
#if PLC_HARDWARE_CATALOG_ENTRIES
                entry is global::Siemens.Engineering.HW.HardwareCatalog.CatalogEntry typed
                ? new HardwareCatalogEntry
                {
                    Source = "HardwareCatalog", Keyword = keyword, ArticleNumber = TiaMcp.Adapters.Hardware.HardwarePrimitives.ArticleNumber(typed), CatalogPath = TiaMcp.Adapters.Hardware.HardwarePrimitives.CatalogPath(typed), Description = TiaMcp.Adapters.Hardware.HardwarePrimitives.Description(typed),
                    TypeIdentifier = TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeIdentifier(typed), TypeIdentifierNormalized = TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeIdentifierNormalized(typed), TypeName = TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeName(typed), Version = TiaMcp.Adapters.Hardware.HardwarePrimitives.Version(typed)
                }
                :
#endif
                new HardwareCatalogEntry
                {
                    Source = "HardwareCatalog",
                    Keyword = keyword,
                    ArticleNumber = HardwareTryProperty(entry, "ArticleNumber")?.ToString(),
                    CatalogPath = HardwareTryProperty(entry, "CatalogPath")?.ToString(),
                    Description = HardwareTryProperty(entry, "Description")?.ToString(),
                    TypeIdentifier = HardwareTryProperty(entry, "TypeIdentifier")?.ToString(),
                    TypeIdentifierNormalized = HardwareTryProperty(entry, "TypeIdentifierNormalized")?.ToString(),
                    TypeName = HardwareTryProperty(entry, "TypeName")?.ToString(),
                    Version = HardwareTryProperty(entry, "Version")?.ToString()
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

        private IEnumerable<HardwareGsdCandidate> SearchGsdmlFiles(string keyword)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Siemens",
                "Automation",
                $"Portal V{HardwareMajorVersion}",
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

                    yield return new HardwareGsdCandidate
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

        private static int ScoreGsdCandidate(HardwareGsdCandidate c, string keyword, string? preferredDap)
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

        private static int ScoreHardwareCatalogEntry(HardwareCatalogEntry c, string keyword, string? preferredText = null)
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

        private static int ScoreHardwareCatalogEntryLegacy(HardwareCatalogEntry c, string keyword, string? preferredText = null)
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
        private void GuardUnifiedPanelVersion(string orderNumber, string version)
        {
            var text = (orderNumber ?? "") + "/" + (version ?? "");
            if (NormalizeOrderNumber(text).IndexOf("6AV2128-", StringComparison.OrdinalIgnoreCase) < 0) return;
            var match = Regex.Match(text, @"/V?(\d+)\.(\d+)");
            if (!match.Success) return;
            if (int.Parse(match.Groups[1].Value) != HardwareMajorVersion)
                throw new HardwareAddressingException("InvalidParams", "WinCC Unified panel version " + match.Value.TrimStart('/') + " does not match TIA Portal V" + HardwareMajorVersion
                    + ": on the real machine a Unified Comfort panel created with a /20.0.0.0 identifier made TIA Portal V21 exit (NonRecoverable). Use the catalog entry ending in /" + HardwareMajorVersion + ".0.0.0 (SearchHardwareCatalog).");
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

        public HardwareAddressingReply HardwareLegacySetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
        {
            // envelope: legacy-multiple-dynamic-fields
            var meta = HardwareStepMeta(DateTime.Now, false);
            foreach (var pair in new Dictionary<string, object?>
            {
                ["deviceItemPath"] = deviceItemPath,
                ["attributeName"] = attributeName,
                ["mayHaveChanged"] = false
            }) meta[pair.Key] = pair.Value;

            try
            {
                if (HardwareProjectMissing())
                {
                    meta["error"] = "Project is null";
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };
                }

                var di = HardwareResolveItem(deviceItemPath);
                if (di == null)
                {
                    meta["error"] = "Device item not found";
                    return new HardwareAddressingReply { Message = "Device item not found", Meta = meta };
                }

                var info = di.GetAttributeInfos().FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    meta["error"] = "Attribute not found";
                    meta["availableAttributes"] = HardwareToArray(di.GetAttributeInfos().Select(x => x.Name ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return new HardwareAddressingReply { Message = "Attribute not found", Meta = meta };
                }

                object? oldValue = null;
                try { oldValue = di.GetAttribute(info.Name); } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["oldValue"] = oldValue?.ToString() ?? string.Empty;
                meta["attributeDataType"] = HardwareTryProperty(info, "DataType", "Type")?.ToString() ?? string.Empty;
                meta["attributeWritable"] = IsAttributeWritable(info);

                object typedValue = HardwareCoerceAttributeValue(value, oldValue, info);
                meta["mayHaveChanged"] = true;
                meta["writeOutcomeUnknown"] = true;
                di.SetAttribute(info.Name, typedValue);

                object? newValue = null;
                try { newValue = di.GetAttribute(info.Name); meta["writeOutcomeUnknown"] = false; } catch /* swallow(probe-optional): Attribute readback is best effort; preserve the write result when this value cannot be read. */ { }
                meta["newValue"] = newValue?.ToString() ?? string.Empty;
                // envelope: legacy-single-verdict
                meta["success"] = true;
                return new HardwareAddressingReply { Message = $"Device item attribute '{attributeName}' set", Meta = meta };
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                meta["error"] = HardwareExceptionDetail(ex);
                return new HardwareAddressingReply { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        public HardwareAddressingReply HardwareLegacySetCpuCommonSettings(string cpuPath, Dictionary<string, HardwareScalar> settingsJson)
        {
            var meta = HardwareStepMeta(DateTime.Now, false); meta["cpuPath"] = cpuPath; meta["mayHaveChanged"] = false;

            try
            {
                if (HardwareProjectMissing())
                    return new HardwareAddressingReply { Message = "Project is null", Meta = meta };

                var di = HardwareResolveItem(cpuPath);
                if (di == null)
                {
                    meta["error"] = "CPU device item not found";
                    return new HardwareAddressingReply { Message = "CPU device item not found", Meta = meta };
                }

                var exact = settingsJson;
                if (exact == null || exact.Count == 0)
                {
                    meta["error"] = "settingsJson must contain exactAttributes. Attribute names must come from GetDeviceItemInfo/GetDeviceItemNetworkInfo readback.";
                    return new HardwareAddressingReply { Message = "No exact attributes supplied", Meta = meta };
                }

                var infos = di.GetAttributeInfos().ToList();
                var applied = new List<object?>();
                var rejected = new List<object?>();
                meta["applied"] = applied; meta["rejected"] = rejected;

                foreach (var kv in exact)
                {
                    var name = kv.Key;
                    var value = kv.Value?.Text ?? string.Empty;
                    var info = infos.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        rejected.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = name,
                            ["reason"] = "Attribute not found on CPU device item. Read exact names first."
                        });
                        continue;
                    }

                    if (IsAttributeWritable(info) != true)
                    {
                        rejected.Add(new Dictionary<string, object?>
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
                        var typedValue = HardwareCoerceAttributeValue(value, oldValue, info);
                        writeIssued = true; meta["mayHaveChanged"] = true;
                        di.SetAttribute(info.Name, typedValue);
                        object? newValue = null;
                        try { newValue = di.GetAttribute(info.Name); readbackComplete = true; } catch /* swallow(probe-optional): Optional attribute readback must not interrupt the per-attribute settings report. */ { }
                        if (!readbackComplete) { meta["writeOutcomeUnknown"] = true; meta["readbackComplete"] = false; }
                        applied.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = info.Name,
                            ["oldValue"] = oldValue?.ToString() ?? string.Empty,
                            ["newValue"] = newValue?.ToString() ?? string.Empty,
                            ["readbackComplete"] = readbackComplete,
                            ["dataType"] = HardwareTryProperty(info, "DataType", "Type")?.ToString() ?? string.Empty
                        });
                    }
                    catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                    {
                        if (writeIssued) meta["writeOutcomeUnknown"] = true;
                        rejected.Add(new Dictionary<string, object?>
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = ex.GetType().Name,
                            ["writeIssued"] = writeIssued
                        });
                    }
                }

                meta["applied"] = applied;
                meta["rejected"] = rejected;
                meta["readback"] = HardwareBuildDeviceItemNetworkReadbackJson(cpuPath);
                meta["success"] = applied.Count > 0 && rejected.Count == 0;
                return new HardwareAddressingReply
                {
                    Message = Equals(meta["success"], true)
                        ? "CPU common settings applied and read back"
                        : "CPU common settings completed with rejected attributes",
                    Meta = meta
                };
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                meta["error"] = HardwareExceptionDetail(ex);
                return new HardwareAddressingReply { Message = "Failed setting CPU common settings", Meta = meta };
            }
        }

        public Dictionary<string, object?> HardwareLegacyDumpDeviceAttributes(string devicePath, string? nameFilter = null, int maxItems = 500)
        {
            if (HardwareProjectMissing()) return new Dictionary<string, object?> { ["found"] = false, ["message"] = "No project open." };
            var device = HardwareResolveDevice(devicePath);
            if (device == null) return new Dictionary<string, object?> { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            // Match alternatives separated by '|' or ',' independently instead of treating them as one substring.
            var filters = (nameFilter ?? string.Empty).Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(HardwareNormalizeAttrName).Where(f => f.Length > 0).ToArray();
            var hasFilter = filters.Length > 0;

            var itemsArr = new List<object?>();
            int itemCount = 0;
            int totalAttrs = 0, writableAttrs = 0;

            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in HardwareTraverseDeviceItems(root, root.Name))
                {
                    if (itemCount >= maxItems) break;
                    var it = (DeviceItem)tup.Item1;

                    System.Collections.Generic.IList<EngineeringAttributeInfo>? infos = null;
                    try { infos = it.GetAttributeInfos(); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ }
                    if (infos == null || infos.Count == 0) continue;

                    var attrsArr = new List<object?>();
                    foreach (var info in infos)
                    {
                        var name = info?.Name ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (hasFilter && !filters.Any(f => HardwareNormalizeAttrName(name).Contains(f))) continue;

                        var access = TryGetAttributeInfoAccess(info!);
                        object? val = null; bool readErr = false;
                        try { val = it.GetAttribute(name); } catch { /* swallow(probe-optional): Unavailable attributes retain the existing skipped item or read-error evidence in the inventory. */ readErr = true; }

                        var isWritable = access?.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                         || access?.IndexOf("readwrite", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isWritable) writableAttrs++;
                        totalAttrs++;

                        attrsArr.Add(new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["access"] = access ?? string.Empty,
                            ["value"] = val?.ToString() ?? (readErr ? "<read-error>" : string.Empty),
                            ["valueType"] = val?.GetType().Name ?? string.Empty
                        });
                    }

                    if (attrsArr.Count == 0) continue;
                    itemsArr.Add(new Dictionary<string, object?>
                    {
                        ["item"] = it.Name,
                        ["path"] = tup.Item2,
                        ["attributeCount"] = attrsArr.Count,
                        ["attributes"] = attrsArr
                    });
                    itemCount++;
                }
            }

            return new Dictionary<string, object?>
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

        public string HardwareCreateDevice(string orderNumber, string version, string deviceName)
            => HardwareLegacyAddDevice(orderNumber, version, deviceName).Name;
        public Dictionary<string, object?> HardwareCreateCatalogDevice(string keyword, string deviceName, string preferredText = "")
        {
            var result = HardwareLegacyAddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
            return new Dictionary<string, object?> { ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["Candidate"] = result.Candidate, ["Candidates"] = result.Candidates, ["Attempts"] = result.Attempts, ["Error"] = result.Error };
        }
        public Dictionary<string, object?> HardwareCreateGsdDevice(string keyword, string deviceName, string preferredDap = "")
        {
            var result = HardwareLegacyAddGsdDeviceWithProbe(keyword, deviceName, preferredDap);
            return new Dictionary<string, object?> { ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["Candidate"] = result.Candidate, ["Candidates"] = result.Candidates, ["Attempts"] = result.Attempts, ["Error"] = result.Error };
        }
        public List<Device> HardwareReadDevicesRaw(string regexName = "")
        {


            if (HardwareProjectMissing())
            {
                return [];
            }

            var list = new List<Device>();

            if (project?.Devices != null)
            {
                foreach (Device device in project.Devices)
                {
                    list.Add(device);
                }

                foreach (var group in project.DeviceGroups)
                {
                    GetDevicesRecursive(group, list, regexName);
                }

            }

            return list;
        }
        private bool GetDevicesRecursive(DeviceUserGroup group, List<Device> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Devices)
            {
                if (composition is Device device)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this device if it doesn't match the pattern
                        }
                    }
                    catch (Exception) /* swallow(parse-fallback): an invalid regex or unreadable device name skips this candidate */
                    {
                        // Invalid regex pattern, skip this device
                        continue;
                    }

                    list.Add(device);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetDevicesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }
        public List<HardwareDeviceDescription> HardwareDescribeDevices(string regexName = "")
            => HardwareReadDevicesRaw(regexName).Select(HardwareDescribeDevice).ToList();
        public HardwareDeviceDescription? HardwareDescribeDeviceAt(string path)
        { var device = HardwareResolveDevice(path); return device == null ? null : HardwareDescribeDevice(device); }
        public HardwareDeviceDescription? HardwareDescribeItemAt(string path)
        { var item = HardwareResolveItem(path); return item == null ? null : HardwareDescribeDevice(item); }
        private static HardwareDeviceDescription HardwareDescribeDevice(HardwareObject obj)
        {
            var result = new HardwareDeviceDescription();
            IEngineeringObject attributes = obj;
            foreach (var attr in attributes.GetAttributeInfos())
            {
                object? value;
                try { value = attributes.GetAttribute(attr.Name); } catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */ { value = "<unreadable: " + ex.GetType().Name + ">"; }
                if (value != null && !(value.GetType().IsPrimitive || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid || value.GetType().IsEnum))
                    try { value = value.ToString(); } catch { /* swallow(probe-optional): Preserve the type marker when an optional attribute cannot be formatted. */ value = "<" + value.GetType().Name + ">"; }
                result.Attributes.Add(new HardwareDeviceAttribute { Name = attr.Name, Value = value, AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode) });
            }
            result.Name = obj.Name; result.Description = obj.ToString(); return result;
        }

        private static IEnumerable<object> HardwareFindCatalogEntries(object catalog, string filter)
        {
            var find = catalog.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (find == null) yield break;

            var value = find.Invoke(catalog, new object[] { filter });
            if (value is not IEnumerable enumerable) yield break;

            foreach (var item in enumerable)
            {
                if (item != null) yield return item;
            }
        }
        private static object HardwareCoerceAttributeValue(string value, object? oldValue, object attributeInfo)
        {
            if (oldValue != null)
            {
                var oldType = oldValue.GetType();
                if (oldType == typeof(string)) return value;
                if (oldType == typeof(bool)) return bool.Parse(value);
                if (oldType == typeof(int)) return int.Parse(value);
                if (oldType == typeof(uint)) return uint.Parse(value);
                if (oldType == typeof(short)) return short.Parse(value);
                if (oldType == typeof(ushort)) return ushort.Parse(value);
                if (oldType == typeof(long)) return long.Parse(value);
                if (oldType == typeof(ulong)) return ulong.Parse(value);
                if (oldType == typeof(float)) return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType.IsEnum) return Enum.Parse(oldType, value, ignoreCase: true);
            }

            var dataType = HardwareTryProperty(attributeInfo, "DataType", "Type")?.ToString() ?? string.Empty;
            if (dataType.IndexOf("Boolean", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return bool.Parse(value);
            if (dataType.IndexOf("Int32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Int", StringComparison.OrdinalIgnoreCase)) return int.Parse(value);
            if (dataType.IndexOf("UInt32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("UInt", StringComparison.OrdinalIgnoreCase)) return uint.Parse(value);
            if (dataType.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Real", StringComparison.OrdinalIgnoreCase)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

            return value;
        }
        public string HardwareReadItemTree(string deviceItemPath, int maxDepth = 4)
        {


            if (HardwareProjectMissing())
            {
                return string.Empty;
            }

            var root = HardwareResolveItem(deviceItemPath);
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name} [DeviceItem]");
            BuildDeviceItemTree(sb, root, new List<bool>(), 0, Math.Max(0, maxDepth));
            return sb.ToString();
        }
        private static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            // Hardware components (Items)
            if (node.Items != null && node.Items.Count > 0)
            {
                var items = node.Items.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = (i == items.Count - 1) && (node.DeviceItems == null || node.DeviceItems.Count == 0);
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            // Sub device items
            if (node.DeviceItems != null && node.DeviceItems.Count > 0)
            {
                var children = node.DeviceItems.ToList();
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var isLast = i == children.Count - 1;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                    BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
                }
            }
        }
        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }
        private List<object?> HardwareBuildDeviceItemNetworkReadbackJson(string deviceItemPath)
        {
            var arr = new List<object?>();
            var attrs = HardwareGetDeviceItemNetworkInfo(deviceItemPath) ?? new List<HardwareNetworkAttribute>();
            foreach (var attr in attrs)
            {
                arr.Add(new Dictionary<string, object?>
                {
                    ["name"] = attr.Name ?? string.Empty,
                    ["value"] = attr.Value ?? string.Empty,
                    ["dataType"] = attr.DataType ?? string.Empty,
                    ["isWritable"] = attr.IsWritable
                });
            }
            return arr;
        }
        private int HardwareMajorVersion => ReleaseKey == "14sp1" ? 14 : ReleaseKey == "15.1" ? 15 : int.Parse(ReleaseKey);

        private static IEnumerable<(DeviceItem Item, string Path)> HardwareTraverseDeviceItems(DeviceItem root, string path)
        {
            yield return (root, path);
            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in HardwareTraverseDeviceItems(child, path + "/" + child.Name))
                {
                      
      yield return nested;
                }
            }
        }
        public Dictionary<string, object?> HardwareCreateDeviceFallback(string preferredMlfb, string preferredVersion, string deviceName, string family)
        {
            var result = HardwareLegacyAddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
            return new Dictionary<string, object?> { ["Device"] = result.Device == null ? null : new Dictionary<string, object?> { ["Name"] = result.Device.Name }, ["MlfbUsed"] = result.MlfbUsed, ["VersionUsed"] = result.VersionUsed, ["Attempts"] = result.Attempts, ["Error"] = result.Error };
        }
        public string[] HardwareReadPlcNames()
        {
            if (HardwareProjectMissing()) throw new HardwareAddressingException("InvalidState", "No project is open.");
            return HardwareEnumerateSoftwareContainers()
                .Select(c => c.Software).OfType<PlcSoftware>().Select(p => p.Name).ToArray();
        }
        private IEnumerable<SoftwareContainer> HardwareEnumerateSoftwareContainers()
        {
            if (project == null) yield break;
            int count = 0;
            IEnumerable<SoftwareContainer> Items(IEnumerable<DeviceItem> items)
            {
                var pending = new Stack<DeviceItem>(items);
                while (pending.Count > 0)
                {
                    if (++count > 100000) throw new HardwareAddressingException("OpennessError",
                        "Exact software enumeration limit reached; no target selected.");
                    var item = pending.Pop();
                    var sc = item.GetService<SoftwareContainer>();
                    if (HardwareSoftwareLookupValue(sc) != null) yield return sc!;
                    foreach (var child in item.DeviceItems) pending.Push(child);
                }
            }
            IEnumerable<SoftwareContainer> Devices(DeviceComposition devices)
            {
                foreach (var device in devices)
                    foreach (var sc in Items(device.DeviceItems)) yield return sc;
            }
            foreach (var sc in Devices(project.Devices)) yield return sc;
            var groups = new Stack<DeviceUserGroup>(project.DeviceGroups);
            while (groups.Count > 0)
            {
                if (++count > 100000) throw new HardwareAddressingException("OpennessError",
                    "Exact software enumeration limit reached; no target selected.");
                var group = groups.Pop();
                foreach (var sc in Devices(group.Devices)) yield return sc;
                foreach (var child in group.Groups) groups.Push(child);
            }
        }
        private static Software? HardwareSoftwareLookupValue(SoftwareContainer? container)
            => container?.Software;
    }
}
