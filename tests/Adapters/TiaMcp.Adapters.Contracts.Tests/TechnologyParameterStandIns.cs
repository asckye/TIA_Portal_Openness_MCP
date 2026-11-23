using Siemens.Engineering.SW.TechnologicalObjects;

namespace Siemens.Engineering
{
    public enum EngineeringAttributeAccessMode { Read, ReadWrite }
}

namespace Siemens.Engineering.SW.TechnologicalObjects
{
    // Supply the SDK call for this port while preserving the optional metadata surface used by the reflection tests.
    internal static class TechnologyParameterMetadata
    {
        internal static IEnumerable<EngineeringAttributeInfo> GetAttributeInfos(this IEngineeringObject parameter)
            => ((TechnologicalParameter)parameter).GetAttributeInfos();
    }

    public sealed class TechnologicalInstanceDBGroup
    {
        public string Name { get; set; } = "";
        public TechnologicalInstanceDBComposition TechnologicalObjects { get; } = new();
    }

    public sealed class TechnologicalInstanceDBComposition : List<TechnologicalInstanceDB>
    {
        public TechnologicalInstanceDB? Find(string name) => this.SingleOrDefault(db => db.Name == name);
        public TechnologicalInstanceDB Create(string name, string typeIdentifier, Version version)
        {
            var db = new TechnologicalInstanceDB { Name = name };
            Add(db);
            return db;
        }
    }

    public sealed class TechnologicalInstanceDB
    {
        public string Name { get; init; } = "";
        public TechnologicalParameterComposition Parameters { get; } = new();
    }

    public sealed class TechnologicalParameterComposition : List<TechnologicalParameter>
    {
        public TechnologicalParameter? Find(string name) => this.SingleOrDefault(parameter => parameter.Name == name);
    }

    public sealed class TechnologicalParameter : IEngineeringObject
    {
        public string Name { get; init; } = "";
        public EngineeringAttributeAccessMode? ValueAccess { get; init; }
        public bool RejectValueWrite { get; init; }
        public int MetadataReads { get; private set; }
        public int SetCalls { get; private set; }
        private object value = 1.0f;
        public object Value
        {
            get => value;
            set
            {
                SetCalls++;
                if (RejectValueWrite || ValueAccess == EngineeringAttributeAccessMode.Read) throw new InvalidOperationException("Value is read-only.");
                this.value = value;
            }
        }

        public IEnumerable<EngineeringAttributeInfo> GetAttributeInfos()
        {
            MetadataReads++;
            var access = ValueAccess ?? throw new InvalidOperationException("Parameter metadata unavailable.");
            return [new EngineeringAttributeInfo { Name = "Value", AccessMode = access.ToString() }];
        }

        public object GetAttribute(string name) => name == "Value" ? Value : throw new ArgumentException(name);
        public void SetAttribute(string name, object? value)
        {
            if (name != "Value") throw new ArgumentException(name);
            Value = value!;
        }
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        internal bool MutationStarted { get; private set; }
        private static Dictionary<string, object?> TechnologyObjectRow(TechnologicalInstanceDB db) => new() { ["name"] = db.Name };
        private static Dictionary<string, object?> ParameterRow(TechnologicalParameter parameter)
            => new() { ["name"] = parameter.Name, ["value"] = parameter.Value };
        private static object TypedMotionView(TechnologicalInstanceDB db) => throw new NotSupportedException();
    }
}
