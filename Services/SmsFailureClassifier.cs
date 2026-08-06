using Windows.Devices.Sms;

namespace SuperDoc.Sms.Services;

/// <summary>
/// Decides whether a rejected send is worth trying again.
/// </summary>
/// <remarks>
/// The modem reports <see cref="SmsSendMessageResult.IsErrorTransient"/>, and where it has
/// actually identified the failure that judgement is the best available. But
/// <see cref="SmsModemErrorCode.Other"/> means precisely that it could not identify it, and the
/// transient flag alongside it is a default rather than a finding. Trusting it there turns any
/// unexplained hiccup - a moment of congestion, an SMSC that was briefly unreachable - into a
/// message marked Failed on the first attempt, with the retry budget untouched.
///
/// Observed on 2026-08-06: a message to a number that had been messaged successfully all week,
/// eight minutes after another send on the same modem succeeded, rejected once as
/// <c>modem=Other, network=0, transport=0, transient=False</c> and never retried.
/// </remarks>
public static class SmsFailureClassifier
{
    /// <summary>
    /// Classifies a failed send.
    /// </summary>
    /// <param name="modemError">What the modem said went wrong.</param>
    /// <param name="networkCauseCode">The GSM cause code, or 0 when the network gave none.</param>
    /// <param name="modemSaysTransient">The modem's own transient flag.</param>
    public static SmsSendOutcome Classify(
        SmsModemErrorCode modemError,
        int networkCauseCode,
        bool modemSaysTransient)
    {
        if (modemSaysTransient)
        {
            return SmsSendOutcome.TransientFailure;
        }

        // Unclassified by the modem and unexplained by the network: nothing here actually says
        // the message is unacceptable, so spend the retry budget rather than give up on the
        // first attempt. A genuinely bad message still fails at the end of it.
        if (modemError == SmsModemErrorCode.Other && networkCauseCode == 0)
        {
            return SmsSendOutcome.TransientFailure;
        }

        return SmsSendOutcome.PermanentFailure;
    }
}
