using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactTechnology(string softwarePath,string objectPath,bool writing)
        {
            var plc=ExactPlcForEngineering(softwarePath,writing);var parts=EngineeringGroupOperations.Parts(objectPath);
            var group=EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup,string.Join("/",parts.Take(parts.Length-1)));
            return EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group,"TechnologicalObjects"),parts.Last()) ?? throw new InvalidOperationException("Exact technology object not found.");
        }


    }
}
