using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Hardware
{
    public static class HardwareAddressPrimitives
    {
        public static IEnumerable<object> Items(object collection)
        {
            if (collection is not IEnumerable sequence || collection is string) throw new NotSupportedException("Expected an engineering collection.");
            int count = 0;
            foreach (var item in sequence)
            {
                if (++count > 10000) throw new InvalidOperationException("Collection exceeds 10000 objects; operation refused, not silently truncated.");
                if (item != null) yield return item;
            }
        }

        public static object? Find(object collection, string name)
        {
            var matches = Items(collection).Where(x => string.Equals(x.GetType().GetProperty("Name")?.GetValue(x)?.ToString(), name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact name: " + name);
            return matches.SingleOrDefault();
        }

        public static string[] HardwareOwnerPath(object? start)
        {
            var names = new List<string>(); object? current = start;
            for (int hop = 0; hop < 16 && current != null; hop++)
            {
                if (current is Device device) { names.Insert(0, device.Name); break; }
                if (current is DeviceItem item) { names.Insert(0, item.Name); current = item.Parent; continue; }
                var ownedBy = current.GetType().GetProperty("OwnedBy")?.GetValue(current);
                current = ownedBy ?? (current as IEngineeringInstance)?.Parent;
            }
            return names.ToArray();
        }

        public static Dictionary<string, object?> DynamicAttributes(IEngineeringObject target, string[] names)
        {
            var values = new Dictionary<string, object?>(); var failures = new List<object>();
            foreach (var name in names)
            {
                try { values[name] = HardwareAddressingPolicy.ScalarValue(target.GetAttribute(name)); }
                catch (Exception ex) { failures.Add(new Dictionary<string, object?> { ["attribute"] = name, ["error"] = ex.GetBaseException().Message }); }
            }
            return new Dictionary<string, object?> { ["values"] = values, ["failures"] = failures.ToArray() };
        }

        public static Dictionary<string, object?> AddressRow(Address address) => new HardwareAddressRow
        {
            StartAddress = address.StartAddress, Length = address.Length, IoType = address.IoType.ToString(),
            Controllers = Items(address.AddressControllers).Select(HardwareOwnerPath).ToArray(),
            Attributes = DynamicAttributes(address, HardwareAddressingPolicy.AddressAttributes)
        }.ToEvidence();

        public static Dictionary<string, object?> HwIdentifierRow(HwIdentifier id) => new Dictionary<string, object?>
        {
            ["identifier"] = id.Identifier,
            ["controllers"] = Items(id.HwIdentifierControllers).Select(HardwareOwnerPath).ToArray()
        };

        public static void ApplyScalarsAndAttributes(object target, Dictionary<string, HardwareScalar> properties,
            Dictionary<string, HardwareScalar> attributes, Dictionary<string, object?> meta, bool write)
        {
            var prepared = HardwareAddressingPolicy.Prepare(target.GetType(), properties);
            meta["requestedProperties"] = properties.ToDictionary(p => p.Key, p => HardwareAddressingPolicy.ConvertValue(p.Value, typeof(object)));
            meta["requestedAttributes"] = attributes.ToDictionary(p => p.Key, p => HardwareAddressingPolicy.ConvertValue(p.Value, typeof(object)));
            if (!write) return;
            if (prepared.Count > 0)
            {
                var applied = new List<string>(); meta["appliedProperties"] = applied;
                foreach (var change in prepared)
                {
                    meta["mayHaveChanged"] = true; meta["lastAttemptedProperty"] = change.Property.Name;
                    change.Property.SetValue(target, change.Value);
                    applied.Add(change.Property.Name);
                    if (!HardwareAddressingPolicy.SameValue(change.Property.GetValue(target), change.Value))
                        throw new InvalidOperationException("Property readback differs: " + change.Property.Name + ". Changes are not rolled back.");
                }
            }
            if (attributes.Count > 0) SetDynamicAttributes((IEngineeringObject)target, attributes, meta);
        }

        public static void SetDynamicAttributes(IEngineeringObject target, Dictionary<string, HardwareScalar> attributes, Dictionary<string, object?> meta)
        {
            var applied = new List<string>(); meta["appliedAttributes"] = applied;
            if (attributes.Count > 1)
            {
                var pairs = new List<KeyValuePair<string, object>>(); var refused = new List<Dictionary<string, object?>>(); meta["refusedAttributes"] = refused;
                foreach (var pair in attributes)
                {
                    object? current = null; try { current = target.GetAttribute(pair.Key); } catch { /* swallow(probe-optional): An unreadable current value leaves conversion untyped; the native write and readback still validate the requested value. */ }
                    pairs.Add(new KeyValuePair<string, object>(pair.Key, HardwareAddressingPolicy.ConvertValue(pair.Value, current?.GetType() ?? typeof(object))!));
                }
                meta["mayHaveChanged"] = true;
#if PLC_ATTRIBUTE_DELEGATE
                AttributeDelegate handler = configuration => { AttributeConfiguration problem = configuration; refused.Add(new Dictionary<string, object?> { ["attribute"] = problem.Name, ["kind"] = problem.GetType().Name, ["message"] = problem.Message }); problem.CurrentSelection = AttributeChoiceSelection.Ignore; };
                target.SetAttributes(pairs, handler);
#else
                // V14 SP1-V17 expose SetAttributes without AttributeDelegate. Preserve
                // the per-attribute refusal/readback policy with one write per pair.
                foreach (var pair in pairs)
                {
                    try { target.SetAttribute(pair.Key, pair.Value); }
                    catch (Exception ex)
                    {
                        if (TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(ex)) throw;
                        refused.Add(new Dictionary<string, object?> { ["attribute"] = pair.Key, ["kind"] = ex.GetType().Name, ["message"] = ex.GetBaseException().Message });
                    }
                }
#endif
                var refusedNames = new HashSet<string>(refused.Select(r => (string)r["attribute"]!), StringComparer.Ordinal);
                foreach (var pair in pairs)
                {
                    if (refusedNames.Contains(pair.Key)) continue;
                    applied.Add(pair.Key);
                    if (!HardwareAddressingPolicy.SameValue(target.GetAttribute(pair.Key), pair.Value)) throw new InvalidOperationException("Attribute readback differs: " + pair.Key + ". Changes are not rolled back.");
                }
                if (refused.Count > 0) throw new InvalidOperationException("TIA refused " + refused.Count + " attribute(s) (see refusedAttributes); the others were written and read back.");
                return;
            }
            foreach (var pair in attributes)
            {
                object? current = null; try { current = target.GetAttribute(pair.Key); } catch { /* swallow(probe-optional): An unreadable current value leaves conversion untyped; the native write and readback still validate the requested value. */ }
                var value = HardwareAddressingPolicy.ConvertValue(pair.Value, current?.GetType() ?? typeof(object));
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = pair.Key;
                target.SetAttribute(pair.Key, value);
                applied.Add(pair.Key);
                if (!HardwareAddressingPolicy.SameValue(target.GetAttribute(pair.Key), value)) throw new InvalidOperationException("Attribute readback differs: " + pair.Key + ". Changes are not rolled back.");
            }
        }
    }
}
