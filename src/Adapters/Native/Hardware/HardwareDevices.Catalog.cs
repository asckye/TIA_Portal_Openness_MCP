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
using TiaMcp.Adapters.Native.Hardware;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private Device HardwareCreateExactDevice(string typeIdentifier, string deviceName, string parameter)
        {
            HardwareLegacyRequireCatalogBinding();
            if (HardwareProjectMissing())
                throw new HardwareAddressingException("InvalidState",
                    "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call " +
                    "OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
            var local = this.project as Project ?? throw new HardwareAddressingException("InvalidState", "Current project is not a local Project instance");
            // CreateWithItem names the device and its head item (CPU, interface module) with deviceName, so both names collide.
            DeviceCreationRules.RequireFreeName(ProjectDeviceWalk.OccupiedNames(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup), deviceName);
            GuardUnifiedPanelVersion(typeIdentifier, "");
            var before = HardwareDeviceInventory();
            Device? created;
            try
            {
                created = TiaMcp.Adapters.Hardware.HardwarePrimitives.CreateWithItem(TiaMcp.Adapters.Hardware.HardwarePrimitives.Devices(local), typeIdentifier, deviceName, deviceName);
            }
            catch (Exception error)
            {
                List<Device> after;
                try { after = HardwareDeviceInventory(); }
                catch (Exception readback) {
                    throw new HardwareAddressingException("OpennessError", "CreateWithItem('" + typeIdentifier
                            + "') threw and the device inventory could not be read back; the outcome is unknown: " + ShortExceptionMessage(readback), null,
                            error);
                }
                if (!DeviceCreationRules.SameInventory(before, after))
                    throw new HardwareAddressingException("OpennessError", "CreateWithItem('" + typeIdentifier
                            + "') threw but the device inventory changed; the outcome is unknown. Inspect the project before continuing.", null, error);
                throw DeviceCreationRules.TiaRefusal(typeIdentifier, deviceName, ShortExceptionMessage(error), HardwareCatalogCandidates(typeIdentifier), parameter, error);
            }
            if (created == null)
                throw new HardwareAddressingException("OpennessError", "CreateWithItem('" + typeIdentifier
                    + "') returned null; the outcome is unknown. Inspect the project before continuing.");
            var added = HardwareDeviceInventory().Where(d => !before.Contains(d)).ToList();
            // TIA may file a decentralized station under UngroupedDevicesGroup; both roots are verified by inventory difference.
            if (added.Count != 1 || !object.Equals(added[0], created))
                throw new HardwareAddressingException("OpennessError", "CreateWithItem('" + typeIdentifier
                        + "') returned a device, but the device inventory did not grow by exactly that device; inspect the project before continuing.");
            return created;
        }

        // Catalog rows for the article number part; exact identifiers are compared on the client side.
        private string HardwareCatalogCandidates(string typeIdentifier)
        {
#if PLC_HARDWARE_CATALOG
            var filter = DeviceCreationRules.CatalogFilter(typeIdentifier);
            if (filter.Length == 0 || portal == null) return " No hardware-catalog candidates were listed (no article number in the request).";
            try
            {
                return DeviceCreationRules
                    .CandidateText(filter, portal.HardwareCatalog.Find(filter).Select(TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeIdentifier)
                    .Take(DeviceCreationRules.CandidateListLimit + 1).ToList());
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return " Hardware-catalog candidates could not be read: " + ShortExceptionMessage(error) + ".";
            }
#else
            return DeviceCreationRules.NoCatalogText(typeIdentifier);
#endif
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

        // The ranked search is a preview only: creation needs keyword = one candidate's exact TypeIdentifier, and that entry is
        // created once. Any other keyword is refused with the ranked candidates so the caller chooses again.
        private (Device? Device, HardwareCatalogEntry? Candidate, List<HardwareCatalogEntry> Candidates, List<string> Attempts, string? Error)
            HardwareLegacyAddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "")
        {
            var candidates = HardwareLegacySearchHardwareCatalog(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogEntry(c, keyword, preferredText);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();
            var exact = DeviceCreationRules.ExactCandidate(candidates.Select(c => c.TypeIdentifier), keyword, "hardware catalog");
            var candidate = candidates.First(c => string.Equals(c.TypeIdentifier, exact, StringComparison.Ordinal));
            var device = HardwareCreateExactDevice(exact, deviceName, "keyword");
            return (device, candidate, candidates, new List<string> { exact + " -> created (one CreateWithItem call)" }, null);
        }

        private (Device? Device, HardwareGsdCandidate? Candidate, List<HardwareGsdCandidate> Candidates, List<string> Attempts, string? Error)
            HardwareLegacyAddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "")
        {
            var candidates = HardwareLegacySearchInstalledGsdDevices(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, keyword, preferredDap);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();
            var metadataOnly = candidates.Count(c => string.IsNullOrWhiteSpace(c.TypeIdentifier));
            var exact = DeviceCreationRules.ExactCandidate(candidates.Select(c => c.TypeIdentifier), keyword, "installed GSD/catalog",
                metadataOnly == 0 ? "" : " " + metadataOnly
                    + " GSDML file row(s) carry no TypeIdentifier (GSDML DAP metadata only) and cannot be created by this tool.");
            var candidate = candidates.First(c => string.Equals(c.TypeIdentifier, exact, StringComparison.Ordinal));
            var device = HardwareCreateExactDevice(exact, deviceName, "keyword");
            return (device, candidate, candidates, new List<string> { exact + " -> created (one CreateWithItem call)" }, null);
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
                    Source = "HardwareCatalog", Keyword = keyword, ArticleNumber = TiaMcp.Adapters.Hardware.HardwarePrimitives.ArticleNumber(typed),
                        CatalogPath = TiaMcp.Adapters.Hardware.HardwarePrimitives.CatalogPath(typed), Description = TiaMcp.Adapters.Hardware.HardwarePrimitives
                        .Description(typed),
                    TypeIdentifier = TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeIdentifier(typed), TypeIdentifierNormalized = TiaMcp.Adapters.Hardware
                        .HardwarePrimitives.TypeIdentifierNormalized(typed), TypeName = TiaMcp.Adapters.Hardware.HardwarePrimitives.TypeName(typed), Version =
                        TiaMcp.Adapters.Hardware.HardwarePrimitives.Version(typed)
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
        // EngineeringAttributeInfo documents Name, AccessMode and CreateRelevance (all releases) and SupportedTypes (V18+).
    }
}
