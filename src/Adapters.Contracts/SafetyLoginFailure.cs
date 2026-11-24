using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts;

// Called on the native owner thread, after the caller has observed a logged-off session.
public sealed class SafetyLoginFailure
{
    public const string Phase = "safety-offline-login";
    public const string CompileRefusal = "Safety offline login failed; nothing was compiled";
    public string NativeExceptionType { get; }
    public bool? LoggedOn { get; }
    public bool LogoffFailed { get; }
    public bool ConnectionLost { get; }

    private SafetyLoginFailure(string nativeExceptionType, bool? loggedOn, bool logoffFailed, bool connectionLost = false)
    {
        NativeExceptionType = nativeExceptionType;
        LoggedOn = loggedOn;
        LogoffFailed = logoffFailed;
        ConnectionLost = connectionLost;
    }

    public static SafetyLoginFailure? Attempt(Action login, Func<bool> isLoggedOn, Action logoff, bool reportConnectionLoss = false)
    {
        try { login(); return null; }
        catch (Exception loginError) when (!HmiReadPolicy.ConnectionUnavailable(loginError))
        {
            string type = loginError.GetType().FullName!;
            bool? loggedOn;
            try { loggedOn = isLoggedOn(); }
            catch (Exception error) when (reportConnectionLoss || !HmiReadPolicy.ConnectionUnavailable(error))
            {
                if (reportConnectionLoss && HmiReadPolicy.ConnectionUnavailable(error)) return new SafetyLoginFailure(type, null, false, connectionLost: true);
                loggedOn = null;
            }
            if (loggedOn != false)
            {
                try { logoff(); }
                catch (Exception error) when (reportConnectionLoss || !HmiReadPolicy.ConnectionUnavailable(error))
                {
                    return new SafetyLoginFailure(type, loggedOn, true, connectionLost: reportConnectionLoss && HmiReadPolicy.ConnectionUnavailable(error));
                }
            }
            return new SafetyLoginFailure(type, loggedOn, false);
        }
        catch (Exception loss) when (reportConnectionLoss)
        {
            return new SafetyLoginFailure(loss.GetType().FullName!, null, false, connectionLost: true);
        }
    }

    public static string Message(bool compiling, bool? loggedOn, bool logoffFailed, bool connectionLost = false)
    {
        string message = compiling ? CompileRefusal : "Safety offline login failed";
        if (connectionLost) return message + ". Session state is unknown because the safety connection was lost."
            + (logoffFailed ? " The cleanup logoff also failed." : "");
        if (loggedOn == false) return message;
        string reason = loggedOn == true ? "the session was logged on after the failure" : "the login state could not be read";
        return message + ". Session state is unknown because " + reason
            + (logoffFailed ? ". The cleanup logoff also failed." : ". A cleanup logoff was attempted.");
    }

    public InvalidOperationException CompileException() =>
        AttachTo(new InvalidOperationException(Message(true, LoggedOn, LogoffFailed, ConnectionLost)));

    // Exception.Data on .NET Framework accepts only serializable keys and values.
    public T AttachTo<T>(T error, bool mutationStarted = false) where T : Exception
    {
        error.Data[nameof(SafetyLoginFailure)] = NativeExceptionType;
        if (LoggedOn.HasValue) error.Data["loggedOnAfterFailure"] = LoggedOn.Value;
        error.Data["logoffFailed"] = LogoffFailed;
        if (ConnectionLost) error.Data["connectionLost"] = true;
        if (LoggedOn == false && !mutationStarted) error.Data["foundationKnownNoMutation"] = true;
        return error;
    }

    public static SafetyLoginFailure? FromException(Exception error) =>
        error.Data[nameof(SafetyLoginFailure)] is string type
            ? new SafetyLoginFailure(type, error.Data["loggedOnAfterFailure"] as bool?, error.Data["logoffFailed"] is true,
                error.Data["connectionLost"] is true) : null;

    public void RecordEvidence(IDictionary<string, object?> evidence, bool compiling, string typeField = "nativeExceptionType")
    {
        evidence["phase"] = Phase;
        evidence[typeField] = NativeExceptionType;
        evidence["loggedOnAfterFailure"] = LoggedOn;
        evidence["logoffFailed"] = LogoffFailed;
        if (compiling) evidence["compileStarted"] = false;
        if (ConnectionLost) evidence["connectionLost"] = true;
    }

    public string RecordLoginResult(IDictionary<string, object?> meta)
    {
        RecordEvidence(meta, compiling: false);
        bool unknown = LoggedOn != false;
        meta["mayHaveChanged"] = unknown;
        meta["writeOutcomeUnknown"] = unknown;
        meta["operationSuccess"] = false;
        meta["apiCallSuccess"] = false;
        meta["status"] = unknown ? "ReadOrWriteFailed" : "AccessDenied";
        return Message(false, LoggedOn, LogoffFailed);
    }
}
