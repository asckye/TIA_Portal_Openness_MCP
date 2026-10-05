using System;
using System.Collections;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // The typed Sivarc project service anchors the generic readers for arbitrary sub-paths.
        private object ExactSiVArcRoot(string category) => Family(category).Anchor(RequireSivarc());
    }
}
