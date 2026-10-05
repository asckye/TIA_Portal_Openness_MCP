using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace TiaMcpServer.Tests.Shared
{
    // xUnit 2 has no dynamic Assert.Skip. Supply each recorded row's skip reason to its runner.
    // The project-local attribute names this discoverer's assembly because this source is linked.
    public sealed class CheckTheoryDiscoverer : IXunitTestCaseDiscoverer
    {
        private readonly IMessageSink diagnosticMessageSink;
        public CheckTheoryDiscoverer(IMessageSink diagnosticMessageSink) => this.diagnosticMessageSink = diagnosticMessageSink;

        public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
            ITestMethod testMethod, IAttributeInfo factAttribute)
        {
            yield return new CheckTheoryTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
                discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
        }
    }

    public sealed class CheckTheoryTestCase : XunitTheoryTestCase
    {
        [Obsolete("Called by the xUnit deserializer.")]
        public CheckTheoryTestCase() { }

        public CheckTheoryTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay display,
            TestMethodDisplayOptions options, ITestMethod testMethod)
            : base(diagnosticMessageSink, display, options, testMethod) { }

        public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
            object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
            new CheckTheoryRunner(this, DisplayName, SkipReason, constructorArguments, diagnosticMessageSink,
                messageBus, aggregator, cancellationTokenSource).RunAsync();
    }

    internal sealed class CheckTheoryRunner : XunitTheoryTestCaseRunner
    {
        internal CheckTheoryRunner(IXunitTestCase testCase, string displayName, string skipReason,
            object[] constructorArguments, IMessageSink diagnosticMessageSink, IMessageBus messageBus,
            ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
            : base(testCase, displayName, skipReason, constructorArguments, diagnosticMessageSink,
                messageBus, aggregator, cancellationTokenSource) { }

        protected override XunitTestRunner CreateTestRunner(ITest test, IMessageBus messageBus, Type testClass,
            object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments, string skipReason,
            IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
        {
            if (testMethodArguments.Length == 1 && testMethodArguments[0] is CheckRow row)
                skipReason = row.SkipReason ?? skipReason;
            return base.CreateTestRunner(test, messageBus, testClass, constructorArguments, testMethod,
                testMethodArguments, skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource);
        }
    }
}
