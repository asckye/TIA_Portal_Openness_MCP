using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private MasterCopy ExactMasterCopy(string libraryName,string path) {
            var parts=EngineeringGroupOperations.Parts(path);
            var folder=EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName),string.Join("/",parts.Take(parts.Length-1)),"MasterCopyFolder");
            return (MasterCopy)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(folder,"MasterCopies"),parts.Last()) ?? throw new InvalidOperationException("Exact master copy not found."));
        }
    }
}
