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
        private static JsonObject HwIdentifierRow(HwIdentifier id) => new JsonObject
        {
            ["identifier"] = id.Identifier,
            ["controllers"] = new JsonArray(EngineeringGroupOperations.Items(id.HwIdentifierControllers).Select(c => (JsonNode)HardwareOwnerPath(c)).ToArray())
        };

        // ---- channels ----------------------------------------------------------------------------------------------------
        private static Channel ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels.Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
               ?? throw new PortalException(PortalErrorCode.NotFound, "Channel not found: " + channelType + "/" + channelIoType + "/" + channelNumber);

        // ---- addressing -------------------------------------------------------------------------------------------------
        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => RunHmiStepTool("ReadDeviceAddressing", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["ownerPath"] = HardwareOwnerPath(owner); meta["ownerType"] = owner.GetType().Name;
                meta["hwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(owner.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)HwIdentifierRow(h)).ToArray());
                var rows = new List<JsonNode>();
                if (owner is DeviceItem item)
                {
                    rows.AddRange(EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Select(a => (JsonNode)AddressRow(a)));
                    var addressController = ServiceProvider(item).GetService<AddressController>(); var hwController = ServiceProvider(item).GetService<HwIdentifierController>();
                    meta["isAddressController"] = addressController != null; meta["isHwIdentifierController"] = hwController != null;
                    if (addressController != null)
                        meta["registeredAddresses"] = new JsonArray(EngineeringGroupOperations.Items(addressController.RegisteredAddresses).Cast<Address>()
                            .Select(a => { var r = AddressRow(a); r["ownerPath"] = HardwareOwnerPath(a); return (JsonNode)r; }).ToArray());
                    if (hwController != null)
                        meta["registeredHwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(hwController.RegisteredHwIdentifiers).Cast<HwIdentifier>()
                            .Select(h => { var r = HwIdentifierRow(h); r["ownerPath"] = HardwareOwnerPath(h); return (JsonNode)r; }).ToArray());
                }
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Address StartAddress/Length/IoType, controller owner paths and official dynamic attributes; HwIdentifiers; AddressController/HwIdentifierController registrations when the item is a controller.";
                return "Addressing of the hardware object read; no modification.";
            });
        public ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress, string propertiesJson = "{}", string attributesJson = "{}",
            string softwarePath = "", string processImageObName = "", bool dryRun = true)
            => RunHmiStepTool("UpdateDeviceAddress", meta => {
                HardwareNetworkLogic.RequireOneOf(ioType, HardwareNetworkLogic.AddressIoTypes, "ioType");
                if (startAddress < 0) throw new ArgumentException("startAddress identifies the existing address (>= 0).");
                using var access = dryRun ? null : AcquireHmiEditAccess();
                var item = RequireDeviceItem(ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                var wanted = (AddressIoType)Enum.Parse(typeof(AddressIoType), ioType);
                var matches = EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Where(a => a.IoType == wanted && a.StartAddress == startAddress).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, matches.Length == 0 ? "No address with that IoType/StartAddress on the item." : "Ambiguous address identity.");
                var address = matches[0];
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(item); meta["before"] = AddressRow(address);
                meta["restrictions"] = "Changing StartAddress may move the opposite IoType of the same module and never rewires tags; packed addresses are unsupported.";
                // Resolve (and on V21 refuse) the OB assignment before any property is written, so a refusal never leaves a partial edit.
#if TIA_V20
                OB? ob = null;
                if (!string.IsNullOrEmpty(processImageObName))
                {
                    ob = GetBlock(HardwareNetworkLogic.RequireExactName(softwarePath, "softwarePath"), processImageObName) as OB ?? throw new PortalException(PortalErrorCode.NotFound, "OB not found in the PLC software: " + processImageObName);
                    meta["processImageOb"] = ob.Name;
                }
#else
                // V21 moved the assignment from Address to the ProcessImageProvider service of the address.
                OB? ob = null; global::Siemens.Engineering.SW.ProcessImageProvider? processImage = null;
                if (!string.IsNullOrEmpty(processImageObName))
                {
                    ob = GetBlock(HardwareNetworkLogic.RequireExactName(softwarePath, "softwarePath"), processImageObName) as OB ?? throw new PortalException(PortalErrorCode.NotFound, "OB not found in the PLC software: " + processImageObName);
                    processImage = address.GetService<global::Siemens.Engineering.SW.ProcessImageProvider>() ?? throw new NotSupportedException("ProcessImageProvider unavailable on this address (V21 service replacing Address.AssignProcessImageToOrganizationBlock).");
                    meta["processImageOb"] = ob.Name; meta["processImageAccess"] = "ProcessImageProvider.AssignProcessImageToOrganizationBlock";
                }
#endif
                ApplyScalarsAndAttributes(address, propertiesJson, attributesJson, meta, !dryRun);
#if TIA_V20
                if (ob != null && !dryRun) { meta["mayHaveChanged"] = true; address.AssignProcessImageToOrganizationBlock(ob); meta["processImageAssigned"] = true; }
#else
                if (ob != null && !dryRun) { meta["mayHaveChanged"] = true; processImage!.AssignProcessImageToOrganizationBlock(ob); meta["processImageAssigned"] = true; }
#endif
                if (dryRun) return "Address update preview; nothing changed.";
                meta["after"] = AddressRow(address);
                return "Address properties/attributes written and read back. No save/compile/download.";
            });
    }
}
