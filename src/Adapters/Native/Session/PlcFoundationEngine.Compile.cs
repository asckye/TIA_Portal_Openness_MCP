using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcCompileResult CompileSoftware(string softwarePath, string password="", bool dryRun = true)
            => CompileSoftwareCore(softwarePath, password, dryRun, "software", "", Array.Empty<string>());

        public PlcCompileResult CompileSoftwareScoped(string softwarePath, string password = "", bool dryRun = true,
            string scope = "software", string scopePath = "", string unitName = "")
        {
            var parts = PlcCompilePolicy.RequireScope(ReleaseKey, scope, scopePath, unitName);
            return CompileSoftwareCore(softwarePath, password, dryRun, scope, unitName, parts);
        }

        private PlcCompileResult CompileSoftwareCore(string softwarePath, string password, bool dryRun, string scope, string unitName,
                string[] parts)
        {
            PlcCompilePolicy.RequirePasswordCapability(ReleaseKey,password);
            var selected=ReadSelection(softwarePath);
            PlcScopedCompileResult? scoped = null;
            ICompilable? compiler;
            if (scope == "software")
                compiler = ((IEngineeringServiceProvider)selected.Value).GetService<ICompilable>()
                    ?? throw new AdapterPreconditionException("The selected software does not provide ICompilable.","softwarePath",false);
            else
            {
                object owner;
                if (scope == "unit") owner = PlcUnitScope.Optional(selected.Value, parts[0])!;
                else
                {
                    var group = PlcUnitScope.Blocks(selected.Value, PlcUnitScope.Optional(selected.Value, unitName));
                    foreach (string part in scope == "block" ? parts.Take(parts.Length - 1) : parts)
                    {
                        var child = group.Groups.Find(part);
                        if (child == null || child.Name != part) throw AdapterPreconditionException.NotFound("Exact block group not found.", "scopePath");
                        group = child;
                    }
                    if (scope == "group") owner = group;
                    else
                    {
                        var block = PlcBlockPrimitives.Find(group.Blocks, parts.Last());
                        if (block == null || block.Name != parts.Last()) throw AdapterPreconditionException.NotFound("Exact block not found.", "scopePath");
                        owner = block;
                    }
                }
                compiler = owner is IEngineeringServiceProvider provider ? PlcBlockPrimitives.Compiler(provider) : null;
                string path = PlcObjectPath.Format(parts);
                string unit = scope == "unit" ? parts[0] : unitName;
                string identity = selected.ExactPath + (unit == "" ? "" : "/units/" + ObjectPathEncoding.EncodeSegment(unit));
                if (scope != "unit") identity += "/blocks/" + path;
                scoped = new PlcScopedCompileResult { Scope = scope, ScopePath = path, UnitName = unitName, ResolvedObject = identity,
                    Compilable = compiler != null };
                if (!dryRun && compiler == null) throw PlcCompilePolicy.ScopeUnavailable("The exact " + scope + " does not provide ICompilable.");
            }
            if(dryRun)
            {
                PlcCompileResult preview = scoped ?? new PlcCompileResult(); preview.Executed = false; preview.ProjectFile = Project().Path.FullName;
                return preview;
            }
            RequireTargetOffline(selected); var unobservedDevices=CheckProjectOfflineProviders();
            CompilerResult result;
#if PLC_SAFETY
            Siemens.Engineering.Safety.SafetyAdministration? admin=null;
            bool loggedOnHere=false;
            if(password.Length!=0)
            {
                if(ReleaseKey=="21")
                {
                    var assembly=typeof(Siemens.Engineering.Safety.SafetyAdministration).Assembly;
                    EngineeringAssemblyIdentity
                        .RequireMatch(new AssemblyName("Siemens.Engineering.Safety, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b"),
                            assembly.GetName(),assembly.Location);
                    if(!string
                            .Equals(Path.GetDirectoryName(assembly.Location),Path.GetDirectoryName(typeof(PlcSoftware).Assembly.Location),StringComparison
                                .OrdinalIgnoreCase))
                        throw new FileLoadException("Safety must come from the selected V21 API directory.");
                }
                admin=((IEngineeringServiceProvider)(DeviceItem)selected.Context!).GetService<Siemens.Engineering.Safety.SafetyAdministration>()
                    ?? throw new AdapterPreconditionException("The selected PLC does not provide SafetyAdministration; password was not ignored.",
                            "softwarePath",false);
                if(!admin.IsLoggedOnToSafetyOfflineProgram)
                {
                    using(var secure=new System.Net.NetworkCredential("",password).SecurePassword)
                    {
                        try { admin.LoginToSafetyOfflineProgram(secure); } catch {
                            throw new InvalidOperationException("Safety offline login failed; password and native login details are not echoed."); }
                    }
                    loggedOnHere=true;
                }
            }
            try { RequireTargetOffline(selected); unobservedDevices=CheckProjectOfflineProviders(); result=compiler!.Compile(); }
            catch(Exception ex)
            {
                if(loggedOnHere) { try { admin!.LogoffFromSafetyOfflineProgram(); } catch /* swallow(teardown): preserve the original compile failure and attach the safety logoff failure as cleanup evidence */ { ex.Data["safetyCleanup"]="Safety logoff also failed; inspect the session manually."; } }
                throw;
            }
            if(loggedOnHere) {
                try { admin!.LogoffFromSafetyOfflineProgram(); } catch {
                    throw new InvalidOperationException("Compile returned but safety logoff failed; session outcome is unknown."); }
            }
#else
            RequireTargetOffline(selected); unobservedDevices=CheckProjectOfflineProviders(); result=compiler!.Compile();
#endif
            PlcCompileResult response = scoped ?? new PlcCompileResult();
            response.Executed = true; response.ProjectFile = Project().Path.FullName; response.State = result.State.ToString();
            response.ErrorCount = result.ErrorCount; response.WarningCount = result.WarningCount; response.OfflineStateNotExposedByDevices = unobservedDevices;
            PlcCompilePolicy.Classify(response,Messages(result.Messages,0));
            return response;
        }
        private static IEnumerable<PlcDiagnostic> Messages(CompilerResultMessageComposition messages, int depth, string parentPath = "")
        {
            Depth(depth);
            foreach (var m in messages)
            {
                var parts=new List<string> { "State="+m.State };
                if(!string.IsNullOrWhiteSpace(m.Description)) parts.Add("Description="+m.Description);
                string path = string.IsNullOrWhiteSpace(m.Path) ? parentPath : m.Path;
                if(!string.IsNullOrWhiteSpace(path)) parts.Add("Path="+path);
                parts.Add("DateTime="+m.DateTime.ToString("O"));
                parts.Add("ErrorCount="+m.ErrorCount);
                parts.Add("WarningCount="+m.WarningCount);
                yield return new PlcDiagnostic {
                    State=m.State.ToString(),Description=m.Description ?? "",Formatted=string.Join("; ",parts),HasChildren=m.Messages.Count!=0 };
                foreach (var child in Messages(m.Messages, depth + 1, path)) yield return child;
            }
        }
    }
}
