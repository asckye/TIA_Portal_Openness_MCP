using TiaMcp.Adapters.Native.Engineering;
#if HMI_UNIFIED
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using static TiaMcp.Adapters.Native.Hmi.UnifiedHmiAccess;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed partial class UnifiedHmiAdapter
    {

        public HmiMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName,
                string dynamizationType = "")
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiDynamization", meta => _session.Capture(meta, _ =>
            {
                var candidates = ResolveUnifiedHmiDynamizationTypes(dynamizationType).ToList();
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var existing = find?.Invoke(dynamizations, new object[] { propertyName });
                if (existing != null)
                {
                    if (existing.GetType() != candidates.Single())
                        throw new NotSupportedException("Existing dynamization has a different documented kind; no replacement was attempted.");
                    meta["action"] = "exists";
                    meta["dynamizationType"] = existing.GetType().FullName;
                    meta["members"] = string.Join(" | ", _session.DescribeMembers(existing, 100).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                    return $"Dynamization for '{itemName}.{propertyName}' exists.";
                }

                var createMethods = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0]
                            .ParameterType == typeof(string))
                    .ToList();
                if (!createMethods.Any())
                {
                    throw new InvalidOperationException($"Generic Create<T>(string) not found on {dynamizations.GetType().FullName}.");
                }

                meta["candidateTypes"] = string.Join(" | ", candidates.Select(t => t.FullName));
                if (!candidates.Any())
                {
                    throw new InvalidOperationException($"No dynamization type matched '{dynamizationType}'. Use ListUnifiedHmiApiTypes with nameContains='Dynamization'.");
                }

                var errors = new List<string>();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        meta["mayHaveChanged"] = true;
                        var created = createMethods[0].MakeGenericMethod(candidate).Invoke(dynamizations, new object[] { propertyName });
                        EngineeringWriteJournal
                            .Created(created, hmiSoftwarePath + "/Screens/" + screenName + "/" + itemName + "/Dynamizations/" + propertyName, collection
                                    : dynamizations);
                        if (created == null) continue;

                        meta["action"] = "created";
                        meta["dynamizationType"] = created.GetType().FullName;
                        meta["members"] = string.Join(" | ", _session.DescribeMembers(created, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return $"Dynamization for '{itemName}.{propertyName}' created as '{created.GetType().Name}'.";
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{candidate.FullName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                meta["attemptErrors"] = string.Join(" || ", errors);
                throw new InvalidOperationException($"Unable to create dynamization for '{itemName}.{propertyName}'.");
            }));
        }

        private static IEnumerable<Type> ResolveUnifiedHmiDynamizationTypes(string dynamizationType)
        {
            var requested = string.IsNullOrWhiteSpace(dynamizationType) ? "Tag" : dynamizationType.Trim();
            var match = UnifiedUiModelLogic.DynamizationKinds.SingleOrDefault(p => requested == p.Key
                || requested == p.Value || requested == p.Value.Substring(p.Value.LastIndexOf('.') + 1));
            if (match.Value == null) throw new NotSupportedException("Unknown documented dynamization kind: " + requested);
            var type = typeof(HmiSoftware).Assembly.GetType(match.Value, false, false)
                ?? throw new NotSupportedException("Dynamization kind is unavailable in the selected SDK: " + match.Key);
            yield return type;
        }
    }
}
#endif
