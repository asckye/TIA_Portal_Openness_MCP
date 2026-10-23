using ModelContextProtocol.Protocol;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ClassicHmiFoldersTools
    {
        private readonly ClassicHmiFoldersService _classicHmiFolders;

        public ClassicHmiFoldersTools(ClassicHmiFoldersService service) => _classicHmiFolders = service;

        [McpServerTool(Name="GetClassicHmiScreenTree"), Description("[L2][HMI-Classic][READ] Typed folder tree of a classic (non-Unified) WinCC HMI device: ScreenSystemFolder / ScreenUserFolder (Screens, Folders), ScreenPopupSystemFolder / ScreenPopupUserFolder (ScreenPopups), ScreenTemplateSystemFolder / ScreenTemplateUserFolder (ScreenTemplates), ScreenSlideinSystemFolder (ScreenSlidein by SlideinType) plus ScreenOverview / ScreenGlobalElements presence. kind all/screens/popups/templates/slideins, optional folderPath under the family root, recursive to maxDepth; names only (first 500 per folder). Unified targets are refused. No modification.")]
        public CallToolResult GetClassicHmiScreenTree(
            string softwarePath,
            [Description("kind: all | screens | popups | templates | slideins.")] string kind="all",
            string folderPath="",
            int maxDepth=4)
            => ClassicMotionToolContract.Invoke("GetClassicHmiScreenTree", true, false,
                () => _classicHmiFolders.ReadClassicHmiScreenTree(softwarePath,kind,folderPath,maxDepth));
        [McpServerTool(Name="ManageClassicHmiScreenObject"), Description("[L2][HMI-Classic][WRITE] Classic WinCC screen objects beyond plain screens: objectKind popup (ScreenPopup: read / export / delete, objectPath folder/.../name), template (ScreenTemplate: read / export / delete), slidein (ScreenSlidein addressed by SlideinType Top/Bottom/Left/Right: read / export; no native Delete), overview (ScreenOverview: read / export / import) and globalElements (ScreenGlobalElements: read / export / import). export defaults to WithDefaults and writes a NEW absolute XML file and hashes it; import (objectPath = target folder for popups / templates, importOptions None/Override) uses ScreenPopupComposition / ScreenTemplateComposition / ScreenSlideinComposition.Import or HmiTarget.ImportScreenOverview / ImportScreenGlobalElements and returns the imported names. Deletion and Override import need confirmDelete=true besides dryRun=false; every delete is verified by Find. No save/compile/download; Plain screen and tag-table deletion and single-tag XML exchange are outside the 4.0 tool surface; use ExportHmiScreen for classic screen content. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiScreenObject(
            string softwarePath,
            [Description("objectKind: popup | template | slidein | overview | globalElements.")] string objectKind,
            string objectPath="",
            [Description("action: the operation to perform - read | export | import | delete.")] string action="read",
            string filePath="",
            [Description("importOptions: import option - None | Override.")] string importOptions="None",
            bool confirmDelete=false,
            bool dryRun=true,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => ClassicMotionToolContract.Invoke("ManageClassicHmiScreenObject", dryRun || action == "read", action != "read",
                () => _classicHmiFolders.ManageClassicHmiScreenObject(softwarePath,objectKind,objectPath,action,filePath,importOptions,confirmDelete,dryRun,exportOptions));
        [McpServerTool(Name="ManageClassicHmiFolder"), Description("[L2][HMI-Classic][WRITE] Classic WinCC user folders of one family: folderKind screens (ScreenUserFolder), popups (ScreenPopupUserFolder), templates (ScreenTemplateUserFolder), tags (TagUserFolder; the TagSystemFolder row also names the DefaultTagTable) or scripts (VBScriptUserFolder). read returns the typed folder row (child names, one nested level); create adds newName under folderPath (empty = system folder) through the typed Folders.Create and reads it back; delete removes the empty user folder at folderPath (system folders and nonempty folders refused, confirmDelete=true). Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiFolder(
            string softwarePath,
            [Description("folderKind: screens | popups | templates | tags | scripts.")] string folderKind,
            string folderPath="",
            [Description("action: the operation to perform - read | create | delete.")] string action="read",
            string newName="",
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiFolder", dryRun || action == "read", action != "read",
                () => _classicHmiFolders.ManageClassicHmiFolder(softwarePath,folderKind,folderPath,action,newName,confirmDelete,dryRun));
        [McpServerTool(Name="ManageClassicHmiGraphic"), Description("[L2][HMI-Classic][WRITE] Project multilingual graphics through project.Graphics (V14 SP1-V20) or project.GetService<GraphicsProvider>() (V21) (MultiLingualGraphic): list, read, export (MultiLingualGraphic.Export writes a NEW XML file plus the image files beside it, per language with the 'default' suffix), import (MultiLingualGraphicComposition.Import, importOptions None/Override; graphics referenced by relative paths) and delete (confirmDelete=true, verified by Find). Available on all eight releases: project.Graphics on V14 SP1-V20, project GraphicsProvider on V21. softwarePath is retained for compatibility; graphics are project-wide. Default dryRun=true; no save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiGraphic(
            string softwarePath,
            [Description("action: the operation to perform - list | read | export | import | delete.")] string action="list",
            string name="",
            string filePath="",
            [Description("importOptions: import option - None | Override.")] string importOptions="None",
            bool confirmDelete=false,
            bool dryRun=true,
            [Description("exportOptions: None | WithDefaults (default) | WithReadOnly.")] string exportOptions = "WithDefaults")
            => ClassicMotionToolContract.Invoke("ManageClassicHmiGraphic", dryRun || action == "read" || action == "list", action != "read" && action != "list",
                () => _classicHmiFolders.ManageClassicHmiGraphic(softwarePath,action,name,filePath,importOptions,confirmDelete,dryRun,exportOptions));
    }
}
