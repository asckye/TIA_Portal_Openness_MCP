using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    // Phase 6 ⑥-③ (2.7.42): typed Teamcenter Gateway option package (identical on V20 / V21). The engine keeps one active
    // TcGatewayConnectionInfo per session; the dataset and workflow tools present it to the gateway.
    public static partial class McpServer
    {
        [McpServerTool(Name="ManageTeamcenterConnection"), Description("[L2][VersionControl][WRITE] Teamcenter Gateway connection of this engine session (TiaPortal.GetService<TeamcenterConnectionProvider>): read (connected, group, role, SHA-256 prefix of the session token, provider availability), connect (Connect(userName, password as SecureString - never logged, group, role, hostUrl, instance)), connectSso (ConnectSSO(hostUrl, instance, loginUrl, applicationId); without an active SSO session Teamcenter prompts in a browser), disconnect (Disconnect(info)). One connection per session; the info object stays in engine memory for ManageTeamcenterDataset / ManageTeamcenterWorkflow. Default preview; project unchanged.")]
        public static ResponseMessage ManageTeamcenterConnection(
            [Description("action: the operation to perform - read | connect | connectSso | disconnect.")] string action="read",
            [Description("userName: user name.")] string userName="",
            string password="",
            [Description("group: group name.")] string group="",
            [Description("role: Teamcenter role name.")] string role="",
            [Description("hostUrl: Teamcenter host URL.")] string hostUrl="",
            [Description("instance: Teamcenter instance name.")] string instance="",
            [Description("loginUrl: SSO login URL.")] string loginUrl="",
            [Description("applicationId: SSO application id.")] string applicationId="",
            bool dryRun=true)
            => Portal.ManageTeamcenterConnection(action,userName,password,group,role,hostUrl,instance,loginUrl,applicationId,dryRun);
        [McpServerTool(Name="ManageTeamcenterDataset"), Description("[L2][VersionControl][WRITE] Teamcenter datasets with the active connection: checkout / checkin / cancelCheckout (TcGatewayLockProvider.CheckoutDataset / CheckinDataset / CancelCheckoutDataset(info, itemId, revisionId, datasetType T4TiaProjectDataset | T4TiaLibraryDataset, datasetName)), search (TcGatewaySearchAndDownloadProvider.Search(info, itemType Project | GlobalLibrary, tiaObjectName, itemId, itemName, revisionId; wildcards allowed, at least one of the three names) -> item ids with revision ids), download (Download(info, itemId, revisionId, itemType, localCacheOption Overwrite | DoNotOverwrite) -> starter file path in the Teamcenter cache; nothing is opened automatically). Default preview (search runs directly); the local project is unchanged.")]
        public static ResponseMessage ManageTeamcenterDataset(
            [Description("action: the operation to perform - checkout | checkin | cancelCheckout | search | download.")] string action,
            [Description("itemId: Teamcenter item id.")] string itemId="",
            [Description("revisionId: Teamcenter revision id.")] string revisionId="",
            [Description("datasetType: T4TiaProjectDataset | T4TiaLibraryDataset.")] string datasetType="",
            [Description("datasetName: exact dataset name.")] string datasetName="",
            [Description("itemType: Project | GlobalLibrary.")] string itemType="",
            [Description("tiaObjectName: name of the TIA project / library object.")] string tiaObjectName="",
            string itemName="",
            [Description("localCacheOption: Overwrite | DoNotOverwrite.")] string localCacheOption="",
            bool dryRun=true)
            => Portal.ManageTeamcenterDataset(action,itemId,revisionId,datasetType,datasetName,itemType,tiaObjectName,itemName,localCacheOption,dryRun);
        [McpServerTool(Name="ManageTeamcenterWorkflow"), Description("[L2][VersionControl][WRITE] Save the open project (target project) or an open global library (target globalLibrary + libraryName) to Teamcenter through TcGatewayWorkflowProvider: readCustomAttributes (GetTeamcenterCustomAttributes(info, itemType e.g. T4TiaProject) -> name, data type, default, required, bounds, list of values), save / saveWithProxyObject (Save / SaveWithProxyObject(info, localCacheOption Overwrite | DoNotOverwrite)), saveToItem / saveToItemWithProxyObject (itemId, revisionId, localCacheOption), saveAsNewItem / saveAsNewItemWithProxyObject (itemDetailsJson {itemId, itemName*, revisionId, teamcenterItemType*, comment, teamcenterFolder, teamcenterProject [..]} via ItemDetailsDelegate), saveAsNewRevision / saveAsNewRevisionWithProxyObject (revisionDetailsJson {revisionId, comment} via RevisionDetailsDelegate; itemType names the mapped attribute type when customAttributesJson is given). customAttributesJson {name: value} sets mapped TeamcenterProperty values with SetValue + ErrorCallback before saving. Every save action saves the open object through the gateway and needs confirmSave=true; returns the native ItemInfo. Default preview.")]
        public static ResponseMessage ManageTeamcenterWorkflow(
            [Description("action: the operation to perform - readCustomAttributes | save | saveWithProxyObject | saveToItem | saveToItemWithProxyObject | saveAsNewItem | saveAsNewItemWithProxyObject | saveAsNewRevision | saveAsNewRevisionWithProxyObject.")] string action,
            [Description("target: the target of the action (see the tool description).")] string target="project",
            string libraryName="",
            string itemType="",
            [Description("itemId: Teamcenter item id.")] string itemId="",
            [Description("revisionId: Teamcenter revision id.")] string revisionId="",
            [Description("localCacheOption: Overwrite | DoNotOverwrite.")] string localCacheOption="",
            [Description("itemDetailsJson: JSON object of item details (see the tool description).")] string itemDetailsJson="{}",
            [Description("revisionDetailsJson: JSON object of revision details.")] string revisionDetailsJson="{}",
            [Description("customAttributesJson: JSON object attribute name -> value.")] string customAttributesJson="{}",
            [Description("confirmSave: must be true together with dryRun=false to save to Teamcenter.")] bool confirmSave=false,
            bool dryRun=true)
            => Portal.ManageTeamcenterWorkflow(action,target,libraryName,itemType,itemId,revisionId,localCacheOption,itemDetailsJson,revisionDetailsJson,customAttributesJson,confirmSave,dryRun);
    }
}
