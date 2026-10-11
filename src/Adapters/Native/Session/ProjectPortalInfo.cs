using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Contracts;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Utilities;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // ---- portal / session diagnostics -------------------------------------------------------------------------------------
        private static HmiObject SessionRow(TiaPortalSession session) => new HmiObject
        {
            ["id"] = session.Id, ["version"] = session.Version, ["isActive"] = session.IsActive, ["attachTime"] = HmiScalar.Json(session.AttachTime),
            ["utilizationTime"] = HmiScalar.Json(session.UtilizationTime), ["accessLevel"] = session.AccessLevel.ToString(), ["trustAuthority"] = session.TrustAuthority.ToString(),
            ["processPath"] = session.ProcessPath?.FullName, ["processId"] = session.ProcessId
        };
        private static HmiObject ProductRow(TiaPortalProduct product) => new HmiObject
        {
            ["name"] = product.Name, ["version"] = product.Version,
            ["options"] = new HmiArray(HardwareGroupOperations.Items(product.Options).Select(o => (object?)new HmiObject { ["name"] = o.GetType().GetProperty("Name")?.GetValue(o)?.ToString(), ["version"] = o.GetType().GetProperty("Version")?.GetValue(o)?.ToString() }).ToArray())
        };
        private static HmiObject ProcessRow(TiaPortalProcess process, bool sessions, bool products)
        {
            var row = new HmiObject { ["id"] = process.Id, ["mode"] = process.Mode.ToString(), ["path"] = process.Path?.FullName, ["projectPath"] = process.ProjectPath?.FullName, ["acquisitionTime"] = HmiScalar.Json(process.AcquisitionTime) };
            if (sessions) { try { row["attachedSessions"] = new HmiArray(process.AttachedSessions.Select(s => (object?)SessionRow(s)).ToArray()); } catch (Exception ex) { row["attachedSessionsError"] = ex.GetBaseException().Message; } }
            if (products) { try { row["installedSoftware"] = new HmiArray(process.InstalledSoftware.Select(p => (object?)ProductRow(p)).ToArray()); } catch (Exception ex) { row["installedSoftwareError"] = ex.GetBaseException().Message; } }
            return row;
        }

        public SecurityResult ReadPortalInfo(bool includeProcesses = true, bool includeSessions = true, bool includeProducts = true)
            => RunProjectLifecycleStep("GetPortalInfo", meta => {
                meta["isConnected"] = portal != null; meta["expectedProject"] = ProjectExpectedName;
                if (includeProcesses)
                {
                    // TiaPortalProcess is a static snapshot (AcquisitionTime); the diagnostic interface never blocks on a busy Portal.
                    var processes = TiaPortal.GetProcesses();
                    meta["processes"] = new HmiArray(processes.Select(p => (object?)ProcessRow(p, includeSessions, includeProducts)).ToArray());
                }
                if (portal != null)
                {
                    try { var current = portal.GetCurrentProcess(); meta["boundProcess"] = ProcessRow(current, includeSessions, false); } catch (Exception ex) { meta["boundProcessError"] = ex.GetBaseException().Message; }
                }
                if (project != null)
                {
                    try { meta["project"] = new HmiObject { ["name"] = project.Name, ["projectClass"] = project.GetType().Name }; } catch (Exception ex) { meta["projectError"] = ex.GetBaseException().Message; }
#if !PROJECT_TEXT_CATEGORIES
                    meta["textCategoriesNote"] = "ProjectBase.TextCategories exists in the V21 PublicAPI only.";
#else
                    try { meta["textCategories"] = new HmiArray(HardwareGroupOperations.Items(project.TextCategories).Cast<TextCategory>().Select(c => (object?)new HmiObject { ["identifier"] = c.Identifier, ["name"] = c.Name }).ToArray()); }
                    catch (Exception ex) { meta["textCategoriesError"] = ex.GetBaseException().Message; }
#endif
                    #if PROJECT_HARDWARE_UTILITIES
                    try { meta["hardwareUtilities"] = new HmiArray(HardwareGroupOperations.Items(project.HwUtilities).Cast<HardwareUtility>().Select(u => (object?)new HmiObject { ["identifier"] = u.Identifier, ["utilityClass"] = u.GetType().Name }).ToArray()); }
                    catch (Exception ex) { meta["hardwareUtilitiesError"] = ex.GetBaseException().Message; }
                    try { meta["objectIdentifierProviderAvailable"] = project.GetService<ObjectIdentifierProvider>() != null; } catch (Exception ex) { meta["objectIdentifierProviderError"] = ex.GetBaseException().Message; }
#endif
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "TiaPortalProcess snapshots (Id, Mode, Path, ProjectPath, AcquisitionTime, AttachedSessions with AccessLevel / TrustAuthority / UtilizationTime, InstalledSoftware with options), the bound process, ProjectBase.TextCategories and HwUtilities. Read-only.";
                return "Portal diagnostics read; no modification.";
            }, requiresProject: false);

    }
}
