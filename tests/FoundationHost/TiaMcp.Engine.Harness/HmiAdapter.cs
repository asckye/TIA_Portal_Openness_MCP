using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace TiaMcpServer.Siemens
{
    internal static class HmiScreenTraversal
    {
        internal static Type EngineType = null!;
        private static object? Invoke(string method, params object[] args)
        {
            try { return EngineType.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        internal static List<string> ListNames(object root) => (List<string>)Invoke("ListNames", root)!;
        internal static object? FindByName(object root, string name) => Invoke("FindByName", root, name);
    }
}
