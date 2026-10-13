using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class MigrationReadService : UnifiedPortService
    {
        public MigrationReadService() { }
        internal MigrationReadService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ListUnifiedGlobalScripts(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "ListUnifiedGlobalScripts", SoftwarePath = softwarePath, ExpectedProject = expectedProject, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReadUnifiedGlobalScript(string softwarePath, string expectedProject, string moduleName, string cursor = "", int pageSize = 50, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedGlobalScript", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ModuleName = moduleName, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReadUnifiedTagDefinitions(string softwarePath, string expectedProject, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "ListUnifiedTagDefinitions", SoftwarePath = softwarePath, ExpectedProject = expectedProject, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReadUnifiedScreenBranch(string softwarePath, string expectedProject, string screenPath, string itemName = "", string branchJson = "[]", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedScreenBranch", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ScreenPath = screenPath, ItemName = itemName, BranchJson = branchJson, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReadUnifiedLibraryType(string softwarePath, string expectedProject, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedLibraryType", SoftwarePath = softwarePath, ExpectedProject = expectedProject, TypePath = typePath, Version = version, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ListUnifiedLibraryFolder(string softwarePath, string expectedProject, string folderPath = "/", string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "ListUnifiedLibraryFolderEntries", SoftwarePath = softwarePath, ExpectedProject = expectedProject, FolderPath = folderPath, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReadUnifiedFaceplateInstance(string softwarePath, string expectedProject, string screenPath, string itemName, string typePath, string version, string cursor = "", int pageSize = 100, int budgetMs = 5000)
            => Invoke(new UnifiedRequest { Operation = "GetUnifiedFaceplateInstance", SoftwarePath = softwarePath, ExpectedProject = expectedProject, ScreenPath = screenPath, ItemName = itemName, TypePath = typePath, Version = version, Cursor = cursor, PageSize = pageSize, BudgetMs = budgetMs }, true);
        public ResponseMessage ReleaseUnifiedReadCursor(string cursor)
            => Invoke(new UnifiedRequest { Operation = "ReleaseUnifiedReadCursor", Cursor = cursor }, true);
    }
}
