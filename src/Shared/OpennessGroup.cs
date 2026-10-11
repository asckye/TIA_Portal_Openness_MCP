using Siemens.Collaboration.Net;
using System.Threading.Tasks;
namespace TiaMcpServer.Siemens;
public static class OpennessGroup
{
        public static async Task<bool> EnsureMembership()
        {
            if (Api.Global.Openness().IsUserInGroup())
            {
                // user is in group
                return true;
            }
            else
            {
                return await Api.Global.Openness().AddUserToGroupAsync();
            }
        }
}
