using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcSoftwareService
    {
        private readonly IEngineeringSession _session;

        public PlcSoftwareService(IEngineeringSession session) => _session = session;

        public string GetSoftwareTree(string softwarePath) => GetSoftwareTree(softwarePath, out _);

        public string GetSoftwareTree(string softwarePath, out JsonObject metadata)
        {
            var read = new PlcListingRead();
            metadata = new JsonObject();
            _session.Logger?.LogInformation("Getting software tree for path: {SoftwarePath}", softwarePath);

            if (_session.IsProjectNull())
            {
                // 无工程时明确拒绝，提示调用方先连接并打开工程。
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetSoftwareTree: no project is open. Call ConnectPortal + OpenProject "
                    + "(or AttachOpenProject) first.");
            }

            try
            {
                var plcSoftware = _session.ResolvePlcForListing(softwarePath);
                if (plcSoftware != null)
                {
                    StringBuilder sb = new();
                    sb.AppendLine($"{plcSoftware.Name} [PLC Software]");
                    
                    var ancestorStates = new List<bool>();
                    var sections = new List<Action>();
                    
                    var rootBlocks = read.Required(softwarePath + "/BlockGroup", () => plcSoftware.BlockGroup);
                    if (rootBlocks == null) throw new PortalException(PortalErrorCode.OpennessError, "PLC resolved but BlockGroup is unavailable.");
                    var hasBlocks = true;
                    var rootTypes = read.Required(softwarePath + "/TypeGroup", () => plcSoftware.TypeGroup);
                    var hasTypes = rootTypes != null;
                    
                    // Add blocks section
                    if (hasBlocks)
                    {
                        var blockGroup = rootBlocks;
                        if (blockGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeBlockGroup(sb, blockGroup, ancestorStates, "Program blocks", !hasTypes, read));
                        }
                    }
                    
                    // Add types section
                    if (hasTypes)
                    {
                        var typeGroup = rootTypes;
                        if (typeGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeTypeGroup(sb, typeGroup, ancestorStates, "PLC data types", true));
                        }
                    }
                    
                    
                    // Execute sections
                    for (int i = 0; i < sections.Count; i++)
                    {
                        sections[i]();
                    }

                    metadata = read.Metadata(softwarePath + ": user blocks and PLC types; system block groups and external sources excluded");
                    return sb.ToString();
                }
                else
                {
                    // 找不到软件必须失败，错误文本不能作为成功的树内容返回。
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"GetSoftwareTree: PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
                }
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "Error getting software tree for {SoftwarePath}", softwarePath);
                // 遍历失败必须抛出，不能返回看似完整的残缺树。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetSoftwareTree failed halfway through '{softwarePath}': {ex.Message}. "
                    + "The tree would have been INCOMPLETE, so it is not returned.", null, ex);
            }
        }

        private void GetSoftwareTreeBlockGroup(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection, PlcListingRead read)
        {
            sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{_session.GetTreePrefix(newAncestorStates, isLastBlock)}{block.Name} [{blockTypeName}{read.Optional<string?>(block.Name + "/Number", () => block.Number.ToString(), null) ?? "?"}, {read.Optional<string?>(block.Name + "/ProgrammingLanguage", () => block.ProgrammingLanguage.ToString(), null) ?? "unavailable"}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{_session.GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates, read);
            }
        }

        private void GetSoftwareTreeBlockGroupRecursive(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates, PlcListingRead read)
        {
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastBlock)}{block.Name} [{blockTypeName}{read.Optional<string?>(block.Name + "/Number", () => block.Number.ToString(), null) ?? "?"}, {read.Optional<string?>(block.Name + "/ProgrammingLanguage", () => block.ProgrammingLanguage.ToString(), null) ?? "unavailable"}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates, read);
            }
        }

        private void GetSoftwareTreeTypeGroup(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            
            sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName=="PlcStruct" ? "UDT": typeTypeName;

                sb.AppendLine($"{_session.GetTreePrefix(newAncestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{_session.GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }

        private void GetSoftwareTreeTypeGroupRecursive(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates)
        {
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName == "PlcStruct" ? "UDT" : typeTypeName;

                sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{_session.GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
    }
}
