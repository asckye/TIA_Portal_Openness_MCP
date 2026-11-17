using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // Path resolution is split out so offline worker stand-ins exercise the native session methods.
        private sealed partial class OrganisationSession
        {
            public PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath)
            {
                object root = engine.ReadSelection(path).Value.TypeGroup;
                foreach (var part in PlcObjectPath.Parts(groupPath, true)) {
                    var next = PlcGroupOperations.Find(PlcGroupOperations.Get(root, "Groups"), part); if (next == null) return null; root = next; }
                return (PlcTypeGroup)root;
            }
            public string GetPlcBlockGroupPath(PlcBlockGroup group)
            {
                var names = new List<string>(); object? value = group;
                while (value is PlcBlockUserGroup child) { names.Insert(0, child.Name); value = child.Parent; }
                return PlcObjectPath.Format(names);
            }
            public PlcBlock? GetBlock(string path, string blockPath)
            {
                var parts = PlcObjectPath.Parts(blockPath, parameter: "blockPath"); var root = engine.ReadSelection(path).Value.BlockGroup;
                return FindBlock(root, parts);
            }
            private static PlcBlock? FindBlock(PlcBlockGroup root, string[] parts)
            {
                var group = FindGroup(root, parts.Take(parts.Length - 1)) as PlcBlockGroup;
                return group?.Blocks.Find(parts.Last());
            }
            private static object? FindGroup(object root, IEnumerable<string> parts)
            {
                object? current = root;
                foreach (var part in parts)
                {
                    current = PlcGroupOperations.Find(PlcGroupOperations.Get(current, "Groups"), part);
                    if (current == null) return null;
                }
                return current;
            }
            public PlcBlock GetBlockForRead(string path, string blockPath)
            {
                var parts = PlcObjectPath.Parts(blockPath, parameter: "blockPath");
                var root = engine.ReadSelection(path).Value.BlockGroup;
                var exact = FindBlock(root, parts);
                if (exact != null) return exact;
                var matches = new Dictionary<string, PlcBlock>(StringComparer.Ordinal);
                for (int split = 0; split < parts.Length - 1; split++)
                {
                    var group = FindGroup(root, parts.Take(split)) as PlcBlockGroup;
                    var block = group?.Blocks.Find(string.Join("/", parts.Skip(split)));
                    if (block != null) matches[GetBlockPath(block)] = block;
                }
                if (matches.Count > 1) throw new AdapterPreconditionException(
                    "Ambiguous raw block path; use one canonical encoded path: " + string.Join(", ", matches.Keys.OrderBy(value => value,
                        StringComparer.Ordinal)), "blockPath");
                return matches.Values.SingleOrDefault() ?? throw AdapterPreconditionException.NotFound(
                    "Block not found at exact encoded path or unique raw name: " + blockPath, "blockPath");
            }
            public PlcType? GetType(string path, string typePath)
            {
                var parts = PlcObjectPath.Parts(typePath, parameter: "typePath"); var root = engine.ReadSelection(path).Value.TypeGroup;
                var group = FindGroup(root, parts.Take(parts.Length - 1)) as PlcTypeGroup;
                return group?.Types.Find(parts.Last());
            }
            public string GetBlockPath(PlcBlock block)
            {
                var names = new List<string> { block.Name }; object? parent = block.Parent;
                while (parent is PlcBlockUserGroup group) { names.Insert(0, group.Name); parent = group.Parent; }
                return PlcObjectPath.Format(names);
            }
            public PlcBlockGroup? GetPlcBlockGroupByPath(string path, string groupPath)
            {
                object current = engine.ReadSelection(path).Value.BlockGroup;
                foreach (var segment in PlcObjectPath.Parts(groupPath, true))
                { var next = PlcGroupOperations.Find(PlcGroupOperations.Get(current, "Groups"), segment); if (next == null) return null; current = next; }
                return (PlcBlockGroup)current;
            }
        }
    }
}
