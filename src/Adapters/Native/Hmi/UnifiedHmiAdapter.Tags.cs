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
        public HmiMessage EnsureUnifiedHmiTagTable(string hmiSoftwarePath, string tagTableName)
        {
            return _session.RunHmiStepTool("EnsureUnifiedHmiTagTable", meta => _session.Capture(meta, _ =>
            {
                EngineeringInputValidation.NameOrAbsolutePath(tagTableName);
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var existed = _session.TryFindHmiTagTable(sw, tagTableName) != null;
                var table = EnsureHmiTagTableObject(sw, tagTableName);
                var action = existed ? "exists" : "created";
                meta["action"] = action;
                meta["tagTableType"] = table.GetType().FullName;
                return $"HMI tag table '{tagTableName}' {action}.";
            }));
        }

        private void BindUnifiedHmiTagToPlcSymbol(object tag, string connectionName, string plcName,
            string plcTagSymbol, string hmiDataType, HmiArray writeResults, string address = "")
        {
            var prepared = UnifiedRequestScalar.Prepare(tag.GetType(), UnifiedTagBindingChanges(connectionName, plcName, plcTagSymbol, hmiDataType, address));
            var evidence = new HmiObject();
            try { UnifiedRequestScalar.Apply(tag, prepared, evidence); }
            finally
            {
                if (evidence["mayHaveChanged"] is true) _session.MarkHmiChanged();
                foreach (var pair in evidence) writeResults.Add(pair.Key + "=" + pair.Value);
            }
        }

        private static HmiObject UnifiedTagBindingChanges(string connectionName, string plcName, string plcTagSymbol, string hmiDataType, string address)
        {
            bool absolute = !string.IsNullOrWhiteSpace(address) || plcTagSymbol.StartsWith("%", StringComparison.Ordinal)
                || Regex.IsMatch(plcTagSymbol, @"^(DB|IW|QW|ID|QD|IB|QB|MB|MW|MD)\d", RegexOptions.IgnoreCase);
            var changes = new HmiObject {
                ["DataType"] = hmiDataType,
                ["AccessMode"] = absolute ? "AbsoluteAccess" : "SymbolicAccess",
                ["Address"] = absolute ? (string.IsNullOrWhiteSpace(address) ? plcTagSymbol : address.Trim()) : "",
                ["PlcTag"] = absolute ? "" : NormalizeControllerTagName(plcTagSymbol)
            };
            if (!string.IsNullOrWhiteSpace(plcName)) changes["PlcName"] = plcName;
            if (!string.IsNullOrWhiteSpace(connectionName)) changes["Connection"] = connectionName;
            return changes;
        }

        public HmiMessage EnsureUnifiedHmiTag(string hmiSoftwarePath, string tagTableName, string tagName, string hmiDataType = "Bool", string plcName = "",
                string plcTag = "", string connectionName = "", string address = "", bool requireVerifiedBinding = true)
        {
            UnifiedHmiInputValidation.PlcBinding(plcName, plcTag);
            return _session.RunHmiStepTool("EnsureUnifiedHmiTag", meta => _session.Capture(meta, _ =>
            {
                EngineeringInputValidation.NameOrAbsolutePath(tagTableName);
                var sw = _session.ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                // Check the documented Create return type before creating either a table or tag.
                // Unsupported binding properties must not leave a newly created internal tag behind.
                var deviceTags = TryGetPropertyValue(sw, "Tags") ?? throw new NotSupportedException("Unified Tags unavailable.");
                var existingTag = HmiExchangePolicy.Find(deviceTags, tagName);
                var tagType = existingTag?.GetType() ?? deviceTags.GetType().GetMethod("Create", new[] { typeof(string), typeof(string) })?.ReturnType
                    ?? throw new NotSupportedException("Documented Tags.Create(name, tagTableName) unavailable.");
                UnifiedRequestScalar.Prepare(tagType, UnifiedTagBindingChanges(connectionName, plcName, plcTag, hmiDataType, address));
                var tagTable = EnsureHmiTagTableObject(sw, tagTableName);
                var tags = TryGetPropertyValue(tagTable, "Tags");
                if (tags == null) throw new InvalidOperationException($"Tags collection not found on tag table '{tagTableName}'.");

                bool createIssued = false;
                var tag = HmiExchangePolicy.EnsureTag(sw, tagTable, tagName, () => { meta["mayHaveChanged"] = true; createIssued = true; });
                var action = createIssued ? "created" : "exists";
                meta["action"] = action;
                meta["tagCreateConfirmed"] = createIssued;

                var writeResults = new HmiArray();
                var targetPlcTag = plcTag;
                BindUnifiedHmiTagToPlcSymbol(tag, connectionName, plcName, targetPlcTag, hmiDataType, writeResults, address);

                meta["action"] = action;
                meta["tagType"] = tag.GetType().FullName;
                meta["tagEnumHints"] = DescribeWritableEnumProperties(tag, "TagType", "AccessMode", "AddressAccessMode");
                meta["requestedPlcTag"] = targetPlcTag;
                meta["requestedAddress"] = address ?? string.Empty;
                meta["writeResults"] = writeResults;
                var binding = ClassifyUnifiedHmiTagBinding(tag, connectionName, targetPlcTag, address ?? string.Empty);
                meta["readback"] = binding.Readback;
                meta["bindingStatus"] = binding.Status;
                meta["bindingVerified"] = binding.Verified;
                meta["bindingGuidance"] = binding.Guidance;
                meta["requireVerifiedBinding"] = requireVerifiedBinding;
                if (requireVerifiedBinding && !binding.Verified)
                {
                    throw new InvalidOperationException($"HMI tag '{tagName}' binding is not verified. Status={binding.Status}; {binding.Guidance}; Readback={binding.Readback}");
                }

                return $"HMI tag '{tagName}' {action}. Binding={binding.Status}.";
            }));
        }

        private sealed class UnifiedHmiTagBindingReadback
        {
            public string Status { get; set; } = "Failed";
            public bool Verified { get; set; }
            public string Readback { get; set; } = string.Empty;
            public string Guidance { get; set; } = string.Empty;
        }

        private UnifiedHmiTagBindingReadback ClassifyUnifiedHmiTagBinding(object tag, string connectionName, string plcTag, string address)
        {
            string Attr(params string[] names)
            {
                foreach (var name in names)
                {
                    try
                    {
                        var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanRead)
                        {
                            var v = prop.GetValue(tag)?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) return v!;
                        }
                    }
                    catch /* swallow(probe-optional): Unavailable tag properties fall through to attribute readback and other names. */ { }

                    try
                    {
                        var v = _session.TryGetEngineeringAttribute(tag, name)?.ToString();
                        if (!string.IsNullOrWhiteSpace(v)) return v!;
                    }
                    catch /* swallow(probe-optional): Unavailable tag attributes leave the remaining binding names available. */ { }
                }

                return string.Empty;
            }

            var readback = _session
                .SummarizeHmiObjectReadback(tag, "Connection", "AccessMode", "TagType", "PlcName", "PlcTag", "Address", "DataType", "HmiDataType");
            var connection = Attr("Connection");
            var accessMode = Attr("AccessMode");
            var symbol = Attr("PlcTag");
            var runtimeAddress = Attr("Address");
            var requestedAddress = (address ?? string.Empty).Trim();
            var requestedSymbol = NormalizeControllerTagName(plcTag ?? string.Empty);
            var expectedConnection = (connectionName ?? string.Empty).Trim();

            var connectionOk = string.IsNullOrWhiteSpace(expectedConnection) ||
                string.Equals(connection, expectedConnection, StringComparison.OrdinalIgnoreCase);
            var absoluteOk = !string.IsNullOrWhiteSpace(requestedAddress) &&
                connectionOk &&
                string.Equals(runtimeAddress, requestedAddress, StringComparison.OrdinalIgnoreCase);
            var symbolicOk = !string.IsNullOrWhiteSpace(requestedSymbol) &&
                connectionOk &&
                string.Equals(NormalizeControllerTagName(symbol), requestedSymbol, StringComparison.OrdinalIgnoreCase) &&
                accessMode == "SymbolicAccess";

            if (symbolicOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "SymbolicVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "PLC symbolic HMI tag binding read back successfully."
                };
            }

            if (absoluteOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "AbsoluteVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "Absolute-address HMI tag binding read back successfully."
                };
            }

            var status = string.IsNullOrWhiteSpace(connection) || connection.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0 || connection
                .IndexOf("内部", StringComparison.OrdinalIgnoreCase) >= 0
                ? "InternalOnly"
                : "Unverified";
            return new UnifiedHmiTagBindingReadback
            {
                Status = status,
                Verified = false,
                Readback = readback,
                Guidance =
                    "Pass connectionName plus a verified PLC symbol or absolute address, then read back Connection/AccessMode/PlcTag/Address. Internal HMI " +
                    "tags are not accepted by the stable project-generation path."
            };
        }

        private static string NormalizeControllerTagName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
            return tagName.Replace("\"", string.Empty);
        }



    }
}
#endif
