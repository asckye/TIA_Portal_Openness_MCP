#nullable disable
using System;
using global::Siemens.Engineering.SW;
using global::Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaMcp.Adapters
{
    public static partial class WatchTechnologyPrimitives
    {
        public static TechnologicalInstanceDBGroup TechnologyGroup(PlcSoftware software) => software.TechnologicalObjectGroup;
        public static TechnologicalInstanceDBComposition Objects(TechnologicalInstanceDBGroup group) => group.TechnologicalObjects;
        public static string Name(TechnologicalInstanceDB item) => item.Name;
        public static string Name(TechnologicalInstanceDBGroup group) => group.Name;
#if PLC_TECH_GROUP_READ
        public static TechnologicalInstanceDBUserGroupComposition Groups(TechnologicalInstanceDBGroup group) => group.Groups;
        public static string Name(TechnologicalInstanceDBUserGroup group) => group.Name;
#endif
        public static TechnologicalInstanceDB Create(TechnologicalInstanceDBComposition objects, string name, string typeIdentifier, Version version) => objects
            .Create(name, typeIdentifier, version);
        public static TechnologicalInstanceDB Find(TechnologicalInstanceDBComposition objects, string name) => objects.Find(name);
        public static TechnologicalParameter Find(TechnologicalParameterComposition parameters, string name) => parameters.Find(name);
        public static TechnologicalParameterComposition Parameters(TechnologicalInstanceDB item) => item.Parameters;
        public static int Count(TechnologicalInstanceDBComposition objects) => objects.Count;
#if PLC_TECH_GROUP_READ
        public static int Count(TechnologicalInstanceDBUserGroupComposition groups) => groups.Count;
#endif
    }
}
