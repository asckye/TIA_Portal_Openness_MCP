using Siemens.Engineering;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    internal TiaPortal? CollaborationPortal => portal;
    internal IEngineeringServiceProvider? CollaborationProject => project;
}
