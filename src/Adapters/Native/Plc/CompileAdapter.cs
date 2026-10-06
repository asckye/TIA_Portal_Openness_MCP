using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed class CompileNativeTarget
    {
        public object Owner { get; set; } = null!;
        public object? Software { get; set; }
        public DeviceItem? SafetyItem { get; set; }
        public HardwareObject OfflineOwner { get; set; } = null!;
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public ICompilable? Compiler { get; set; }
    }
    // Candidate-only native primitives. Callbacks borrow the existing MTA/STA owner synchronously.
    public sealed class CompileAdapter : ICompileAdapter, ICompileBoundary
    {
        private readonly List<KeyValuePair<object, string>> ids = new List<KeyValuePair<object, string>>();
        private readonly string scope = Guid.NewGuid().ToString("N");
        public Func<CompileObservation> Project { private get; set; }
        private readonly Action thread;
        public Func<CompileNativeTarget> Target { private get; set; }
        private CompileNativeTarget? observedTarget;
#if PLC_SAFETY
        private Siemens.Engineering.Safety.SafetyAdministration? observedSafety;
#endif
        public bool RequiresReset { get; private set; }
        public CompileAdapter(Func<CompileObservation> project, Func<CompileNativeTarget> target, Action thread)
        { Project = project; Target = target; this.thread = thread; }
        private string Id(object? value)
        {
            if (value == null) return "";
            foreach (var pair in ids) if (object.Equals(pair.Key, value)) return pair.Value;
            if (ids.Count >= 16384) throw new InvalidOperationException("Compile target identity budget exceeded.");
            string id = "compile-" + scope + "-" + ids.Count.ToString(CultureInfo.InvariantCulture);
            ids.Add(new KeyValuePair<object, string>(value, id)); return id;
        }
        public CompileObservation Observe()
        {
            thread(); var result = Project(); var target = Target(); observedTarget = target;
            result.TargetId = Id(target.Owner); result.SoftwareId = Id(target.Software);
            result.TargetKind = target.Kind; result.TargetName = target.Name; result.Compilable = target.Compiler != null;
            var online = new List<string>(); bool unavailable = false, applicable = false; int count = 0;
            void Items(HardwareObject owner, string path, int depth)
            {
                if (depth > 64 || ++count > 16384) throw new InvalidOperationException("Incomplete compile offline inventory.");
                if (owner is DeviceItem item)
                {
                    var provider = ((IEngineeringServiceProvider)item).GetService<OnlineProvider>();
                    var software = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>()?.Software;
                    if (provider != null)
                    {
                        applicable = true; string state = provider.State.ToString();
                        if (state != "Offline") online.Add(path);
                    }
                    else if (software is PlcSoftware) { applicable = true; unavailable = true; }
                }
                foreach (var child in owner.DeviceItems) Items(child, path + "/" + child.Name, depth + 1);
            }
            Items(target.OfflineOwner, target.Name, 0);
            result.OfflineTargets = online.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            result.OfflineState = online.Count > 0 ? "online" : unavailable ? "unavailable" : applicable ? "offline" : "not-applicable";
            result.SafetyPermission = "not-applicable";
#if PLC_SAFETY
            observedSafety = null;
#endif
            if (target.SafetyItem != null)
            {
#if PLC_SAFETY
                var admin = ((IEngineeringServiceProvider)target.SafetyItem).GetService<Siemens.Engineering.Safety.SafetyAdministration>(); observedSafety = admin;
                result.SafetyPermission = admin == null ? "not-applicable" : admin.IsLoggedOnToSafetyOfflineProgram ? "logged-on"
                    : admin.IsSafetyOfflineProgramPasswordSet ? "password-required" : "unprotected";
#else
                result.SafetyPermission = "api-unavailable";
#endif
            }
            return result;
        }
        public void BeforeAction() { thread(); if (RequiresReset) CandidatePrimitives.Fail("precondition", "session-reset-required"); }
        public CompileAttempt Execute(CompileCheck check, string password) => CandidateExecution.Compile(this, check, password);
        public void Login(string password)
        {
            thread();
#if PLC_SAFETY
            var admin = observedSafety ?? throw new NotSupportedException("Safety administration unavailable.");
            using var secure = new System.Net.NetworkCredential("", password).SecurePassword;
            admin.LoginToSafetyOfflineProgram(secure);
#else
            throw new NotSupportedException("Safety login unavailable in this API.");
#endif
        }
        public void Logout()
        {
            thread();
#if PLC_SAFETY
            var admin = observedSafety ?? throw new NotSupportedException("Safety administration unavailable.");
            admin.LogoffFromSafetyOfflineProgram();
#else
            throw new NotSupportedException("Safety logout unavailable in this API.");
#endif
        }
        public CompileDiagnostics Compile()
        {
            thread(); var compiler = observedTarget?.Compiler ?? throw new NotSupportedException("Compiler unavailable.");
            var result = compiler.Compile() ?? throw new InvalidOperationException("Compiler returned no result.");
            int count = 0;
            CompileMessage[] Tree(CompilerResultMessageComposition messages, int depth)
            {
                if (depth > 128) throw new InvalidOperationException("Compiler diagnostic depth exceeded.");
                var nodes = new List<CompileMessage>();
                foreach (var m in messages)
                {
                    if (++count > 100000) throw new InvalidOperationException("Compiler diagnostic budget exceeded.");
                    nodes.Add(new CompileMessage { State = m.State.ToString(), Path = m.Path ?? "", Description = m.Description ?? "",
                        ErrorCount = m.ErrorCount, WarningCount = m.WarningCount, Messages = Tree(m.Messages, depth + 1) });
                }
                return nodes.ToArray();
            }
            return new CompileDiagnostics { State = result.State.ToString(), RootErrorCount = result.ErrorCount,
                RootWarningCount = result.WarningCount, Messages = Tree(result.Messages, 0) };
        }
        public void MarkUncertain() { RequiresReset = true; }
    }
}
