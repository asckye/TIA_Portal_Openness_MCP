using System.Collections.Generic;
using System.IO;
using System.Security;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.ModelContextProtocol;
using System;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private string? _plcLookupPathsSuffix;

        // Read and Write record intent only; both use the same exact/structural alias policy.
        private PlcSoftware? ResolvePlc(string softwarePath, PlcAccess access)
        {
            _logger?.LogInformation("Resolving PLC for {Access}: {SoftwarePath}", access, softwarePath);
            _plcLookupPathsSuffix = null;
            if (IsProjectNull()) return null;
            _deviceScanFirstError = null;
            if (!ReferenceEquals(_project, _softwareCacheProject))
            {
                _softwareContainerCache.Clear();
                _plcResolutionCache.Clear();
                _softwareCacheProject = _project;
            }
            return ResolveCachedPlc(softwarePath, path => ResolveSoftwareLookup(path, true),
                container => container.Software as PlcSoftware)
                ?? MatchAvailablePlcSoftware((softwarePath ?? string.Empty).Trim());
        }

        private PlcSoftware? ResolveCachedPlc(string softwarePath,
            Func<string, SoftwareContainer?> lookup, Func<SoftwareContainer, PlcSoftware?> software)
        {
            var cacheKey = softwarePath ?? string.Empty;
            var path = cacheKey.Trim();
            _softwareContainerCache.Remove(cacheKey);
            if (_plcResolutionCache.TryGetValue(path, out var cached))
            {
                var plc = software(cached);
                if (plc != null)
                {
                    _softwareContainerCache[cacheKey] = cached;
                    return plc;
                }
                _plcResolutionCache.Remove(path);
            }
            var container = lookup(path);
            if (container == null) return null;
            var resolved = software(container);
            if (resolved != null)
            {
                // Only a complete, unique strict lookup can populate the PLC cache.
                // Service lookup uses this same CPU, including on normalized cache hits.
                _plcResolutionCache[path] = container;
                _softwareContainerCache[cacheKey] = container;
            }
            return resolved;
        }

        private PlcSoftware ExactPlcForEngineering(string softwarePath, bool writing)
        {
            var plc = ResolvePlc(softwarePath, writing ? PlcAccess.Write : PlcAccess.Read)
                ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath + AvailablePlcPathsSuffix());
            if (writing && ResolvePlcService<OnlineProvider>(softwarePath, plc)?.State.ToString() != "Offline")
                throw new PortalException(PortalErrorCode.InvalidState, "Confirmed Offline state is required.");
            return plc;
        }

        // JSON segments preserve legal '/' in station names. Never use CPU-name aliases for deletion.
        private Device ExactEngineeringDevice(string pathJson)
        {
            var names = JsonNode.Parse(pathJson)?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
                ?? throw new ArgumentException("devicePathJson must be an array.");
            if (names.Length < 1 || names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Device path needs 1-64 exact nonempty names.");
            if (names.Length == 1)
                return (Device)(EngineeringGroupOperations.Find(EnumerateAllDevices().ToArray(), names[0])
                    ?? throw new InvalidOperationException("Exact unique device not found."));
            var group = EngineeringGroupOperations.Find(_project!.DeviceGroups, names[0]) as DeviceUserGroup
                ?? throw new InvalidOperationException("Device group not found: " + names[0]);
            foreach (var name in names.Skip(1).Take(names.Length - 2))
                group = (DeviceUserGroup)(EngineeringGroupOperations.Find(group.Groups, name) ?? throw new InvalidOperationException("Device group not found: " + name));
            return (Device)(EngineeringGroupOperations.Find(group.Devices, names.Last()) ?? throw new InvalidOperationException("Device not found."));
        }
        private HardwareObject ExactEngineeringHardware(string devicePathJson, string itemPathJson)
        {
            HardwareObject current = ExactEngineeringDevice(devicePathJson);
            var names = JsonNode.Parse(itemPathJson)?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
                ?? throw new ArgumentException("itemPathJson must be an array.");
            if (names.Length > 64 || names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid item path.");
            foreach (var name in names)
            {
                // fall back to the hardware-component association (Items) - a Comfort panel's head item reaches its
                // IE_CP_1 only that way, an S7-1500 rail reaches its plugged modules only that way (real project).
                var next = EngineeringGroupOperations.Find(current.DeviceItems, name)
                    ?? current.Items.Cast<object?>().FirstOrDefault(i => i is DeviceItem d && string.Equals(d.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Device item not found: " + name + " (children of " + current.Name + ": " + string.Join(", ", current.DeviceItems.Select(d => d.Name).Concat(current.Items.OfType<DeviceItem>().Select(d => d.Name)).Distinct().Take(20)) + ").");
                current = (DeviceItem)next;
            }
            return current;
        }

        private object ExactOpenEngineeringLibrary(string libraryName)
            => string.IsNullOrEmpty(libraryName) ? _project!.ProjectLibrary
                : EngineeringGroupOperations.Find(_portal!.GlobalLibraries, libraryName) ?? throw new InvalidOperationException("Exact open global library not found. Open it in TIA first; no implicit open or close.");

        private object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath)
        {
            var sc = GetSoftwareContainer(hmiSoftwarePath);
            var software = sc?.Software;
            if (software == null)
            {
                throw new InvalidOperationException($"HMI software not found at '{hmiSoftwarePath}'.");
            }

            return software;
        }

        // ---- hardware utilities ------------------------------------------------------------------------------------------------
        private T RequireHardwareUtility<T>(string identifier) where T : HardwareUtility
        {
            HardwareUtilityComposition utilities = _project!.HwUtilities;
            var utility = utilities.Find(identifier) as T ?? EngineeringGroupOperations.Items(utilities).OfType<T>().FirstOrDefault()
                ?? throw new NotSupportedException(typeof(T).Name + " is not among Project.HwUtilities (" + string.Join(", ", EngineeringGroupOperations.Items(utilities).Cast<HardwareUtility>().Select(u => u.Identifier)) + ").");
            return utility;
        }

    }
}
