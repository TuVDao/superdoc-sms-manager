using SuperDoc.Sms.Services;
using Xunit;

namespace SuperDoc.Sms.Tests;

public class CarrierBalanceTests
{
    [Theory]
    [InlineData("251", null, "*804#")]                // Default Ethiopia -> Ethio Telecom
    [InlineData("251", "0911234567", "*804#")]        // Ethio Telecom mobile
    [InlineData("251", "+251911000010", "*804#")]     // Ethio Telecom SMSC
    [InlineData("251", "0712345678", "*704#")]        // Safaricom Ethiopia mobile
    [InlineData("251", "+251712345678", "*704#")]     // Safaricom Ethiopia international
    [InlineData("84", null, "*101#")]                  // Vietnam
    [InlineData("1", null, "")]                        // Unknown
    public void DefaultCodeMatchesCountryAndCarrier(string country, string? hint, string expected)
    {
        var code = CarrierBalance.DefaultCodeForCountry(country, hint);
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("0712345678", true)]
    [InlineData("+251712345678", true)]
    [InlineData("712345678", true)]
    [InlineData("0911234567", false)]
    [InlineData("+251911234567", false)]
    public void SafaricomDetectionIdentifiesPrefix(string number, bool expected)
    {
        Assert.Equal(expected, CarrierBalance.IsSafaricomEthiopia(number));
    }

    [Theory]
    [InlineData("0911234567", true)]
    [InlineData("+251911234567", true)]
    [InlineData("911234567", true)]
    [InlineData("0712345678", false)]
    [InlineData("+251712345678", false)]
    public void EthioTelecomDetectionIdentifiesPrefix(string number, bool expected)
    {
        Assert.Equal(expected, CarrierBalance.IsEthioTelecom(number));
    }

    [Theory]
    [InlineData("*804#", true)]                        // Ethio Telecom balance
    [InlineData("*704#", true)]                        // Safaricom balance
    [InlineData("*999#", true)]                        // Ethio Telecom services
    [InlineData("*777#", true)]                        // Safaricom services
    [InlineData("*805*1234567890123456#", true)]       // Voucher recharge
    [InlineData("*705*1234567890123456#", true)]       // Safaricom voucher recharge
    [InlineData("804", false)]                         // Missing * and #
    [InlineData("*804", false)]                        // Missing trailing #
    [InlineData("804#", false)]                        // Missing leading *
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UssdCodesAreValidatedStrictly(string? code, bool expected)
    {
        Assert.Equal(expected, CarrierBalance.IsPlausibleCode(code));
    }

    [Fact]
    public void EthiopianPresetsIncludeBothEthioTelecomAndSafaricom()
    {
        var presets = CarrierBalance.GetPresetsForCountry("251");
        Assert.Contains(presets, p => p.Code == "*804#");
        Assert.Contains(presets, p => p.Code == "*704#");
        Assert.Contains(presets, p => p.Code == "*999#");
        Assert.Contains(presets, p => p.Code == "*777#");
    }
}
