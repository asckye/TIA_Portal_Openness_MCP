// Portable serializer variant of the existing diagnostic runtime; shared by all eight adapters.
// Newtonsoft.Json preserves the actual net461 target; no Siemens reference or SDK stub.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace TiaMcpServer.ModelContextProtocol
{
    // Called by build-generated wrappers. NEVER read engineering properties, ToString,
    // GetHashCode, or argument/return contents here. Paths describe observed access lineage,
    // not an invented canonical engineering path. Weak keys do not retain native proxies.
    internal static class NativeCallDiagnostics
    {
        private sealed class Address
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal readonly string Path;
            internal Address(string path) { Path = path; }
        }
        private sealed class Span
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal string Correlation = "", Site = "", Member = "", Category = "", Type = "", Path = "", ObjectId = "";
            internal Span? Parent;
        }
        private static readonly ConditionalWeakTable<object, Address> Addresses = new ConditionalWeakTable<object, Address>();
        private sealed class NativeFlag { internal bool Value; }
        private static readonly ConditionalWeakTable<Type, NativeFlag> NativeTypes = new ConditionalWeakTable<Type, NativeFlag>();
        private static readonly AsyncLocal<Span?> Active = new AsyncLocal<Span?>();
        private static bool NativeType(Type? type) => type != null && NativeTypes.GetValue(type, t => new NativeFlag { Value = (t.Assembly.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.Ordinal) }).Value;
        private static bool Known(object? value) => value != null && (NativeType(value.GetType()) || Addresses.TryGetValue(value, out _));
        private static string Limit(string value) => value.Length <= 1024 ? value : value.Substring(0, 1024) + "\u2026";
        private static string Clean(string value) => Limit(value.Replace("\r", " ").Replace("\n", " ").Replace("\0", " "));
        private static string Leaf(string signature)
        {
            int at = signature.LastIndexOf("::", StringComparison.Ordinal);
            var name = at >= 0 ? signature.Substring(at + 2) : signature;
            int end = name.IndexOf('('); return end < 0 ? name : name.Substring(0, end);
        }
        private static bool SelectorMember(string name) => name == "Find" || name == "GetAttribute" || name == "SetAttribute" || name == "GetComposition";
        internal static object? Enter(string site, string member, string category, object? receiver, object? descriptor, object? selector)
        {
            try
            {
                var info = descriptor as MemberInfo;
                if (descriptor is Delegate callback) { receiver = callback.Target; info = callback.Method; }
                bool native = category == "direct" || Known(receiver) || NativeType(info?.DeclaringType) || descriptor is Type type && NativeType(type);
                if (!native) return null;
                if (info != null) member = (info.DeclaringType?.FullName ?? "") + "::" + info.Name;
                string owner = member.Split(new[] { "::" }, StringSplitOptions.None)[0];
                owner = owner.Substring(owner.LastIndexOf(' ') + 1);
                string typeName = receiver?.GetType().FullName ?? info?.DeclaringType?.FullName ?? owner;
                var address = receiver == null ? null : Addresses.GetValue(receiver, _ => new Address("/" + typeName));
                string path = address?.Path ?? "/" + typeName;
                string leaf = Leaf(member);
                // Only names identifying a selected object/attribute, never values/passwords/scripts.
                if (SelectorMember(leaf))
                {
                    string? name = selector as string;
                    if (selector is object[] array && array.Length > 0) name = array[0] as string;
                    if (name != null) path = Limit(path + "/" + leaf + "[" + Clean(name) + "]");
                }
                var span = new Span { Correlation = InvocationJournal.CorrelationId, Site = site, Member = member, Category = category,
                    Type = typeName, Path = path, ObjectId = address?.Id ?? "", Parent = Active.Value };
                Write(span, "BEFORE", null);
                Active.Value = span;
                return span;
            }
            catch { return null; } // Diagnostics must never change the original operation.
        }
        private static void Write(Span span, string phase, Exception? error)
        {
            var exceptions = new JArray();
            for (var current = error; current != null && exceptions.Count < 8; current = current.InnerException)
                exceptions.Add(new JObject { ["type"] = current.GetType().FullName, ["hresult"] = current.HResult });
            InvocationJournal.Write(span.Correlation, "native:" + span.Member, phase, span.Type, span.Path, new JObject {
                ["nativeCallId"] = span.Id, ["parentNativeCallId"] = span.Parent?.Id, ["callSite"] = span.Site,
                ["member"] = span.Member, ["dispatch"] = span.Category, ["objectId"] = span.ObjectId,
                ["pathKind"] = "observed-access-lineage", ["exceptionType"] = error?.GetType().FullName,
                ["hresult"] = error == null ? (int?)null : error.HResult, ["exceptionChain"] = exceptions });
        }
        internal static void Returned(object? token, object? result)
        {
            if (!(token is Span span)) return;
            try
            {
                if (result != null && !result.GetType().IsValueType && !(result is string) && !(result is Type) && !(result is MemberInfo) &&
                    (NativeType(result.GetType()) || result is IEnumerable || result is IEnumerator || result is IDisposable))
                    Addresses.GetValue(result, _ => new Address(Limit(span.Path + "/" + Leaf(span.Member))));
                Write(span, "RETURNED", null);
            }
            catch { }
            finally { Active.Value = span.Parent; }
        }
        internal static void Threw(object? token, Exception error)
        {
            if (!(token is Span span)) return;
            try { Write(span, "THREW", error); _ = PortalFailureClassifier.IsPortalProcessLost(error); }
            catch { }
            finally { Active.Value = span.Parent; }
        }
        private static T Call<T>(object receiver, string member, Func<T> action)
        {
            object? token = Enter("enumeration-adapter", member, "interface", receiver, null, null);
            try { T result = action(); Returned(token, result); return result; }
            catch (Exception ex) { Threw(token, ex); throw; }
        }
        // System.Linq and collection constructors enumerate outside our assembly. Wrap only
        // observed native inputs so every GetEnumerator/MoveNext/Current/Dispose is bracketed.
        internal static IEnumerable? Enumerate(IEnumerable? source)
        {
            try { return source == null || !Known(source) ? source : source is IList list ? new ListSequence(list) : source is ICollection collection ? new CollectionSequence(collection) : new Sequence(source); } catch { return source; }
        }
        internal static IEnumerable<T>? EnumerateGeneric<T>(IEnumerable<T>? source)
        {
            try { return source == null || !Known(source) ? source : source is IList<T> list ? new ListSequence<T>(list) : source is ICollection<T> collection ? new CollectionSequence<T>(collection) : new Sequence<T>(source); } catch { return source; }
        }
        // Cast<T> returns an already typed input verbatim without enumerating it.
        // Its next consumer is instrumented separately. Only the lazy fallback
        // needs an adapter around its captured non-generic source.
        internal static IEnumerable? EnumerateCast<T>(IEnumerable? source) => source is IEnumerable<T> ? source : Enumerate(source);
        private class Sequence : IEnumerable
        {
            private readonly IEnumerable source;
            internal Sequence(IEnumerable source) { this.source = source; }
            public IEnumerator GetEnumerator() => new Cursor(Call(source, "System.Collections.IEnumerable::GetEnumerator", source.GetEnumerator));
        }
        private class Sequence<T> : IEnumerable<T>
        {
            private readonly IEnumerable<T> source;
            internal Sequence(IEnumerable<T> source) { this.source = source; }
            public IEnumerator<T> GetEnumerator() => new Cursor<T>(Call(source, "System.Collections.Generic.IEnumerable::GetEnumerator", source.GetEnumerator));
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private class CollectionSequence<T> : Sequence<T>, ICollection<T>
        {
            protected readonly ICollection<T> collection;
            internal CollectionSequence(ICollection<T> source) : base(source) { collection = source; }
            public int Count => Call(collection, "System.Collections.Generic.ICollection::get_Count", () => collection.Count);
            public bool IsReadOnly => Call(collection, "System.Collections.Generic.ICollection::get_IsReadOnly", () => collection.IsReadOnly);
            public void CopyTo(T[] array, int index) => Call(collection, "System.Collections.Generic.ICollection::CopyTo", () => { collection.CopyTo(array, index); return true; });
            public void Add(T item) => Call(collection, "System.Collections.Generic.ICollection::Add", () => { collection.Add(item); return true; });
            public void Clear() => Call(collection, "System.Collections.Generic.ICollection::Clear", () => { collection.Clear(); return true; });
            public bool Contains(T item) => Call(collection, "System.Collections.Generic.ICollection::Contains", () => collection.Contains(item));
            public bool Remove(T item) => Call(collection, "System.Collections.Generic.ICollection::Remove", () => collection.Remove(item));
        }
        private sealed class ListSequence<T> : CollectionSequence<T>, IList<T>
        {
            private readonly IList<T> list;
            internal ListSequence(IList<T> source) : base(source) { list = source; }
            public T this[int index] { get => Call(list, "System.Collections.Generic.IList::get_Item", () => list[index]); set => Call(list, "System.Collections.Generic.IList::set_Item", () => { list[index] = value; return true; }); }
            public int IndexOf(T item) => Call(list, "System.Collections.Generic.IList::IndexOf", () => list.IndexOf(item));
            public void Insert(int index, T item) => Call(list, "System.Collections.Generic.IList::Insert", () => { list.Insert(index, item); return true; });
            public void RemoveAt(int index) => Call(list, "System.Collections.Generic.IList::RemoveAt", () => { list.RemoveAt(index); return true; });
        }
        private class CollectionSequence : Sequence, ICollection
        {
            protected readonly ICollection collection;
            internal CollectionSequence(ICollection source) : base(source) { collection = source; }
            public int Count => Call(collection, "System.Collections.ICollection::get_Count", () => collection.Count);
            public bool IsSynchronized => Call(collection, "System.Collections.ICollection::get_IsSynchronized", () => collection.IsSynchronized);
            public object SyncRoot => Call(collection, "System.Collections.ICollection::get_SyncRoot", () => collection.SyncRoot);
            public void CopyTo(Array array, int index) => Call(collection, "System.Collections.ICollection::CopyTo", () => { collection.CopyTo(array, index); return true; });
        }
        private sealed class ListSequence : CollectionSequence, IList
        {
            private readonly IList list;
            internal ListSequence(IList source) : base(source) { list = source; }
            public object? this[int index] { get => Call(list, "System.Collections.IList::get_Item", () => list[index]); set => Call(list, "System.Collections.IList::set_Item", () => { list[index] = value; return true; }); }
            public bool IsFixedSize => Call(list, "System.Collections.IList::get_IsFixedSize", () => list.IsFixedSize);
            public bool IsReadOnly => Call(list, "System.Collections.IList::get_IsReadOnly", () => list.IsReadOnly);
            public int Add(object? item) => Call(list, "System.Collections.IList::Add", () => list.Add(item));
            public void Clear() => Call(list, "System.Collections.IList::Clear", () => { list.Clear(); return true; });
            public bool Contains(object? item) => Call(list, "System.Collections.IList::Contains", () => list.Contains(item));
            public int IndexOf(object? item) => Call(list, "System.Collections.IList::IndexOf", () => list.IndexOf(item));
            public void Insert(int index, object? item) => Call(list, "System.Collections.IList::Insert", () => { list.Insert(index, item); return true; });
            public void Remove(object? item) => Call(list, "System.Collections.IList::Remove", () => { list.Remove(item); return true; });
            public void RemoveAt(int index) => Call(list, "System.Collections.IList::RemoveAt", () => { list.RemoveAt(index); return true; });
        }
        private sealed class Cursor : IEnumerator, IDisposable
        {
            private readonly IEnumerator source;
            internal Cursor(IEnumerator source) { this.source = source; }
            public object Current => Call(source, "System.Collections.IEnumerator::get_Current", () => source.Current);
            public bool MoveNext() => Call(source, "System.Collections.IEnumerator::MoveNext", source.MoveNext);
            public void Reset() => Call(source, "System.Collections.IEnumerator::Reset", () => { source.Reset(); return true; });
            public void Dispose() { if (source is IDisposable d) Call(source, "System.IDisposable::Dispose", () => { d.Dispose(); return true; }); }
        }
        private sealed class Cursor<T> : IEnumerator<T>
        {
            private readonly IEnumerator<T> source;
            internal Cursor(IEnumerator<T> source) { this.source = source; }
            public T Current => Call(source, "System.Collections.Generic.IEnumerator::get_Current", () => source.Current);
            object? IEnumerator.Current => Current;
            public bool MoveNext() => Call(source, "System.Collections.IEnumerator::MoveNext", source.MoveNext);
            public void Reset() => Call(source, "System.Collections.IEnumerator::Reset", () => { source.Reset(); return true; });
            public void Dispose() => Call(source, "System.IDisposable::Dispose", () => { source.Dispose(); return true; });
        }
    }
}
