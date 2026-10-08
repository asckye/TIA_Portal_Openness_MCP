using TiaMcpServer.Siemens.Services;

namespace TiaMcp.Engine.Tests
{
    internal static class HmiToolFixture
    {
        internal static FakeHmiToolSession Session { get; private set; } = new FakeHmiToolSession();
        internal static HmiInspectionService HmiInspection { get; private set; } = new HmiInspectionService(Session);
        internal static MigrationReadService MigrationRead { get; private set; } = new MigrationReadService(Session);
        internal static RuntimeSettingsService RuntimeSettings { get; private set; } = new RuntimeSettingsService(Session);
        internal static GraphicSelectionService GraphicSelection { get; private set; } = new GraphicSelectionService(Session, MigrationRead);
        internal static GlobalScriptEditService GlobalScriptEdit { get; private set; } = new GlobalScriptEditService(Session);

        internal static void Configure(FakeHmiToolSession session)
        {
            Session = session;
            HmiInspection = new HmiInspectionService(session);
            MigrationRead = new MigrationReadService(session);
            RuntimeSettings = new RuntimeSettingsService(session);
            GraphicSelection = new GraphicSelectionService(session, MigrationRead);
            GlobalScriptEdit = new GlobalScriptEditService(session);
            ToolBridgeFixture.Configure();
        }
    }
}
