using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    internal static class McpLocalProcessTests
    {

        internal static int Child(string mode)
        {
            if (mode == "sleep") { System.Threading.Thread.Sleep(10000); return 0; }
            Console.Write(new string('o', 100000));
            Console.Error.Write(new string('e', 100000));
            return 7;
        }
    }
}
