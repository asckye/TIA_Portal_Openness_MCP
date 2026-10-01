// A test double in a separate assembly. No Siemens code or connection.
using System;
using System.Collections;
using System.Collections.Generic;

namespace DiagnosticFixture
{
    public sealed class Node : IDisposable
    {
        public static int Calls;
        public static readonly Exception Failure = new InvalidOperationException("secret-exception-body");
        public string Name { get { Calls++; return "secret-value"; } set { Calls++; } }
        public Node Child { get { Calls++; return new Node(); } }
        public IEnumerable<Node> Children { get { Calls++; return new Nodes(); } }
        public IEnumerable<Node> Counted { get { Calls++; return new CountedNodes(); } }
        public string Bad { get { Calls++; throw Failure; } }
        public Node() { Calls++; }
        public string this[int index] { get { Calls++; return index.ToString(); } set { Calls++; } }
        public void SetAttribute(string name, object value) { Calls++; }
        public object GetAttribute(string name) { Calls++; return "secret-value"; }
        public T Echo<T>(T value) { Calls++; return value; }
        public void Ref(ref int value, out string text) { Calls++; value += 3; text = "secret-ref-value"; }
        public static int Static(int value) { Calls++; return value + 1; }
        public static void ExitNow() { Environment.Exit(23); }
        public void Dispose() { Calls++; }
        public override string ToString() { throw new Exception("Diagnostic code must not call ToString"); }
        public override int GetHashCode() { throw new Exception("Diagnostic code must not call GetHashCode"); }
        public event EventHandler Changed { add { Calls++; } remove { Calls++; } }
    }
    public sealed class Nodes : IEnumerable<Node>
    {
        public static readonly List<string> Order = new List<string>();
        public IEnumerator<Node> GetEnumerator() { Order.Add("get"); return new Cursor(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Cursor : IEnumerator<Node>
        {
            int index;
            public Node Current { get { Order.Add("current"); return new Node(); } }
            object IEnumerator.Current => Current;
            public bool MoveNext() { Order.Add("move"); return ++index <= 2; }
            public void Reset() { Order.Add("reset"); index = 0; }
            public void Dispose() { Order.Add("dispose"); }
        }
    }
    public struct Value : IDisposable
    {
        public int Count;
        public void Dispose() { Count++; }
        public int Add(int n) { Count += n; return Count; }
    }
    public sealed class CountedNodes : IList<Node>
    {
        public static readonly List<string> Order = new List<string>();
        public int Count { get { Order.Add("count"); return 2; } }
        public bool IsReadOnly => true;
        public Node this[int index] { get { Order.Add("item"); return new Node(); } set => throw new NotSupportedException(); }
        public void CopyTo(Node[] array, int index) { Order.Add("copy"); array[index] = new Node(); array[index + 1] = new Node(); }
        public IEnumerator<Node> GetEnumerator() => throw new InvalidOperationException("Fast-path collection must not be enumerated in this test");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(Node item) => -1;
        public bool Contains(Node item) => false;
        public void Add(Node item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(Node item) => throw new NotSupportedException();
        public void Insert(int index, Node item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
    public sealed class BrokenNodes : IEnumerable<Node>
    {
        public static readonly List<string> Order = new List<string>();
        public IEnumerator<Node> GetEnumerator() => new Cursor();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Cursor : IEnumerator<Node>
        {
            public Node Current => throw new Exception("Current must not be read after failed MoveNext");
            object IEnumerator.Current => Current;
            public bool MoveNext() { Order.Add("move"); throw Node.Failure; }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose() { Order.Add("dispose"); }
        }
    }
}
