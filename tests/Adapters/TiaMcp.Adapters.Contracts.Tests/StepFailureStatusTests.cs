using System.Reflection;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class StepFailureStatusTests
{
    public static IEnumerable<object[]> Failures()
    {
        yield return new object[] { new PlcSoftwareException("PlcCode", "PLC refused."), "PlcCode" };
        yield return new object[] { new HardwareAddressingException("HardwareStatus", "Hardware refused."), "HardwareStatus" };
        yield return new object[] { AdapterPreconditionException.NotFound("Target absent.", "target"), "NotFound" };
        yield return new object[] { new FileNotFoundException("File absent."), "NotFound" };
        yield return new object[] { new DirectoryNotFoundException("Directory absent."), "NotFound" };
        yield return new object[] { new AdapterPreconditionException("Invalid target.", "target"), "InvalidParams" };
        yield return new object[] { new ArgumentException("Invalid target.", "target"), "InvalidParams" };
        yield return new object[] { new FormatException("Invalid format."), "InvalidParams" };
        yield return new object[] { new NotSupportedException("Service unavailable."), "NotSupportedOnVersion" };
        yield return new object[] { new AdapterPreconditionException("Offline required.", "target", false), "ReadOrWriteFailed" };
        yield return new object[] { new InvalidOperationException("Native operation failed."), "ReadOrWriteFailed" };
    }

    [Theory, MemberData(nameof(Failures))]
    public void Base_exception_determines_the_status(Exception error, string expected)
    {
        Assert.Equal(expected, StepFailureStatus.Of(error));
        Assert.Equal(expected, StepFailureStatus.Of(new TargetInvocationException(error)));
    }

    [Fact]
    public void Parameter_comes_from_the_base_exception()
    {
        foreach (Exception error in new Exception[] {
            new ArgumentException("Invalid target.", "target"), new AdapterPreconditionException("Invalid target.", "target"),
            AdapterPreconditionException.NotFound("Target absent.", "target") })
        {
            Assert.Equal("target", StepFailureStatus.Parameter(error));
            Assert.Equal("target", StepFailureStatus.Parameter(new TargetInvocationException(error)));
        }
        Assert.Null(StepFailureStatus.Parameter(new FormatException()));
        Assert.Null(StepFailureStatus.Parameter(new ArgumentException("No parameter.")));
    }

    [Fact]
    public void Already_exists_retains_the_declared_parameter_through_wrappers()
    {
        var error = new PlcSoftwareException("AlreadyExists", "Destination must be new.") { Data = { ["parameter"] = "destinationDirectory" } };
        Assert.Equal("AlreadyExists", StepFailureStatus.Of(error));
        Assert.Equal("destinationDirectory", StepFailureStatus.Parameter(error));
        Assert.Equal("destinationDirectory", StepFailureStatus.Parameter(new TargetInvocationException(error)));
        Assert.Null(StepFailureStatus.Parameter(new PlcSoftwareException("AlreadyExists", "No parameter.")));
    }

    [Fact]
    public void Not_found_precondition_is_not_an_argument()
    {
        var error = AdapterPreconditionException.NotFound("Target absent.", "target");
        Assert.False(error.IsArgument);
        Assert.Equal("not-found", error.Condition);
        Assert.Equal("target", error.ParamName);
        Assert.Equal(new AdapterPreconditionException("Target absent.", "target").Message, error.Message);
    }
    [Theory]
    [InlineData("NotFound", "NOT_FOUND")]
    [InlineData("InvalidParams", "INVALID_ARGUMENT")]
    [InlineData("AlreadyExists", "ALREADY_EXISTS")]
    [InlineData("NotSupportedOnVersion", "UNSUPPORTED_CAPABILITY")]
    [InlineData("InvalidState", "PRECONDITION_FAILED")]
    public void Optional_product_evidence_classifies_hardware_before_operation(string status, string expected)
    {
        var meta = new TiaMcp.Adapters.Native.Hmi.HmiObject();
        var error = new TargetInvocationException(new HardwareAddressingException(status, "Exact target refused."));
        TiaMcp.Adapters.Native.Options.OptionalProductEvidence.RecordFailure(meta, error);
        Assert.Equal(expected, meta["rejectionCode"]);
        Assert.Equal(true, meta["operationNotStarted"]);
        foreach (string field in new[] { "mayHaveChanged", "mayHaveWrittenFiles", "mayHaveExternalEffects", "operationStarted" })
        {
            var issued = new TiaMcp.Adapters.Native.Hmi.HmiObject { [field] = true };
            TiaMcp.Adapters.Native.Options.OptionalProductEvidence.RecordFailure(issued, error);
            Assert.Null(issued["rejectionCode"]);
            Assert.Null(issued["operationNotStarted"]);
            Assert.Equal(true, issued[field]);
        }
    }
}
