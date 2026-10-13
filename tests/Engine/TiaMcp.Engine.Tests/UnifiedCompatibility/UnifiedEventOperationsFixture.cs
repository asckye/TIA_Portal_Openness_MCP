using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedEventOperations
    {

        internal static object? Find(object handlers,string eventType,string propertyName)
            => UnifiedPrimitiveCompatibility.Call<object?>("UnifiedEventOperations", "Find", handlers, eventType, propertyName);
        internal static (Type[] Types,object[] Values) Creation(object handlers,string eventType,string propertyName)
            => UnifiedPrimitiveCompatibility.Call<(Type[] Types,object[] Values)>("UnifiedEventOperations", "Creation", handlers, eventType, propertyName);
    }
}
