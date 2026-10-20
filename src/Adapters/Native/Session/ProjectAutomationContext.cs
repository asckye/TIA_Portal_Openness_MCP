using System;
using System.Reflection;
using TiaMcpServer;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
namespace TiaMcp.Adapters;
public sealed partial class PlcFoundationEngine
{

    public Func<string>? ProjectValidationTree { get; set; }
    private string ProjectContextTree() => ProjectValidationTree != null ? ProjectValidationTree() : ReadProjectTree();
    private static string? ProjectSoftwareName(object? value)
    {
        if (value == null) return null;
        try { return value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)?.ToString(); }
        catch /* swallow(probe-optional): objects without a readable Name yield no name hint */ { return null; }
    }
    private static string ProjectExceptionDetail(Exception error)
    {
        if (error is TargetInvocationException invocation && invocation.InnerException != null)
            return $"{invocation.InnerException.GetType().FullName}: {invocation.InnerException.Message}\n{invocation.InnerException}";
        if (error.InnerException != null)
            return $"{error.GetType().FullName}: {error.Message}\nInner: {error.InnerException.GetType().FullName}: {error.InnerException.Message}\n{error}";
        return $"{error.GetType().FullName}: {error.Message}\n{error}";
    }
        public SecurityResult ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            Check();
            var meta = new HmiObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false
            };

            var problems = new HmiArray();
            var devices = new HmiArray();
            var software = new HmiArray();
            meta["problems"] = problems;
            meta["devices"] = devices;
            meta["software"] = software;

            if (PlcB5Session.IsProjectNull())
            {
                problems.Add("Project is null. Use ConnectPortal + AttachOpenProject/OpenProject first.");
                return new SecurityResult { Message = "Automation context invalid", Meta = meta };
            }

            meta["project"] = project?.Name ?? string.Empty;

            try
            {
                // Root devices, nested user groups and the ungrouped system group (ProjectDeviceWalk).
                foreach (var located in TiaMcp.Adapters.Native.Hardware.ProjectDeviceWalk.Walk(project!.Devices, project.DeviceGroups, project.UngroupedDevicesGroup))
                {
                    var d = located.Device;
                    var deviceInfo = new HmiObject
                    {
                        ["name"] = d.Name,
                        ["type"] = d.GetType().FullName ?? d.GetType().Name,
                        ["groupPath"] = located.GroupPath
                    };
                    devices.Add(deviceInfo);

                    foreach (var di in d.DeviceItems)
                    {
                        TryAddSoftwareInfo(di, software);
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Device/software scan failed: {ProjectExceptionDetail(ex)}");
            }

            if (devices.Count == 0)
            {
                problems.Add("No devices found in current project.");
            }

            if (!string.IsNullOrWhiteSpace(expectedPlcSoftwarePath))
            {
                var plc = PlcB5Session.GetSoftwareContainer(expectedPlcSoftwarePath)?.Software as PlcSoftware;
                meta["expectedPlcSoftwarePath"] = expectedPlcSoftwarePath;
                meta["expectedPlcFound"] = plc != null;
                if (plc == null) problems.Add($"PLC software not found at '{expectedPlcSoftwarePath}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHmiSoftwarePath))
            {
                var hmi = PlcB5Session.GetSoftwareContainer(expectedHmiSoftwarePath)?.Software;
                meta["expectedHmiSoftwarePath"] = expectedHmiSoftwarePath;
                meta["expectedHmiFound"] = hmi != null && hmi.GetType().FullName?.IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hmi == null) problems.Add($"HMI software not found at '{expectedHmiSoftwarePath}'.");
            }

            try
            {
                meta["projectTree"] = ProjectContextTree();
            }
            catch (Exception ex)
            {
                problems.Add($"Project tree failed: {ProjectExceptionDetail(ex)}");
            }

            meta["success"] = problems.Count == 0;
            return new SecurityResult
            {
                Message = problems.Count == 0 ? "Automation context OK" : "Automation context has problems",
                Meta = meta
            };

            void TryAddSoftwareInfo(DeviceItem item, HmiArray target)
            {
                try
                {
                    var sc = item.GetService<SoftwareContainer>();
                    if (sc?.Software != null)
                    {
                        target.Add(new HmiObject
                        {
                            ["deviceItem"] = item.Name,
                            ["softwareName"] = ProjectSoftwareName(sc.Software) ?? item.Name,
                            ["softwareType"] = sc.Software.GetType().FullName ?? sc.Software.GetType().Name
                        });
                    }
                }
                catch /* swallow(enumerate-optional): Unavailable software services or children are skipped while retaining the remaining preflight inventory. */ { }

                try
                {
                    foreach (var child in item.DeviceItems)
                    {
                        TryAddSoftwareInfo(child, target);
                    }
                }
                catch /* swallow(enumerate-optional): Unavailable software services or children are skipped while retaining the remaining preflight inventory. */ { }
            }
        }
}
