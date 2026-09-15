using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Helper
    {
        public static List<Attribute> GetAttributeList(IEngineeringObject obj)
        {
            var attributes = new List<Attribute>();

            if (obj != null)
            {
                foreach (var attr in obj.GetAttributeInfos())
                {
                    object value;
                    try { value = obj.GetAttribute(attr.Name); }
                    catch (Exception ex) { value = $"<unreadable: {ex.GetType().Name}>"; }
                    attributes.Add(new Attribute
                    {
                        Name = attr.Name,
                        Value = ToSerializableValue(value),
                        AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode)
                    });
                }
            }

            return attributes;
        }

        // Convert non-primitive attribute values to a safe representation to avoid JSON cycle errors
        // (e.g. CultureInfo.Parent.Parent.Parent... chain triggers JsonException at depth 64).
        private static object ToSerializableValue(object value)
        {
            if (value == null) return null!;
            var t = value.GetType();
            if (t.IsPrimitive || value is string || value is decimal || value is DateTime || value is TimeSpan || value is Guid || t.IsEnum)
                return value;
            // Anything else (CultureInfo, Siemens engineering objects, etc.) → string form
            try { return value.ToString(); }
            catch { return $"<{t.Name}>"; }
        }

        internal static ResponseBlockInfo ReadBlockInfo(PlcBlock block, PlcListingRead read, string groupPath = "")
        {
            var name = read.Required("Block/Name", () => block.Name);
            var path = groupPath + "/" + name;
            var result = new ResponseBlockInfo
            {
                Name = name, TypeName = block.GetType().Name,
                Namespace = read.Optional<string?>(path + "/Namespace", () => block.Namespace, null),
                ProgrammingLanguage = read.Optional<string?>(path + "/ProgrammingLanguage", () => block.ProgrammingLanguage.ToString(), null),
                MemoryLayout = read.Optional<string?>(path + "/MemoryLayout", () => block.MemoryLayout.ToString(), null),
                IsConsistent = read.Optional<bool?>(path + "/IsConsistent", () => block.IsConsistent, null),
                HeaderName = read.Optional<string?>(path + "/HeaderName", () => block.HeaderName, null),
                ModifiedDate = read.Optional<DateTime?>(path + "/ModifiedDate", () => block.ModifiedDate, null),
                IsKnowHowProtected = read.Optional<bool?>(path + "/IsKnowHowProtected", () => block.IsKnowHowProtected, null),
                Description = read.Optional<string?>(path + "/Description", () => block.ToString(), null)
            };
            var attributes = new List<Attribute>();
            read.Optional<object?>(path + "/GetAttributeInfos", () =>
            {
                foreach (var attr in block.GetAttributeInfos())
                    attributes.Add(new Attribute { Name = attr.Name,
                        Value = read.Optional<object?>(path + "/Attributes/" + attr.Name,
                            () => ToSerializableValue(block.GetAttribute(attr.Name)), null),
                        AccessMode = attr.AccessMode.ToString() });
                return null;
            }, null);
            result.Attributes = attributes;
            return result;
        }

        internal static ResponseTypeInfo ReadTypeInfo(PlcType type, PlcListingRead read)
        {
            var name = read.Required("Type/Name", () => type.Name);
            var result = new ResponseTypeInfo
            {
                Name = name, TypeName = type.GetType().Name,
                Namespace = read.Optional<string?>(name + "/Namespace", () => type.Namespace, null),
                IsConsistent = read.Optional<bool?>(name + "/IsConsistent", () => type.IsConsistent, null),
                ModifiedDate = read.Optional<DateTime?>(name + "/ModifiedDate", () => type.ModifiedDate, null),
                IsKnowHowProtected = read.Optional<bool?>(name + "/IsKnowHowProtected", () => type.IsKnowHowProtected, null),
                Description = read.Optional<string?>(name + "/Description", () => type.ToString(), null)
            };
            var attributes = new List<Attribute>();
            read.Optional<object?>(name + "/GetAttributeInfos", () =>
            {
                foreach (var attr in type.GetAttributeInfos())
                    attributes.Add(new Attribute { Name = attr.Name,
                        Value = read.Optional<object?>(name + "/Attributes/" + attr.Name,
                            () => ToSerializableValue(type.GetAttribute(attr.Name)), null),
                        AccessMode = attr.AccessMode.ToString() });
                return null;
            }, null);
            result.Attributes = attributes;
            return result;
        }

        public static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group)
            => BuildBlockHierarchy(group, new PlcListingRead());

        internal static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group, PlcListingRead read, string parentPath = "")
        {
            var groupInfo = new BlockGroupInfo { Name = read.Required("BlockGroup/Name", () => group.Name) };
            var path = parentPath + "/" + groupInfo.Name;
            var blockList = new List<ResponseBlockInfo>();
            foreach (var block in group.Blocks) blockList.Add(ReadBlockInfo(block, read, path));
            groupInfo.Blocks = blockList;
            var groupList = new List<BlockGroupInfo>();
            foreach (var child in group.Groups) groupList.Add(BuildBlockHierarchy(child, read, path));
            groupInfo.Groups = groupList;
            return groupInfo;
        }
    }
}
