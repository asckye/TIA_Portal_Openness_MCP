using System.Xml;
using Microsoft.TestPlatform.Extensions.BlameDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;

namespace HangFixture;

// Only the planted-hang runs select this collector. Blame.Initialize arms the SDK's real inactivity timer.
[DataCollectorFriendlyName("TestStartBlame")]
[DataCollectorTypeUri("datacollector://TiaMcp/SuiteHang/TestStartBlame")]
public sealed class TestStartBlameCollector : BlameCollector
{
    public override void Initialize(XmlElement? configurationElement, DataCollectionEvents events, DataCollectionSink dataSink,
        DataCollectionLogger logger, DataCollectionEnvironmentContext? environmentContext)
    {
        var replay = new RecordedEvents();
        replay.OnFirstTestStart(events, () => base.Initialize(configurationElement, replay, dataSink, logger, environmentContext));
    }

    private sealed class RecordedEvents : DataCollectionEvents
    {
        public override event EventHandler<TestHostLaunchedEventArgs>? TestHostLaunched;
        public override event EventHandler<SessionStartEventArgs>? SessionStart;
        public override event EventHandler<SessionEndEventArgs>? SessionEnd;
        public override event EventHandler<TestCaseStartEventArgs>? TestCaseStart;
        public override event EventHandler<TestCaseEndEventArgs>? TestCaseEnd;

        internal void OnFirstTestStart(DataCollectionEvents source, Action initialize)
        {
            var sync = new object();
            SessionStartEventArgs? session = null;
            TestHostLaunchedEventArgs? host = null;
            TestCaseStartEventArgs? start = null;
            bool armed = false;
            // VSTest delivers host and test-case notifications on different threads.
            void ForwardStart()
            {
                if (start == null || host == null || session == null) return;
                if (!armed)
                {
                    initialize();
                    SessionStart?.Invoke(this, session);
                    TestHostLaunched?.Invoke(this, host);
                    armed = true;
                }
                TestCaseStart?.Invoke(this, start);
                File.WriteAllText(Environment.GetEnvironmentVariable("TIA_MCP_PLANTED_START")!, start.TestCaseName);
                start = null;
            }
            source.SessionStart += (_, e) => { lock (sync) { session = e; ForwardStart(); } };
            source.TestHostLaunched += (_, e) => { lock (sync) { host = e; ForwardStart(); } };
            source.TestCaseStart += (_, e) => { lock (sync) { start = e; ForwardStart(); } };
            source.TestCaseEnd += (_, e) => TestCaseEnd?.Invoke(this, e);
            source.SessionEnd += (_, e) => SessionEnd?.Invoke(this, e);
        }
    }
}
