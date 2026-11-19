using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Optional
{
    internal static class OptionalAssemblyAdmission
    {
        internal static HardwareAddressingReply Run(string release, string assembly, Func<HardwareAddressingReply> operation) => operation();
    }
}

namespace TiaMcp.Adapters.Native.Safety
{
    internal sealed class SafetyValidationSession(PlcFoundationEngine engine, SafetyValidationRequest request)
    {
        internal HardwareAddressingReply Observe(string[] path)
        {
            engine.NativeCalls++;
            return new() { Message = request.Operation, Meta = new() { ["operationSuccess"] = true, ["mayHaveChanged"] = false,
                ["groupPath"] = path, ["dryRun"] = request.DryRun } };
        }
    }

    // Only the native boundary is doubled. Each operation reports the group argument it actually received.
    internal sealed class SafetyValidationAdapter(SafetyValidationSession session)
    {
        internal HardwareAddressingReply ReadSafetyActivationTests(string[] path, params object[] rest) => session.Observe(path);
        internal HardwareAddressingReply ManageSafetyActivationTest(string name, string action, string[] path, params object[] rest) => session.Observe(path);
        internal HardwareAddressingReply ManageSafetyActivationTestGroup(string action, string[] path, params object[] rest) => session.Observe(path);
        internal HardwareAddressingReply ManageSafetyFunction(string test, string action, string[] path, params object[] rest) => session.Observe(path);
        internal HardwareAddressingReply ManageSafetyFunctionCondition(string test, string function, string action, string[] path, params object[] rest)
            => session.Observe(path);
    }
}
