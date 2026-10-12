using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Reflection;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed partial class HmiSession : IUnifiedHmiInspectionSession
    {
        IDisposable IUnifiedHmiInspectionSession.AcquireHmiEditAccess() => AcquireHmiEditAccess();
        internal Project? CurrentProject => session.CurrentProject;
        internal Assembly UnifiedAssembly
        {
            get
            {
#if HMI_UNIFIED
                return typeof(Siemens.Engineering.HmiUnified.HmiSoftware).Assembly;
#else
                throw new NotSupportedException("WinCC Unified HMI API requires V19 or later.");
#endif
            }
        }
        internal object ExactUnifiedRoot(string path)
        {
            var root = ResolveHmiSoftwareOrThrow(path);
            if (root.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware")
                throw new NotSupportedException("Unified HMI required.");
            return root;
        }
        internal object ResolveHmiScreenOrThrow(string software, string screen) => HmiExactAccess.Screen(ResolveHmiSoftwareOrThrow(software), screen);
        internal object? TryFindByNameInCollection(object target, string[] properties, string name)
        {
            foreach (string property in properties)
            {
                var collection = UnifiedHmiAccess.TryGetPropertyValue(target, property);
                if (collection != null && FindExistingByName(collection, name) is object found) return found;
            }
            return null;
        }
        internal bool TrySetProperty(object target, string property, object? value)
        {
            try
            {
                var info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                if (info == null || !info.CanWrite) return false;
                info.SetValue(target, CoerceReflectionValue(value, info.PropertyType));
                return true;
            }
            catch /* swallow(native-fallback): preserve the original optional property write fallback */ { return false; }
        }
        internal object? CoerceReflectionValue(object? value, Type type)
            => new ReflectionSession((_, __, ___, ____) => null, () => null).CoerceReflectionValue(value, type);
        internal object? TryGetEngineeringAttribute(object target, string name)
        {
            try { return target.GetType().GetMethod("GetAttribute", new[] { typeof(string) })?.Invoke(target, new object[] { name }); }
            catch /* swallow(probe-optional): preserve the original optional attribute fallback */ { return null; }
        }
        internal string SummarizeHmiObjectReadback(object target, params string[] names)
        {
            var parts = new List<string>();
            foreach (string name in names)
            {
                object? value = null;
                bool got = false;
                try
                {
                    var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanRead) { value = property.GetValue(target); got = true; }
                }
                catch /* swallow(probe-optional): preserve engineering attribute readback fallback */ { }
                if (!got) { value = TryGetEngineeringAttribute(target, name); got = value != null; }
                if (got) parts.Add($"{name}={value ?? ""}");
            }
            return string.Join("; ", parts);
        }
        internal HmiArray ToHmiArray(IEnumerable<string> values) => new HmiArray(values.Cast<object?>().ToArray());
        internal DeviceItem? GetDeviceItemByPath(string path) => engine.UnifiedHmiDeviceItem(path);
        internal void FillUnifiedHmiPartnerNetworkInfo(DeviceItem root, UnifiedHmiPlcPartnerInfo info) => engine.FillUnifiedHmiPartnerNetworkInfo(root, info);
        internal HmiObject? ValidateUnifiedProperties(Type type, string input)
        {
            var admission = engine.UnifiedPropertyAdmission
                ?? throw new InvalidOperationException("Unified screen item property admission is unavailable.");
            return admission(type, input) is Dictionary<string, object?> error ? new HmiObject(error) : null;
        }
    }

    internal sealed class UnifiedHmiPlcPartnerInfo
    {
        public string SoftwarePath { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string StationName { get; set; } = string.Empty;
        public string NodeName { get; set; } = string.Empty;
        public string InitialAddress { get; set; } = string.Empty;
        public string Family { get; set; } = "UNKNOWN";
        public string Summary => $"SoftwarePath={SoftwarePath}; DeviceName={DeviceName}; StationName={StationName}; NodeName={NodeName}; InitialAddress={InitialAddress}; Family={Family}";
    }
}
