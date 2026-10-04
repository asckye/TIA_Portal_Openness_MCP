using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // ---- portal / session diagnostics -------------------------------------------------------------------------------------
        private static JsonObject SessionRow(TiaPortalSession session) => new JsonObject
        {
            ["id"] = session.Id, ["version"] = session.Version, ["isActive"] = session.IsActive, ["attachTime"] = EngineeringScalarProperties.Json(session.AttachTime),
            ["utilizationTime"] = EngineeringScalarProperties.Json(session.UtilizationTime), ["accessLevel"] = session.AccessLevel.ToString(), ["trustAuthority"] = session.TrustAuthority.ToString(),
            ["processPath"] = session.ProcessPath?.FullName, ["processId"] = session.ProcessId
        };
        private static JsonObject ProductRow(TiaPortalProduct product) => new JsonObject
        {
            ["name"] = product.Name, ["version"] = product.Version,
            ["options"] = new JsonArray(EngineeringGroupOperations.Items(product.Options).Select(o => (JsonNode)new JsonObject { ["name"] = o.GetType().GetProperty("Name")?.GetValue(o)?.ToString(), ["version"] = o.GetType().GetProperty("Version")?.GetValue(o)?.ToString() }).ToArray())
        };
        private static JsonObject ProcessRow(TiaPortalProcess process, bool sessions, bool products)
        {
            var row = new JsonObject { ["id"] = process.Id, ["mode"] = process.Mode.ToString(), ["path"] = process.Path?.FullName, ["projectPath"] = process.ProjectPath?.FullName, ["acquisitionTime"] = EngineeringScalarProperties.Json(process.AcquisitionTime) };
            if (sessions) { try { row["attachedSessions"] = new JsonArray(process.AttachedSessions.Select(s => (JsonNode)SessionRow(s)).ToArray()); } catch (Exception ex) { row["attachedSessionsError"] = ex.GetBaseException().Message; } }
            if (products) { try { row["installedSoftware"] = new JsonArray(process.InstalledSoftware.Select(p => (JsonNode)ProductRow(p)).ToArray()); } catch (Exception ex) { row["installedSoftwareError"] = ex.GetBaseException().Message; } }
            return row;
        }

        public ResponseMessage ReadPortalInfo(bool includeProcesses = true, bool includeSessions = true, bool includeProducts = true)
            => RunHmiStepTool("ReadPortalInfo", meta => {
                meta["isConnected"] = _portal != null; meta["expectedProject"] = _expectedProjectName;
                if (includeProcesses)
                {
                    // TiaPortalProcess is a static snapshot (AcquisitionTime); the diagnostic interface never blocks on a busy Portal.
                    var processes = TiaPortal.GetProcesses();
                    meta["processes"] = new JsonArray(processes.Select(p => (JsonNode)ProcessRow(p, includeSessions, includeProducts)).ToArray());
                }
                if (_portal != null)
                {
                    try { var current = _portal.GetCurrentProcess(); meta["boundProcess"] = ProcessRow(current, includeSessions, false); } catch (Exception ex) { meta["boundProcessError"] = ex.GetBaseException().Message; }
                }
                if (_project != null)
                {
                    try { meta["project"] = new JsonObject { ["name"] = _project.Name, ["projectClass"] = _project.GetType().Name }; } catch (Exception ex) { meta["projectError"] = ex.GetBaseException().Message; }
#if TIA_V20
                    meta["textCategoriesNote"] = "ProjectBase.TextCategories exists in the V21 PublicAPI only.";
#else
                    try { meta["textCategories"] = new JsonArray(EngineeringGroupOperations.Items(_project.TextCategories).Cast<TextCategory>().Select(c => (JsonNode)new JsonObject { ["identifier"] = c.Identifier, ["name"] = c.Name }).ToArray()); }
                    catch (Exception ex) { meta["textCategoriesError"] = ex.GetBaseException().Message; }
#endif
                    try { meta["hardwareUtilities"] = new JsonArray(EngineeringGroupOperations.Items(_project.HwUtilities).Cast<HardwareUtility>().Select(u => (JsonNode)new JsonObject { ["identifier"] = u.Identifier, ["utilityClass"] = u.GetType().Name }).ToArray()); }
                    catch (Exception ex) { meta["hardwareUtilitiesError"] = ex.GetBaseException().Message; }
                    try { meta["objectIdentifierProviderAvailable"] = _project.GetService<ObjectIdentifierProvider>() != null; } catch (Exception ex) { meta["objectIdentifierProviderError"] = ex.GetBaseException().Message; }
                }
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "TiaPortalProcess snapshots (Id, Mode, Path, ProjectPath, AcquisitionTime, AttachedSessions with AccessLevel / TrustAuthority / UtilizationTime, InstalledSoftware with options), the bound process, ProjectBase.TextCategories and HwUtilities. Read-only.";
                return "Portal diagnostics read; no modification.";
            }, requiresProject: false);

    }
}
