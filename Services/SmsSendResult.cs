namespace SuperDoc.Sms.Services;

/// <summary>
/// How a send attempt ended, so the queue can react differently to "the radio is not up yet"
/// and "the network refused this message".
/// </summary>
public enum SmsSendOutcome
{
    /// <summary>Accepted by the modem.</summary>
    Sent,

    /// <summary>
    /// The modem exists but cannot send right now - still registering on the network, powered
    /// off, or SIM-locked. This is not the message's fault, so it must not consume a retry:
    /// the L850-GL has no 2G fallback (UMTS/LTE only), and after a cold boot it can take tens of
    /// seconds to attach. Counting that as failures would exhaust the retry budget and mark a
    /// perfectly good message Failed before the radio ever came up.
    /// </summary>
    NotReady,

    /// <summary>A real send attempt that failed for a reason likely to clear on its own.</summary>
    TransientFailure,

    /// <summary>
    /// Rejected in a way that will not change - a malformed recipient, or a message the network
    /// refuses. Retrying five times over two and a half minutes only delays the inevitable.
    /// </summary>
    PermanentFailure
}

public readonly record struct SmsSendResult(SmsSendOutcome Outcome, string? Error)
{
    public static SmsSendResult Sent() => new(SmsSendOutcome.Sent, null);

    public static SmsSendResult NotReady(string reason) => new(SmsSendOutcome.NotReady, reason);

    public static SmsSendResult Transient(string error) => new(SmsSendOutcome.TransientFailure, error);

    public static SmsSendResult Permanent(string error) => new(SmsSendOutcome.PermanentFailure, error);
}
