using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
#if SECURITY_UMAC
using Siemens.Engineering.Umac;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
#if PLC_SAFETY
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters.Native.Security;

internal sealed class SecuritySession
{
    private readonly PlcFoundationEngine engine;
    private readonly ProjectUserRequest? request;
    private readonly Dictionary<string, SecurityValidationFault> faults;
    internal SecuritySession(PlcFoundationEngine engine, ProjectUserRequest request)
        : this(engine, request.Faults) { this.request = request; }
    internal SecuritySession(PlcFoundationEngine engine, Dictionary<string, SecurityValidationFault> faults)
    { this.engine = engine; this.faults = faults; }
    internal EngineeringProject? CurrentProject => engine.SecurityProject;
    internal SecurityResult RunHmiStepTool(string tool, Func<HmiObject, string> action) => engine.RunSecurityStep(tool, action);
    internal IDisposable AcquireHmiEditAccess() => engine.SecurityEditAccess();
    internal bool IsProjectNull() => engine.PlcB5Session.IsProjectNull();
    internal T RequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
        => engine.SecurityHardwareService<T>(owner, parameter);
    internal string[] HardwareOwnerPath(object owner) => HardwareAddressPrimitives.HardwareOwnerPath(owner);
    internal HmiObject DynamicAttributes(IEngineeringObject owner, string[] names)
        => new HmiObject(HardwareAddressPrimitives.DynamicAttributes(owner, names));
    internal void SetDynamicAttributes(IEngineeringObject owner, HmiObject attributes, HmiObject meta)
        => HardwareAddressPrimitives.SetDynamicAttributes(owner, attributes.ScalarInputs!, meta);
    internal object? FindOnFresh(Func<object> composition, string name, HmiObject meta, string phase)
        => engine.SecurityFindOnFresh(composition, name, meta, phase);
    internal int? CountOnFresh(Func<object> composition, HmiObject meta, string key)
        => engine.SecurityCountOnFresh(composition, meta, key);
    internal HardwareObject ExactEngineeringHardware(string[] devicePath, string[] itemPath)
    { Validate("locator"); return engine.PlcB5Session.ExactEngineeringHardware(devicePath, itemPath); }
    internal Siemens.Engineering.HW.Device? GetDevice(string path) => new PutGetAccess(engine).GetDevice(path);
    internal (DeviceItem? item, string? attrName) FindPutGetAttribute(Siemens.Engineering.HW.Device device) => PutGetAccess.FindPutGetAttribute(device);
    internal bool AttrValueIsEnabled(object? value) => PutGetAccess.AttrValueIsEnabled(value);
    internal HmiObject GetPutGetAccess(string path) => new PutGetAccess(engine).GetPutGetAccess(path);
    internal Siemens.Engineering.SW.PlcSoftware ExactPlcForEngineering(string path, bool writing) => engine.PlcB5Session.ExactPlcForEngineering(path, writing);
    internal void Validate(string stage)
    {
        if (!faults.TryGetValue(stage, out var fault)) return;
        if (fault.Kind == "state") throw new InvalidOperationException(fault.Message);
        throw new ArgumentException(fault.Message);
    }
    internal ProjectUserPlan UserPlan() { Validate("userPlan"); return request!.Plan; }
    internal void RequireUserAction()
    {
#if !SECURITY_DEVICE_RIGHT_DELETE
        if (request?.Action == "deleteDeviceRight") throw new NotSupportedException("CustomDeviceFunctionRight.Delete requires V20 or later.");
#endif
#if !SECURITY_POLICY
        if (request?.Category == "passwordPolicy") throw new NotSupportedException("PasswordPolicyConfigurator requires V18 or later.");
#endif
    }
    internal object? FindOrdinal(object collection, string name, string kind) => ProjectSecurityPrimitives.FindOrdinal(collection, name, kind);
#if SECURITY_UMAC
    internal UmacConfigurator RequireUmac() => ProjectSecurityPrimitives.RequireUmac(CurrentProject!);
    internal Role ExactUmacRole(UmacConfigurator umac, string name) => ProjectSecurityPrimitives.ExactUmacRole(umac, name);
    internal HmiObject UmacRow(object item) => SecurityEvidence.UmacRow(item);
#endif
}
