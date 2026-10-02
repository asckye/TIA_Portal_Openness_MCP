#if NETFRAMEWORK
// Compiler marker for records/init accessors, not a runtime or Siemens API shim.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
