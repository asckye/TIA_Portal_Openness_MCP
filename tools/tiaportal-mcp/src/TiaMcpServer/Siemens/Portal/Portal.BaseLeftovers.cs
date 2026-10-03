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
    // Phase 3 sub-batch 4 (2.7.33): Base leftovers. Official pages: "Diagnostic interfaces on TIA Portal" (TiaPortalProcess /
    // TiaPortalSession / TiaPortalProduct), "Transaction handling", "Identifying cross session object" (ObjectIdentifierProvider),
    // "Accessing normalized type identifiers" / "Export of data in OPC UA XML format" / "Creating and exporting psc file"
    // (HwUtilities), "Managing dynamic certificate settings" (CertificateManagementConfiguration / CertificateSupportedService),
    // DefaultWebPagesFeature.WebApplicationConfigurations, TelecontrolManagement.TelecontrolDataPoints, ConnectionConfiguration
    // route tree (ConfigurationMode / PcInterface / Subnet / Gateway / Address / TargetInterface), RHDownloadProvider /
    // RHOnlineProvider availability, CompileProvider, ProjectBase.TextCategories. Everything is the official V20/V21 API.
    public partial class Portal
    {
        // ---- RunHmiStepTool hooks (the offline test project substitutes both) -------------------------------------------------
        private string ProjectNullMessage(JsonObject meta)
        {
            if (_expectedProjectName == null) return "Project is null";
            meta["expectedProject"] = _expectedProjectName;
            return "Project is null: the explicitly bound project '" + _expectedProjectName + "' is not open in any TIA Portal instance (nothing else was bound in its place); reopen it and call AttachToOpenProject.";
        }
        // Openness exceptions carry structured ExceptionMessageData (Text / DetailText) beside the message.
        private static void AddExceptionMessageData(Exception ex, JsonObject meta)
        {
            if (MigrationRead.Cause(ex) is not EngineeringException engineering) return;
            try
            {
                ExceptionMessageData data = engineering.MessageData;
                meta["messageData"] = new JsonObject { ["text"] = data.Text, ["detailText"] = data.DetailText };
                meta["detailMessageData"] = new JsonArray(engineering.DetailMessageData.Select(d => (JsonNode)new JsonObject { ["text"] = d.Text, ["detailText"] = d.DetailText }).ToArray());
            }
            catch { }
        }

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

        // ---- hardware utilities ------------------------------------------------------------------------------------------------
        private T RequireHardwareUtility<T>(string identifier) where T : HardwareUtility
        {
            HardwareUtilityComposition utilities = _project!.HwUtilities;
            var utility = utilities.Find(identifier) as T ?? EngineeringGroupOperations.Items(utilities).OfType<T>().FirstOrDefault()
                ?? throw new NotSupportedException(typeof(T).Name + " is not among Project.HwUtilities (" + string.Join(", ", EngineeringGroupOperations.Items(utilities).Cast<HardwareUtility>().Select(u => u.Identifier)) + ").");
            return utility;
        }

        // ---- object identifiers and show-in-editor -------------------------------------------------------------------------------
        private IEngineeringObject ExactIdentifiableObject(string kind, string devicePathJson, string itemPathJson, string softwarePath, string objectPath, JsonObject meta)
        {
            if (kind == "device" || kind == "deviceItem")
            {
                var hardware = ExactEngineeringHardware(devicePathJson, kind == "device" ? "[]" : itemPathJson); meta["ownerPath"] = HardwareOwnerPath(hardware);
                return hardware;
            }
            var plc = ExactPlcForEngineering(softwarePath, false); var parts = EngineeringGroupOperations.Parts(objectPath);
            object root = kind == "plcBlock" ? plc.BlockGroup : kind == "plcType" ? (object)plc.TypeGroup : plc.TagTableGroup;
            var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            var collection = EngineeringGroupOperations.Get(group, kind == "plcBlock" ? "Blocks" : kind == "plcType" ? "Types" : "TagTables");
            return EngineeringGroupOperations.Find(collection, parts.Last()) as IEngineeringObject ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + kind + " not found: " + objectPath);
        }
        private static JsonObject IdentifiedObjectRow(IEngineeringObject target)
        {
            var row = new JsonObject { ["objectClass"] = target.GetType().Name };
            try { row["name"] = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString(); } catch { }
            if (target is ISystemObject systemObject) { try { row["isSystemObject"] = systemObject.IsSystemObject; } catch (Exception ex) { row["isSystemObjectError"] = ex.GetBaseException().Message; } }
            return row;
        }

        public ResponseMessage ReadObjectIdentifier(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", string identifier = "")
            => RunHmiStepTool("ReadObjectIdentifier", meta => {
                BaseLeftoversLogic.ValidateObjectSelection(kind, devicePathJson, itemPathJson, softwarePath, objectPath, identifier);
                ObjectIdentifierProvider provider = _project!.GetService<ObjectIdentifierProvider>() ?? throw new NotSupportedException("ObjectIdentifierProvider service unavailable on this project.");
                meta["kind"] = kind;
                if (!string.IsNullOrEmpty(identifier))
                {
                    // Find(identifier) returns the object behind an identifier issued earlier (cross-session stable per the official page).
                    var found = provider.Find(identifier) ?? throw new PortalException(PortalErrorCode.NotFound, "ObjectIdentifierProvider.Find returned null for the identifier.");
                    meta["identifier"] = identifier; meta["found"] = IdentifiedObjectRow(found);
                    if (found is HardwareObject hardware) meta["ownerPath"] = HardwareOwnerPath(hardware);
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Object found from its identifier; no modification.";
                }
                var target = ExactIdentifiableObject(kind, devicePathJson, itemPathJson, softwarePath, objectPath, meta);
                meta["object"] = IdentifiedObjectRow(target); meta["identifier"] = provider.GetIdentifier(target);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "ObjectIdentifierProvider.GetIdentifier / Find; official support: Device, DeviceItem, code and data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct.";
                return "Object identifier read; no modification.";
            });

        public ResponseMessage ShowObjectInEditor(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", bool dryRun = true)
            => RunHmiStepTool("ShowObjectInEditor", meta => {
                BaseLeftoversLogic.ValidateObjectSelection(kind, devicePathJson, itemPathJson, softwarePath, objectPath, "");
                var target = ExactIdentifiableObject(kind, devicePathJson, itemPathJson, softwarePath, objectPath, meta);
                meta["kind"] = kind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["object"] = IdentifiedObjectRow(target);
                IShowable showable = target as IShowable ?? throw new NotSupportedException(target.GetType().Name + " does not implement IShowable (Device and the STEP 7 blocks / types / tag tables / watch and force tables do).");
                if (dryRun) return "ShowInEditor preview; the TIA Portal UI was not touched.";
                showable.ShowInEditor(); meta["apiCallSuccess"] = true;
                return "IShowable.ShowInEditor invoked: the object is opened in the TIA Portal editor (UI only; no project change).";
            });

        // ---- transactions ---------------------------------------------------------------------------------------------------------
        // One ExclusiveAccess + Transaction wraps several tool calls into a single TIA undo unit; the inner tools reuse the ambient
        // exclusive access (AcquireHmiEditAccess) instead of opening a second one.
        internal sealed class TransactionScope : IEngineeringTransaction
        {
            private readonly Portal _portal; private readonly ExclusiveAccess _access; private readonly Transaction _transaction; private bool _disposed;
            internal TransactionScope(Portal portal, ExclusiveAccess access, Transaction transaction) { _portal = portal; _access = access; _transaction = transaction; }
            public bool CanCommit => _transaction.CanCommit;
            public bool CommitRequested => _transaction.CommitRequested;
            public bool IsCancellationRequested { get { try { return _access.IsCancellationRequested; } catch { return true; } } }
            public void Commit() => _transaction.CommitOnDispose();
            public void Dispose()
            {
                if (_disposed) return; _disposed = true;
                try { _transaction.Dispose(); } finally { _portal._ambientExclusiveAccess = null; _access.Dispose(); }
            }
        }
        private ExclusiveAccess? _ambientExclusiveAccess;
        internal TransactionScope BeginTransaction(string text)
        {
            if (_portal == null) throw new PortalException(PortalErrorCode.InvalidState, "TIA session unavailable.");
            if (_ambientExclusiveAccess != null) throw new PortalException(PortalErrorCode.InvalidState, "A transaction is already open in this engine.");
            EnsureBoundProjectUnchanged("Transaction");
            var persistence = _project as ITransactionSupport ?? throw new NotSupportedException("The bound project does not implement ITransactionSupport.");
            var access = _portal.ExclusiveAccess(text);
            try
            {
                var transaction = access.Transaction(persistence, text);
                _ambientExclusiveAccess = access;
                return new TransactionScope(this, access, transaction);
            }
            catch { access.Dispose(); throw; }
        }
    }
}
