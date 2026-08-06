using SuperDoc.Sms.Services;
using Windows.Devices.Sms;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// Whether a rejected send is retried decides whether a message reaches its recipient at all,
/// and getting it wrong is invisible: the message simply sits there marked Failed.
/// </summary>
public class SmsFailureClassifierTests
{
    [Fact]
    public void AnUnexplainedRejectionIsRetried()
    {
        // The case that prompted this: the modem classified nothing, the network gave no cause,
        // and the message was dropped on the first attempt with the retry budget untouched.
        var outcome = SmsFailureClassifier.Classify(
            SmsModemErrorCode.Other, networkCauseCode: 0, modemSaysTransient: false);

        Assert.Equal(SmsSendOutcome.TransientFailure, outcome);
    }

    [Fact]
    public void ARejectionTheModemCallsTransientIsRetried()
    {
        var outcome = SmsFailureClassifier.Classify(
            SmsModemErrorCode.NetworkNotReady, networkCauseCode: 0, modemSaysTransient: true);

        Assert.Equal(SmsSendOutcome.TransientFailure, outcome);
    }

    [Theory]
    [InlineData(SmsModemErrorCode.MessageNotEncodedProperly)]
    [InlineData(SmsModemErrorCode.MessageTooLarge)]
    [InlineData(SmsModemErrorCode.InvalidSmscAddress)]
    [InlineData(SmsModemErrorCode.FixedDialingNumberRestricted)]
    [InlineData(SmsModemErrorCode.SmsOperationNotSupportedByDevice)]
    public void AnIdentifiedPermanentRejectionIsNotRetried(SmsModemErrorCode code)
    {
        // Retrying a malformed message five times over two and a half minutes only delays the
        // inevitable, and the user waits for an answer that was already decided.
        var outcome = SmsFailureClassifier.Classify(code, networkCauseCode: 0, modemSaysTransient: false);

        Assert.Equal(SmsSendOutcome.PermanentFailure, outcome);
    }

    [Fact]
    public void AnUnclassifiedRejectionWithANetworkCauseIsNotRetried()
    {
        // "Other" plus a real GSM cause code is not unexplained: the network refused it and said
        // why, so the modem's permanent verdict stands.
        var outcome = SmsFailureClassifier.Classify(
            SmsModemErrorCode.Other, networkCauseCode: 21, modemSaysTransient: false);

        Assert.Equal(SmsSendOutcome.PermanentFailure, outcome);
    }
}
