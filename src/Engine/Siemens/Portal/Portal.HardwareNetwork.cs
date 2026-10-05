using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Hardware-network family: IO systems, sync/MRP domains, transfer areas, channels, addressing, device user groups,
    // device users and port interconnections. Every member is the official V20/V21 API (Siemens.Engineering.HW);
    // dynamic attributes are read per name with the failure captured, never assumed present.
    public partial class Portal
    {
        private static DeviceItem RequireDeviceItem(HardwareObject owner, string parameter)
            => owner as DeviceItem ?? throw new ArgumentException(parameter + " must address a DeviceItem (non-empty itemPathJson).");
        private static T RequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
            => ServiceProvider(owner).GetService<T>() ?? throw new NotSupportedException(parameter + " does not expose " + typeof(T).Name + " (service is null for this hardware object).");

        // Hardware owner path of any HW object or feature: [device, item, sub item ...], walking OwnedBy/Parent (bounded).
        private static JsonArray HardwareOwnerPath(object? start)
        {
            var names = new List<string>(); object? current = start;
            for (int hop = 0; hop < 16 && current != null; hop++)
            {
                if (current is Device device) { names.Insert(0, device.Name); break; }
                if (current is DeviceItem item) { names.Insert(0, item.Name); current = item.Parent; continue; }
                var ownedBy = current.GetType().GetProperty("OwnedBy")?.GetValue(current);
                current = ownedBy ?? (current as IEngineeringInstance)?.Parent;
            }
            return new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        }
        private static JsonObject DynamicAttributes(IEngineeringObject target, string[] names)
        {
            var values = new JsonObject(); var failures = new JsonArray();
            foreach (var name in names)
            {
                try { values[name] = EngineeringScalarProperties.Json(target.GetAttribute(name)); }
                catch (Exception ex) { failures.Add(new JsonObject { ["attribute"] = name, ["error"] = ex.GetBaseException().Message }); }
            }
            return new JsonObject { ["values"] = values, ["failures"] = failures };
        }
        // Dynamic attribute writes: the CLR type is taken from the current value (enum names, numbers, booleans), then read back.
        // Two or more attributes go through SetAttributes(pairs, AttributeDelegate) - the official error handler reports
        // AttributeNameUnsupported / AttributeReadOnly / AttributeTypeUnsupported / AttributeValueUnsupported per attribute
        // (Name, Message) and answers Ignore so the remaining attributes are still written; each value is then read back.
        private static void SetDynamicAttributes(IEngineeringObject target, JsonObject attributes, JsonObject meta)
        {
            var applied = new JsonArray(); meta["appliedAttributes"] = applied;
            if (attributes.Count > 1)
            {
                var pairs = new List<KeyValuePair<string, object>>(); var refused = new JsonArray(); meta["refusedAttributes"] = refused;
                foreach (var pair in attributes)
                {
                    object? current = null; try { current = target.GetAttribute(pair.Key); } catch { /* swallow(probe-optional): An unreadable current value leaves conversion untyped; the native write and readback still validate the requested value. */ }
                    pairs.Add(new KeyValuePair<string, object>(pair.Key, EngineeringScalarProperties.ConvertValue(pair.Value, current?.GetType() ?? typeof(object))!));
                }
                AttributeDelegate handler = configuration => { AttributeConfiguration problem = configuration; refused.Add(new JsonObject { ["attribute"] = problem.Name, ["kind"] = problem.GetType().Name, ["message"] = problem.Message }); problem.CurrentSelection = AttributeChoiceSelection.Ignore; };
                meta["mayHaveChanged"] = true;
                target.SetAttributes(pairs, handler);
                var refusedNames = new HashSet<string>(refused.Select(r => r!["attribute"]!.GetValue<string>()), StringComparer.Ordinal);
                foreach (var pair in pairs)
                {
                    if (refusedNames.Contains(pair.Key)) continue;
                    applied.Add(pair.Key);
                    if (!EngineeringScalarProperties.SameValue(target.GetAttribute(pair.Key), pair.Value)) throw new InvalidOperationException("Attribute readback differs: " + pair.Key + ". Changes are not rolled back.");
                }
                if (refused.Count > 0) throw new InvalidOperationException("TIA refused " + refused.Count + " attribute(s) (see refusedAttributes); the others were written and read back.");
                return;
            }
            foreach (var pair in attributes)
            {
                object? current = null; try { current = target.GetAttribute(pair.Key); } catch { /* swallow(probe-optional): An unreadable current value leaves conversion untyped; the native write and readback still validate the requested value. */ }
                var value = EngineeringScalarProperties.ConvertValue(pair.Value, current?.GetType() ?? typeof(object));
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = pair.Key;
                target.SetAttribute(pair.Key, value);
                applied.Add(pair.Key);
                if (!EngineeringScalarProperties.SameValue(target.GetAttribute(pair.Key), value)) throw new InvalidOperationException("Attribute readback differs: " + pair.Key + ". Changes are not rolled back.");
            }
        }
        private static void ApplyScalarsAndAttributes(object target, string propertiesJson, string attributesJson, JsonObject meta, bool write)
        {
            var properties = HardwareNetworkLogic.ParseObject(propertiesJson, "propertiesJson"); var attributes = HardwareNetworkLogic.ParseObject(attributesJson, "attributesJson");
            var prepared = EngineeringScalarProperties.Prepare(target.GetType(), properties);
            meta["requestedProperties"] = properties.DeepClone(); meta["requestedAttributes"] = attributes.DeepClone();
            if (!write) return;
            if (prepared.Count > 0) EngineeringScalarProperties.Apply(target, prepared, meta);
            if (attributes.Count > 0) SetDynamicAttributes((IEngineeringObject)target, attributes, meta);
        }
        // TIA Portal V21, 2026-09-18 (docs/reference/real-machine-ledger.md): a composition proxy fetched before Create/Delete is stale (the new MrpDomain was not found
        // on it; enumerating it after Delete raised EngineeringObjectDisposedException). Verification therefore always
        // re-navigates to a fresh composition, and a disposed-proxy error while looking for a deleted object counts as
        // evidence of its absence rather than as a session failure.
        private static object? FindOnFresh(Func<object> composition, string name, JsonObject meta, string phase)
        {
            try { return EngineeringGroupOperations.Find(composition(), name); }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex))
            {
                meta[phase + "Enumeration"] = "EngineeringObjectDisposedException on the refreshed composition (TIA released a proxy): " + ex.GetBaseException().Message;
                return null;
            }
        }
        private static int? CountOnFresh(Func<object> composition, JsonObject meta, string key)
        {
            try { int n = EngineeringGroupOperations.Items(composition()).Count(); meta[key] = n; return n; }
            catch (Exception ex) when (HmiReadSafety.DisposedObjectOnly(ex)) { meta[key] = null; meta[key + "Error"] = ex.GetBaseException().Message; return null; }
        }

        // ---- rows -------------------------------------------------------------------------------------------------
        private static JsonObject AddressRow(Address address) => new JsonObject
        {
            ["startAddress"] = address.StartAddress, ["length"] = address.Length, ["ioType"] = address.IoType.ToString(),
            ["controllers"] = new JsonArray(EngineeringGroupOperations.Items(address.AddressControllers).Select(c => (JsonNode)HardwareOwnerPath(c)).ToArray()),
            ["attributes"] = DynamicAttributes(address, HardwareNetworkLogic.AddressAttributes)
        };


        // ---- channels ----------------------------------------------------------------------------------------------------
        private static Channel ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels.Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
               ?? throw new PortalException(PortalErrorCode.NotFound, "Channel not found: " + channelType + "/" + channelIoType + "/" + channelNumber);

    }
}
