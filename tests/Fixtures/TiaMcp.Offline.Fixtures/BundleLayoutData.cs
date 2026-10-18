using System.Collections.Generic;
namespace TiaOpenness.Shared.Tests
{
    public sealed class BundleLayoutTests
    {
        public static IEnumerable<object[]> Anchors()
        {
            foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
                yield return new object[] { "runtime/v" + key };
            yield return new object[] { "" };
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                foreach (string output in new[] { "bin", "bin-v20" })
                    yield return new object[] { "src/Engine/" + output + "/" + configuration + "/net48" };
                yield return new object[] { "src/FoundationHost/bin/" + configuration + "/net10.0" };
                yield return new object[] { "tests/Engine/TiaMcp.Engine.Harness/bin/" + configuration + "/net48" };
                yield return new object[] { "tests/FoundationHost/TiaMcp.FoundationHost.Tests/bin/" + configuration + "/net10.0" };
                yield return new object[] { "tests/Engine/TiaMcp.Engine.Tests/bin/" + configuration + "/net10.0" };
                yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/net10.0-windows" };
                yield return new object[] { "src/Studio/Gui/bin/" + configuration + "/net10.0-windows/bridge" };
                yield return new object[] { "src/Studio/Bridge/bin/" + configuration + "/net48" };
            }
            yield return new object[] { "runtime/studio" };
            yield return new object[] { "runtime/studio/bridge" };
        }

    }
}
