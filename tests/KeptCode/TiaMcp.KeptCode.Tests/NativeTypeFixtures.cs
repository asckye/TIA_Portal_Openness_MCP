using System;
using System.Collections.Generic;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class ScreenItemContractFixture
    {
        internal static int GetterCalls;
        public double Width { get { GetterCalls++; throw new InvalidOperationException(); } set { } }
        public int ReadOnly => throw new InvalidOperationException();
        public string Name { get; set; } = "";
        public FontPart Font => throw new InvalidOperationException();
        public FontPart Parent => throw new InvalidOperationException();
        public FontPart[] Items => throw new InvalidOperationException();
        public MultilingualText Text => throw new InvalidOperationException();
        public sealed class FontPart { public double Size { get; set; } public int ReadOnly => 0; }
        public sealed class MultilingualText { }
    }
}
namespace Siemens.Engineering
{
    internal interface IEngineeringObject
    {
        public List<TiaMcp.Engine.Tests.GraphicSelectionTests.Info> GetAttributeInfos();
        public List<TiaMcp.Engine.Tests.GraphicSelectionTests.Info> GetCompositionInfos();
        public object GetAttribute(string name);
    }
}
namespace TiaMcp.Engine.Tests
{
    internal static class GraphicSelectionTests
    {
        public sealed class Info
        {
            public string Name { get; set; } = "";
            public string AccessMode { get; set; } = "Read";
        }
    }
}
