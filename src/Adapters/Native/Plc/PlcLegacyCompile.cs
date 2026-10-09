using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Net;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        // This observation stays inside the worker. Its getters reuse I7; the managed
        // diagnostic collector retains the legacy Messages -> State -> counts order.
        internal static object ObserveCompiler(CompilerResult result) => new CompilerObservation(result);
        private sealed class CompilerObservation
        {
            private readonly CompilerResult result;
            private readonly Dictionary<CompilerResultMessage, CompilerMessageObservation> messages = new Dictionary<CompilerResultMessage, CompilerMessageObservation>(CompilerMessageReferenceComparer.Instance);
            internal CompilerObservation(CompilerResult result) => this.result = result;
            public System.Collections.IEnumerable? Messages => Observe(PlcBlockPrimitives.Messages(result));
            public CompilerResultState State => PlcBlockPrimitives.State(result);
            public int ErrorCount => PlcBlockPrimitives.ErrorCount(result);
            public int WarningCount => PlcBlockPrimitives.WarningCount(result);
            internal System.Collections.IEnumerable? Observe(CompilerResultMessageComposition? values)
                => values == null ? null : Enumerate(values);
            private System.Collections.IEnumerable Enumerate(CompilerResultMessageComposition values)
            {
                foreach (var value in (System.Collections.IEnumerable)values)
                {
                    if (value == null) { yield return null; continue; }
                    var message = (CompilerResultMessage)value;
                    if (!messages.TryGetValue(message, out var observation))
                        messages.Add(message, observation = new CompilerMessageObservation(this, message));
                    yield return observation;
                }
            }
        }
        private sealed class CompilerMessageObservation
        {
            private readonly CompilerObservation owner;
            private readonly CompilerResultMessage message;
            internal CompilerMessageObservation(CompilerObservation owner, CompilerResultMessage message)
            { this.owner = owner; this.message = message; }
            public CompilerResultState State => PlcBlockPrimitives.State(message);
            public string Description => PlcBlockPrimitives.Description(message);
            public string Path => PlcBlockPrimitives.Path(message);
            public DateTime DateTime => PlcBlockPrimitives.DateTime(message);
            public int ErrorCount => PlcBlockPrimitives.ErrorCount(message);
            public int WarningCount => PlcBlockPrimitives.WarningCount(message);
            public System.Collections.IEnumerable? Messages => owner.Observe(PlcBlockPrimitives.Messages(message));
            public object? ObjectPath => Optional("ObjectPath");
            public object? Line => Optional("Line");
            public object? Column => Optional("Column");
            public object? ErrorCode => Optional("ErrorCode");
            private object? Optional(string property)
            {
                try { return message.GetType().GetProperty(property)?.GetValue(message); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
        }
        private sealed class CompilerMessageReferenceComparer : IEqualityComparer<CompilerResultMessage>
        {
            internal static readonly CompilerMessageReferenceComparer Instance = new CompilerMessageReferenceComparer();
            public bool Equals(CompilerResultMessage? left, CompilerResultMessage? right) => ReferenceEquals(left, right);
            public int GetHashCode(CompilerResultMessage value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        public CompilerResult CompileSoftware(string softwarePath, string password = "")
        {
            

            if (_session.IsProjectNull())
                throw new PlcSoftwareException("InvalidState", "Project is null");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (PlcBlockPrimitives.SoftwareOrNull(softwareContainer) == null)
                throw new PlcSoftwareException("NotFound", $"SoftwareContainer or Software not found for path '{softwarePath}'");

#if PLC_SAFETY
            if (!string.IsNullOrEmpty(password))
            {
                var deviceItem = PlcBlockPrimitives.ParentOrNull(softwareContainer) as DeviceItem;

                var admin = PlcBlockPrimitives.SafetyOrNull(deviceItem);
                if (admin != null)
                {
                    if (!PlcBlockPrimitives.IsLoggedOn(admin))
                    {
                        SecureString secString = new NetworkCredential("", password).SecurePassword;
                        try
                        {
                            PlcBlockPrimitives.Login(admin, secString);
                        }
                        catch (Exception ex)
                        {
                            throw new PlcSoftwareException("OpennessError", $"Safety login failed: {ex.Message}", null, ex);
                        }
                    }
                }
            }

#else
            if (!string.IsNullOrEmpty(password)) throw new NotSupportedException("Safety compile login requires V16 or later.");
#endif
            // PlcSoftware and classic WinCC (HmiTarget) are themselves service providers, so the
            // compiler service comes off the software object. WinCC Unified's HmiSoftware is NOT
            // an IEngineeringServiceProvider (verified against the V21 PublicAPI) and exposes no
            // Compile of its own — for Unified the compilable object is the owning device item,
            // which is what the TIA UI compiles as well.
            ICompilable compileService = ResolveCompileService(softwareContainer, softwarePath, out var targetKind);
            // Siemens.Engineering.Compiler.CompileProvider is documented but internal in the PublicAPI; ICompilable is the public entry.

            try
            {
                MutationStarted = true;
                CompilerResult result = PlcBlockPrimitives.Compile(compileService);

                if (result == null)
                    throw new PlcSoftwareException("OpennessError", "ICompilable.Compile() returned null");

                try
                {
                    foreach (CompilerResultMessage top in PlcGroupOperations.Items(PlcBlockPrimitives.Messages(result)).Cast<CompilerResultMessage>().Take(20))
                        if (_session.CompilerLoggingEnabled) _session.LogCompilerMessage(PlcBlockPrimitives.Path(top), PlcBlockPrimitives.State(top).ToString(), PlcBlockPrimitives.Description(top), PlcBlockPrimitives.ErrorCount(top), PlcBlockPrimitives.WarningCount(top), PlcBlockPrimitives.DateTime(top), PlcGroupOperations.Items(PlcBlockPrimitives.Messages(top)).Count());
                }
                catch { /* swallow(logging-failure): Optional compiler-message logging must not replace the native compile result. */ }
                return result;
            }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new PlcSoftwareException("OpennessError", $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}", null, tie.InnerException);
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("OpennessError", $"{ex.GetType().FullName}: {ex.Message}", null, ex);
            }
        }

        public ICompilable ResolveCompileService(SoftwareContainer? softwareContainer, string softwarePath, out string targetKind)
        {
            var software = PlcBlockPrimitives.SoftwareOrNull(softwareContainer);
            if (software == null)
                throw new PlcSoftwareException("NotFound", $"SoftwareContainer or Software not found for path '{softwarePath}'");

            // 走过的每一层都记下来：找不到时把这串报出去，下一个人不用再猜层级。
            var probed = new List<string>();

            ICompilable? Probe(object? candidate, string kind)
            {
                if (candidate is not IEngineeringServiceProvider provider) return null;
                ICompilable? service;
                try
                {
                    service = PlcBlockPrimitives.Compiler(provider);
                }
                catch (Exception ex)
                {
                    // 代理对象可能已失效；这一层探不了不代表上一层探不了，记下继续往上。
                    probed.Add($"{kind}(threw {ex.GetType().Name})");
                    return null;
                }
                probed.Add($"{kind}{(service == null ? "(no ICompilable)" : "(OK)")}");
                return service;
            }

            var direct = Probe(software, software.GetType().Name);
            if (direct != null)
            {
                targetKind = software.GetType().Name;
                return direct;
            }

            // 从软件容器往上爬。上限 8 层纯属防御：真实层级是 3~4 层，
            // 加个上限只是不想在代理对象出怪时把自己转死在循环里。
            object? node = softwareContainer;
            for (int depth = 0; node != null && depth < 8; depth++)
            {
                var kind = $"{software.GetType().Name} via {node.GetType().Name}";
                var service = Probe(node, kind);
                if (service != null)
                {
                    targetKind = kind;
                    return service;
                }
                node = PlcBlockPrimitives.ParentOrNull(node as IEngineeringObject);
            }

            throw new PlcSoftwareException(
                "InvalidState",
                $"Software at '{softwarePath}' ({software.GetType().FullName}) is not compilable: " +
                $"neither it nor any owner up to 8 levels provides ICompilable. Probed: {string.Join(" -> ", probed)}");
        }
    }
}
