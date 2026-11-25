using System.Xml.Linq;

namespace Siemens.Engineering
{
    public enum ImportOptions { None, Override }
    public sealed class MessageDataEntry { public string Text => "Import failed"; public string DetailText => "Siemens rejected the symbol declaration."; }
    public class EngineeringException : Exception
    {
        public MessageDataEntry MessageData => new();
        public MessageDataEntry[] DetailMessageData => Array.Empty<MessageDataEntry>();
    }
    public class EngineeringTargetInvocationException : EngineeringException { }
    public class EngineeringUserAbortException : EngineeringTargetInvocationException { }
    public class EngineeringObjectDisposedException : EngineeringException { }
    public class NonRecoverableException : Exception { }
}
namespace Siemens.Engineering.SW { public sealed class PlcSoftware { public string Name => "PLC"; } }
namespace Siemens.Engineering.SW.Tags
{
    public class NamedObjects<T> : List<T> where T : PlcObject, new()
    {
        internal Action<T>? OnCreate;
        internal bool Unreadable;
        public T? Find(string name) => Unreadable ? throw new IOException("Composition unavailable") : this.SingleOrDefault(t => t.Name == name);
        public T Create(string name, params string[] args)
        {
            var item = new T { Name = name };
            OnCreate?.Invoke(item);
            Add(item);
            return item;
        }
    }
    public class PlcObject { public string Name { get; set; } = ""; }
    public class PlcTagTableGroup { public PlcTagTableComposition TagTables { get; } = new(); }
    public class PlcTagTable : PlcObject
    {
        public NamedObjects<PlcObject> Tags { get; } = new();
        public NamedObjects<PlcObject> UserConstants { get; } = new();
    }
    public class PlcTagTableComposition : NamedObjects<PlcTagTable>
    {
        public IList<PlcTagTable> Import(FileInfo file, ImportOptions options) =>
            new[] { Create(XDocument.Load(file.FullName).Descendants("Name").First().Value) };
    }
}
